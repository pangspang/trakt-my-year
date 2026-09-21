using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using TraktMyYear.Application;

namespace TraktMyYear.Infrastructure;

public sealed class TraktOptions
{
    public const string SectionName = "Trakt";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackUrl { get; set; } = string.Empty;
    public string Timezone { get; set; } = "UTC";
    public string ApiBaseUrl { get; set; } = "https://api.trakt.tv";
    public string Username { get; set; } = "me";
    public string AuthorizationEndpoint { get; set; } = "https://auth.trakt.tv/oauth/authorize";
    public string TokenEndpoint { get; set; } = "https://auth.trakt.tv/oauth/token";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("A Trakt client ID is required.");
        }

        if (!Uri.TryCreate(CallbackUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("A valid Trakt callback URL is required.");
        }
    }
}

public sealed class InMemoryOAuthStateStore : IOAuthStateStore
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, string CodeVerifier)> states = new();

    public OAuthState Create()
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        states[state] = (DateTimeOffset.UtcNow.Add(StateLifetime), verifier);
        return new OAuthState(state, verifier);
    }

    public bool TryConsume(string state, out string codeVerifier)
    {
        codeVerifier = string.Empty;
        if (!states.TryRemove(state, out var value) || value.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        codeVerifier = value.CodeVerifier;
        return true;
    }
}

public sealed class InMemoryTokenStore : ITokenStore
{
    private TraktToken? token;

    public bool HasToken => token is not null;

    public Task SaveAsync(TraktToken value, CancellationToken cancellationToken = default)
    {
        token = value;
        return Task.CompletedTask;
    }

    public Task<TraktToken?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(token);

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        token = null;
        return Task.CompletedTask;
    }
}

public sealed class ConfiguredYearBoundaryProvider(IOptions<TraktOptions> options) : IYearBoundaryProvider
{
    public (DateTimeOffset Start, DateTimeOffset End) GetBounds(int year)
    {
        var timezone = ResolveTimezone(options.Value.Timezone);
        var localStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddYears(1);
        return (
            new DateTimeOffset(localStart, timezone.GetUtcOffset(localStart)).ToUniversalTime(),
            new DateTimeOffset(localEnd, timezone.GetUtcOffset(localEnd)).ToUniversalTime());
    }

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

public sealed class TraktOAuthClient(
    HttpClient httpClient,
    IOptions<TraktOptions> options) : ITraktOAuthClient
{
    private readonly TraktOptions settings = options.Value;

    public string BuildAuthorizationUrl(string state, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = settings.CallbackUrl,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        var queryString = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return $"{settings.AuthorizationEndpoint}?{queryString}";
    }

    public async Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            settings.TokenEndpoint,
            new
            {
                code,
                client_id = settings.ClientId,
                redirect_uri = settings.CallbackUrl,
                code_verifier = codeVerifier,
                grant_type = "authorization_code"
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<OAuthError>(cancellationToken);
            throw new HttpRequestException(
                $"Trakt token exchange failed with status {(int)response.StatusCode}: " +
                $"{error?.Error ?? "unknown_error"} - {error?.Description ?? "No description returned."}");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken) || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            throw new InvalidOperationException("Trakt returned an incomplete token response.");
        }

        return new TraktToken(
            payload.AccessToken,
            payload.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
            payload.TokenType ?? "bearer");
    }

    public async Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            settings.TokenEndpoint,
            new
            {
                refresh_token = refreshToken,
                client_id = settings.ClientId,
                redirect_uri = settings.CallbackUrl,
                grant_type = "refresh_token"
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<OAuthError>(cancellationToken);
            throw new HttpRequestException(
                $"Trakt token refresh failed with status {(int)response.StatusCode}: " +
                $"{error?.Error ?? "unknown_error"} - {error?.Description ?? "No description returned."}");
        }

        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken) || string.IsNullOrWhiteSpace(payload.RefreshToken))
        {
            throw new InvalidOperationException("Trakt returned an incomplete refreshed token response.");
        }

        return new TraktToken(
            payload.AccessToken,
            payload.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
            payload.TokenType ?? "bearer");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string? TokenType);

    private sealed record OAuthError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}

public static class DotEnvFile
{
    public static void Load(string contentRootPath)
    {
        var directory = new DirectoryInfo(contentRootPath);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, ".env");
            if (File.Exists(path))
            {
                LoadFile(path);
                return;
            }

            directory = directory.Parent;
        }
    }

    private static void LoadFile(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<TraktOptions>()
            .Configure(options =>
            {
                options.ClientId = Environment.GetEnvironmentVariable("TRAKT_CLIENT_ID") ?? string.Empty;
                options.ClientSecret = Environment.GetEnvironmentVariable("TRAKT_CLIENT_SECRET") ?? string.Empty;
                options.CallbackUrl = Environment.GetEnvironmentVariable("TRAKT_CALLBACK_URL") ?? string.Empty;
                options.Timezone = Environment.GetEnvironmentVariable("TRAKT_TIMEZONE") ?? "UTC";
                options.ApiBaseUrl = Environment.GetEnvironmentVariable("TRAKT_API_BASE_URL") ?? "https://api.trakt.tv";
                options.Username = Environment.GetEnvironmentVariable("TRAKT_TEST_ACCOUNT") ?? "me";
            })
            .Validate(options =>
            {
                options.Validate();
                return true;
            }, "Trakt configuration is invalid.")
            .ValidateOnStart();

        services.AddSingleton<IOAuthStateStore, InMemoryOAuthStateStore>();
        services.AddSingleton<ITokenStore, InMemoryTokenStore>();
        services.AddMemoryCache();
        services.AddSingleton<IYearBoundaryProvider, ConfiguredYearBoundaryProvider>();
        services.AddSingleton<IYearReviewStore, MemoryYearReviewStore>();
        services.AddSingleton<IYearReviewService, YearReviewService>();
        services.AddHttpClient<ITraktOAuthClient, TraktOAuthClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TraktOptions>>().Value;
            client.DefaultRequestHeaders.Add("trakt-api-version", "2");
            client.DefaultRequestHeaders.Add("trakt-api-key", options.ClientId);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TraktMyYear/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddHttpClient<ITraktGateway, TraktGateway>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TraktOptions>>().Value;
            client.BaseAddress = new Uri(options.ApiBaseUrl);
            client.DefaultRequestHeaders.Add("trakt-api-version", "2");
            client.DefaultRequestHeaders.Add("trakt-api-key", options.ClientId);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TraktMyYear/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}