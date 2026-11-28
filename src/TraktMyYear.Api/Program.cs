using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;
using System.Diagnostics;
using IAuthenticationService = TraktMyYear.Application.IAuthenticationService;
using AppAuthenticationService = TraktMyYear.Application.AuthenticationService;

static bool IsLoopbackHost(string host) =>
    host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

var builder = WebApplication.CreateBuilder(args);

DotEnvFile.Load(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IAuthenticationService, AppAuthenticationService>();
builder.Services.AddInfrastructureServices(builder.Configuration);
var allowInsecureLoopback = builder.Configuration.GetValue<bool>("TRAKT_ALLOW_INSECURE_LOOPBACK");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = builder.Environment.IsDevelopment() || allowInsecureLoopback
            ? "TraktMyYear.Session"
            : "__Host-TraktMyYear";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() && !allowInsecureLoopback
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnSigningIn = context =>
        {
            if (allowInsecureLoopback && IsLoopbackHost(context.Request.Host.Host) && !context.Request.IsHttps)
            {
                context.CookieOptions.Secure = false;
            }
            return Task.CompletedTask;
        };
        options.Events.OnSigningOut = context =>
        {
            if (allowInsecureLoopback && IsLoopbackHost(context.Request.Host.Host) && !context.Request.IsHttps)
            {
                context.CookieOptions.Secure = false;
            }
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = context =>
        {
            var sessionId = context.Principal?.FindFirstValue("app_session");
            var sessions = context.HttpContext.RequestServices.GetRequiredService<IApplicationSessionStore>();
            if (string.IsNullOrWhiteSpace(sessionId) || sessions.GetSession(sessionId) is null)
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return Task.CompletedTask;
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            return context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Title = "Authentication is required.",
                Status = StatusCodes.Status401Unauthorized,
                Instance = context.Request.Path
            });
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        });
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/year-reviews")
        && context.User.Identity?.IsAuthenticated == true)
    {
        var sessionId = context.User.FindFirstValue("app_session");
        var sessions = context.RequestServices.GetRequiredService<IApplicationSessionStore>();
        var session = sessionId is null ? null : sessions.GetSession(sessionId, touch: true);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Title = "The application session has expired.",
                Status = StatusCodes.Status401Unauthorized
            });
            return;
        }

        using var lifetimeRegistration = session.LifetimeToken.Register(context.Abort);
        await next();
        return;
    }

    await next();
});
app.UseAuthorization();
app.MapControllers();

if (app.Environment.IsDevelopment())
{
    var options = app.Services.GetRequiredService<IOptions<TraktOptions>>().Value;
    var callbackUri = new Uri(options.CallbackUrl);
    var loginUri = new UriBuilder(callbackUri)
    {
        Path = "/api/v1/auth/trakt/login",
        Query = string.Empty
    }.Uri;

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(loginUri.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not open the Trakt login browser: {exception.Message}");
        }
    });
}

app.Run();

public partial class Program;
