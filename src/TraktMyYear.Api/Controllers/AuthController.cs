using Microsoft.AspNetCore.Mvc;
using TraktMyYear.Application;

namespace TraktMyYear.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IAuthenticationService authenticationService,
    ITokenStore tokenStore) : ControllerBase
{
    [HttpGet("trakt/login")]
    public IActionResult Login()
    {
        var authorization = authenticationService.BeginAuthorization();
        return Redirect(authorization.AuthorizationUrl);
    }

    [HttpGet("trakt/callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Trakt authorization was not completed.",
                Detail = error,
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The Trakt callback is incomplete.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            await authenticationService.CompleteAuthorizationAsync(
                new TraktAuthorizationCallback(code, state),
                cancellationToken);
            return Ok(new { connected = true });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "The Trakt callback could not be accepted.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (HttpRequestException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Trakt rejected the authorization code.",
                Detail = exception.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { connected = tokenStore.HasToken });

    [HttpDelete("trakt")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await authenticationService.DisconnectAsync(cancellationToken);
        return NoContent();
    }
}