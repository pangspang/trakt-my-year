using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using Microsoft.Extensions.Options;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

DotEnvFile.Load(builder.Environment.ContentRootPath);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
builder.Services.AddInfrastructureServices(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();
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
