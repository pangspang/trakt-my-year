using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using IAuthenticationService = TraktMyYear.Application.IAuthenticationService;

namespace TraktMyYear.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IAuthenticationService authenticationService,
    IApplicationSessionStore sessionStore,
    IAntiforgery antiforgery,
    IWebHostEnvironment environment,
    IOptions<TraktOptions> options) : ControllerBase
{
    private const string BrowserCookie = "TraktMyYear.OAuthBrowser";
    private const string SessionClaim = "app_session";
    private const string AccountIdClaim = "account_id";
    private const string AccountNameClaim = "account_name";

    [AllowAnonymous]
    [HttpGet("trakt/login")]
    public IActionResult Login()
    {
        var browserId = Request.Cookies.TryGetValue(BrowserCookie, out var existingBrowserId)
            ? existingBrowserId
            : null;
        if (string.IsNullOrWhiteSpace(browserId))
        {
            browserId = RandomId();
        }

        Response.Cookies.Append(BrowserCookie, browserId, new CookieOptions
        {
            HttpOnly = true,
            Secure = SecureCookies,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth/trakt",
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true
        });

        var originatingSessionId = User.FindFirstValue(SessionClaim);
        if (originatingSessionId is not null && sessionStore.GetSession(originatingSessionId, touch: true) is null)
        {
            originatingSessionId = null;
        }

        var authorization = authenticationService.BeginAuthorization(browserId, originatingSessionId);
        return Redirect(authorization.AuthorizationUrl);
    }

    [AllowAnonymous]
    [HttpGet("trakt/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Trakt authorization could not be completed.",
                Detail = "The callback is incomplete or was rejected. Start login again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var browserId = Request.Cookies.TryGetValue(BrowserCookie, out var existingBrowserId)
            ? existingBrowserId
            : null;
        if (string.IsNullOrWhiteSpace(browserId))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Trakt authorization could not be completed.",
                Detail = "The login attempt is not bound to this browser. Start login again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var oldSessionId = User.FindFirstValue(SessionClaim);
            var login = await authenticationService.CompleteAuthorizationAsync(
                new TraktAuthorizationCallback(code, state),
                browserId,
                oldSessionId,
                cancellationToken);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                CreatePrincipal(login),
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });

            Response.Cookies.Delete(BrowserCookie, new CookieOptions
            {
                Path = "/api/v1/auth/trakt",
                Secure = SecureCookies,
                SameSite = SameSiteMode.Lax,
                HttpOnly = true
            });
            return Redirect("/");
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Trakt authorization could not be completed.",
                Detail = "The login attempt expired or Trakt did not return a valid account. Start login again.",
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Trakt authorization could not be completed.",
                Detail = "Trakt could not complete the login request. Try again later.",
                Status = StatusCodes.Status502BadGateway
            });
        }
    }

    [AllowAnonymous]
    [HttpGet("status")]
    public IActionResult Status()
    {
        Response.Headers.CacheControl = "no-store";
        var accountId = User.FindFirstValue(AccountIdClaim);
        var displayName = User.FindFirstValue(AccountNameClaim);
        var sessionId = User.FindFirstValue(SessionClaim);
        if (accountId is null || displayName is null || sessionId is null
            || sessionStore.GetSession(sessionId) is null)
        {
            return Ok(new { connected = false, account = (string?)null });
        }

        return Ok(new { connected = true, account = displayName });
    }

    /// <summary>Returns a request token for same-origin cookie-authenticated mutations.</summary>
    [Authorize]
    [HttpGet("csrf")]
    [ProducesResponseType(typeof(CsrfTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public IActionResult Csrf()
    {
        Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfTokenResponse(tokens.RequestToken!));
    }

    [Authorize]
    [HttpDelete("trakt")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!await antiforgery.IsRequestValidAsync(HttpContext))
        {
            return Problem(title: "The anti-forgery token is missing or invalid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var accountId = User.FindFirstValue(AccountIdClaim);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Unauthorized();
        }

        await authenticationService.DisconnectAsync(accountId, cancellationToken);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    private static ClaimsPrincipal CreatePrincipal(AuthenticatedLogin login)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, login.SessionId),
            new Claim(AccountIdClaim, login.AccountId),
            new Claim(AccountNameClaim, login.DisplayName),
            new Claim(SessionClaim, login.SessionId)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private static string RandomId() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private bool SecureCookies
    {
        get
        {
            if (Request.IsHttps)
            {
                return true;
            }

            var host = Request.Host.Host;
            var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
            return !environment.IsDevelopment() && !(options.Value.AllowInsecureLoopback && loopback);
        }
    }
}

/// <summary>Anti-forgery request token for a browser session.</summary>
public sealed record CsrfTokenResponse(string RequestToken);
