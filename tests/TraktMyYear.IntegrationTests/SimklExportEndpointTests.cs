using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TraktMyYear.Api.Controllers;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using TraktMyYear.Infrastructure.Authentication;
using IApplicationAuthenticationService = TraktMyYear.Application.IAuthenticationService;
using AppAuthenticationService = TraktMyYear.Application.AuthenticationService;

namespace TraktMyYear.IntegrationTests;

public sealed class SimklExportEndpointTests
{
    [Fact]
    public async Task YearReviewEndpointsShareCachedDataAndRefreshFromUpstream()
    {
        await using var host = await TestHost.StartAsync();
        var overview = await host.Client.GetFromJsonAsync<YearReviewOverview>("/api/v1/year-reviews/2026");
        Assert.NotNull(overview);
        Assert.Equal(2026, overview.Year);
        Assert.Equal(1, overview.MovieCount);
        Assert.Equal(0, overview.ShowCount);
        Assert.Equal(1, overview.TotalTitleCount);
        Assert.Equal(1, overview.WatchedTitleCount);
        Assert.Equal(0, overview.RatedTitleCount);
        var requestCount = host.Handler.RequestCount;

        var movies = await host.Client.GetFromJsonAsync<PagedYearReview>(
            "/api/v1/year-reviews/2026/movies?sort=watchedAt&direction=desc&page=1&pageSize=1");
        Assert.NotNull(movies);
        Assert.Equal(1, movies.TotalCount);
        Assert.Equal(1, movies.Page);
        Assert.Equal(1, movies.PageSize);
        Assert.Equal("Example account-a", Assert.Single(movies.Items).Title);
        Assert.Equal(overview.GeneratedAt, movies.GeneratedAt);

        var shows = await host.Client.GetFromJsonAsync<PagedYearReview>("/api/v1/year-reviews/2026/shows");
        Assert.NotNull(shows);
        Assert.Empty(shows.Items);
        var top = await host.Client.GetFromJsonAsync<TopYearReview>(
            "/api/v1/year-reviews/2026/movie/top?limit=5&minimumRating=8");
        Assert.NotNull(top);
        Assert.Equal(5, top.Limit);
        Assert.Equal(8m, top.MinimumRating);
        Assert.Empty(top.Items);
        Assert.Equal(requestCount, host.Handler.RequestCount);

        using var refreshed = await host.PostWithCsrfAsync(host.Client, "/api/v1/year-reviews/2026/refresh");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedOverview = await refreshed.Content.ReadFromJsonAsync<YearReviewOverview>();
        Assert.NotNull(refreshedOverview);
        Assert.Equal(overview.TotalTitleCount, refreshedOverview.TotalTitleCount);
        Assert.True(host.Handler.RequestCount > requestCount);
    }

    [Fact]
    public async Task RewindJsonExportsCompleteVersionedFileAndLeavesCsvUnchanged()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1/year-reviews/2026/movie/export.json?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("2026-films.json", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(response.Headers.CacheControl?.NoStore);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("personal-rewind", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2026, root.GetProperty("year").GetInt32());
        Assert.Equal("film", root.GetProperty("mediaType").GetString());
        Assert.Equal("UTC", root.GetProperty("timeZone").GetString());
        Assert.Equal(root.GetProperty("totalCount").GetInt32(), root.GetProperty("titles").GetArrayLength());
        var title = root.GetProperty("titles")[0];
        Assert.Equal("Example account-a", title.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, title.GetProperty("rating").ValueKind);
        Assert.Equal(JsonValueKind.Null, title.GetProperty("ratedAt").ValueKind);

        using var series = await host.Client.GetAsync("/api/v1/year-reviews/2026/show/export.json");
        Assert.Equal("2026-series.json", series.Content.Headers.ContentDisposition?.FileNameStar);
        using var seriesDocument = JsonDocument.Parse(await series.Content.ReadAsStringAsync());
        Assert.Equal("series", seriesDocument.RootElement.GetProperty("mediaType").GetString());
        Assert.StartsWith("mediaType,traktId,", await host.Client.GetStringAsync("/api/v1/year-reviews/2026/movie/export.csv"));
    }

    [Theory]
    [InlineData("2026/99/export.json")]
    [InlineData("2026/movie/export.json?sort=99")]
    [InlineData("1899/movie/export.json")]
    public async Task RewindJsonRejectsInvalidParameters(string route)
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.GetAsync($"/api/v1/year-reviews/{route}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AuthenticationRoutesPreserveStatusPkceAndCallbackValidation()
    {
        await using var host = await TestHost.StartAsync();
        using var anonymous = host.CreateClient();
        using var status = JsonDocument.Parse(await anonymous.GetStringAsync("/api/v1/auth/status"));
        Assert.False(status.RootElement.GetProperty("connected").GetBoolean());
        using var protectedResponse = await anonymous.GetAsync("/api/v1/year-reviews/2026");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);

        using var login = await host.Client.GetAsync("/api/v1/auth/trakt/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var location = Assert.IsType<Uri>(login.Headers.Location);
        Assert.Equal("auth.trakt.tv", location.Host);
        Assert.Contains("code_challenge_method=S256", location.Query);
        Assert.Contains("code_challenge=", location.Query);
        Assert.Contains("state=", location.Query);

        using var incomplete = await host.Client.GetAsync("/api/v1/auth/trakt/callback");
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        using var invalidState = await host.Client.GetAsync("/api/v1/auth/trakt/callback?code=fake-code&state=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalidState.StatusCode);
        using var disconnect = await host.DeleteWithCsrfAsync(host.Client, "/api/v1/auth/trakt");
        Assert.True(disconnect.StatusCode == HttpStatusCode.NoContent, await disconnect.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Handler.RequestCount);
    }

    [Fact]
    public async Task AccountsUseSeparateSessionsCachesAndExportData()
    {
        await using var host = await TestHost.StartAsync();
        using var otherClient = host.CreateClient();
        await host.LoginAsync(otherClient, "account-b");

        var first = await host.Client.GetFromJsonAsync<YearReviewOverview>("/api/v1/year-reviews/2026");
        var second = await otherClient.GetFromJsonAsync<YearReviewOverview>("/api/v1/year-reviews/2026");
        Assert.NotNull(first);
        Assert.NotNull(second);

        var firstMovies = await host.Client.GetFromJsonAsync<PagedYearReview>("/api/v1/year-reviews/2026/movies");
        var secondMovies = await otherClient.GetFromJsonAsync<PagedYearReview>("/api/v1/year-reviews/2026/movies");
        Assert.Equal("Example account-a", Assert.Single(firstMovies!.Items).Title);
        Assert.Equal("Example account-b", Assert.Single(secondMovies!.Items).Title);

        var firstCsv = await host.Client.GetStringAsync("/api/v1/year-reviews/2026/movie/export.csv");
        var secondJson = await otherClient.GetStringAsync("/api/v1/year-reviews/2026/movie/export.json");
        Assert.Contains("Example account-a", firstCsv);
        Assert.Contains("Example account-b", secondJson);

        var csrf = await host.GetCsrfTokenAsync(host.Client);
        using var disconnect = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/trakt");
        disconnect.Headers.Add("X-CSRF-TOKEN", csrf);
        using var response = await host.Client.SendAsync(disconnect);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await otherClient.GetAsync("/api/v1/year-reviews/2026")).StatusCode);
        using var disconnectedStatus = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/auth/status"));
        Assert.False(disconnectedStatus.RootElement.GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task WrongBrowserOAuthCallbackDoesNotConsumeInitiatingBrowserState()
    {
        await using var host = await TestHost.StartAsync();
        using var browserA = host.CreateClient();
        using var browserB = host.CreateClient();
        var callbackA = await host.BeginLoginAsync(browserA);
        using var wrongBrowser = await browserB.GetAsync(callbackA);
        Assert.Equal(HttpStatusCode.BadRequest, wrongBrowser.StatusCode);

        using var validCallback = await browserA.GetAsync(callbackA);
        Assert.Equal(HttpStatusCode.Redirect, validCallback.StatusCode);
        Assert.True((await browserA.GetFromJsonAsync<JsonElement>("/api/v1/auth/status")).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task SameAccountReauthorizationInvalidatesPreviousSessionCsrfToken()
    {
        await using var host = await TestHost.StartAsync();
        await host.LoginAsync(host.Client, "account-a");
        var oldCsrf = await host.GetCsrfTokenAsync(host.Client);
        await host.LoginAsync(host.Client, "account-a");

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/auth/trakt");
        request.Headers.Add("X-CSRF-TOKEN", oldCsrf);
        using var rejected = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);

        using var disconnect = await host.DeleteWithCsrfAsync(host.Client, "/api/v1/auth/trakt");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
    }

    [Fact]
    public async Task PendingOAuthCallbackCannotRestoreAnAccountDisconnectedDuringAuthorization()
    {
        await using var host = await TestHost.StartAsync();
        await host.LoginAsync(host.Client, "account-a");
        using var otherSession = host.CreateClient();
        await host.LoginAsync(otherSession, "account-a");
        var pendingCallback = await host.BeginLoginAsync(host.Client, "account-b");

        using var disconnect = await host.DeleteWithCsrfAsync(otherSession, "/api/v1/auth/trakt");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        using var callback = await host.Client.GetAsync(pendingCallback);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/v1/year-reviews/2026")).StatusCode);
    }

    [Fact]
    public async Task OpenApiDocumentDescribesAuthenticationReviewsAndExports()
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/v1/auth/status").TryGetProperty("get", out _));
        var disconnectResponses = paths.GetProperty("/api/v1/auth/trakt")
            .GetProperty("delete").GetProperty("responses");
        Assert.True(disconnectResponses.TryGetProperty("204", out _));
        Assert.True(disconnectResponses.TryGetProperty("400", out _));
        Assert.True(disconnectResponses.TryGetProperty("401", out _));
        Assert.True(paths.GetProperty("/api/v1/year-reviews/{year}").TryGetProperty("get", out _));
        var refreshResponses = paths.GetProperty("/api/v1/year-reviews/{year}/refresh")
            .GetProperty("post").GetProperty("responses");
        Assert.True(refreshResponses.TryGetProperty("400", out _));
        Assert.True(refreshResponses.TryGetProperty("401", out _));
        Assert.True(refreshResponses.TryGetProperty("429", out _));
        var overviewResponses = paths.GetProperty("/api/v1/year-reviews/{year}")
            .GetProperty("get").GetProperty("responses");
        Assert.True(overviewResponses.TryGetProperty("429", out _));
        var responses = paths.GetProperty("/api/v1/year-reviews/{year}/{mediaType}/export.csv")
            .GetProperty("get").GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.True(responses.TryGetProperty("401", out _));
        Assert.True(responses.TryGetProperty("429", out _));
        Assert.True(responses.TryGetProperty("502", out _));
    }

    [Fact]
    public async Task SimklDownloadsAllTitlesAndLeavesDefaultCsvUnchanged()
    {
        await using var host = await TestHost.StartAsync();
        using var simkl = await host.Client.GetAsync("/api/v1/year-reviews/2026/movie/export.csv?format=simkl&pageSize=1");
        var text = await simkl.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, simkl.StatusCode);
        Assert.Equal("text/csv", simkl.Content.Headers.ContentType?.MediaType);
        Assert.Equal("trakt-my-year-2026-movie-simkl.csv", simkl.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(simkl.Headers.CacheControl?.NoStore);
        Assert.StartsWith(SimklCsv.Header, text);
        Assert.Equal(121, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("tt0000001", text);

        using var original = await host.Client.GetAsync("/api/v1/year-reviews/2026/movie/export.csv");
        var originalText = await original.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.StartsWith("mediaType,traktId,title,year,watchedAt,ratedAt,personalRating,posterUrl,traktUrl\r\n", originalText);
        Assert.DoesNotContain("SIMKL_ID", originalText);
        Assert.Equal(originalText, await host.Client.GetStringAsync("/api/v1/year-reviews/2026/movie/export.csv?format=csv"));
    }

    [Theory]
    [InlineData("2026/movie/export.csv?format=invalid")]
    [InlineData("1899/movie/export.csv?format=simkl")]
    [InlineData("2026/99/export.csv?format=simkl")]
    [InlineData("2026/episode/export.csv?format=simkl")]
    [InlineData("2026/movie/export.csv?format=simkl&sort=99")]
    [InlineData("2026/movie/export.csv?format=simkl&direction=99")]
    public async Task InvalidParametersReturnProblemDetails(string route)
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.GetAsync($"/api/v1/year-reviews/{route}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, host.Handler.RequestCount);
    }

    [Fact]
    public async Task ShowsAndEmptyExportsHaveSafeOptionalFields()
    {
        await using var host = await TestHost.StartAsync();
        var text = await host.Client.GetStringAsync("/api/v1/year-reviews/2026/show/export.csv?format=simkl");
        Assert.StartsWith(SimklCsv.Header, text);
        Assert.Contains("\"tv\",,\"62\"", text);
        Assert.DoesNotContain("completed", text);
        Assert.Contains("\"S01E05\"", text);

        Assert.Equal(SimklCsv.Header, await host.Client.GetStringAsync("/api/v1/year-reviews/2024/movie/export.csv?format=simkl"));
    }

    [Fact]
    public async Task UpstreamFailuresDoNotLeakResponseOrExceptionDetails()
    {
        await using var host = await TestHost.StartAsync(fail: true);
        using var response = await host.Client.GetAsync("/api/v1/year-reviews/2026/movie/export.csv?format=simkl");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-upstream-data", body);
        Assert.DoesNotContain("fake-client-id", body);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TraktRateLimitReturnsActionableTooManyRequestsProblem()
    {
        await using var host = await TestHost.StartAsync(
            fail: true,
            upstreamFailureStatus: (HttpStatusCode)420);
        using var response = await host.Client.GetAsync("/api/v1/year-reviews/2026");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Trakt rate limit exceeded.", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Wait a little", problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OversizedExportDownloadsIndependentlyImportableZipParts()
    {
        await using var host = await TestHost.StartAsync(large: true);
        using var response = await host.Client.GetAsync("/api/v1/year-reviews/2026/movie/export.csv?format=simkl");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var archive = new ZipArchive(stream);
        Assert.True(archive.Entries.Count > 1);
        var rowCount = 0;
        foreach (var entry in archive.Entries)
        {
            Assert.EndsWith(".csv", entry.FullName);
            Assert.InRange(entry.Length, 1, SimklCsv.MaximumFileBytes);
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.StartsWith(SimklCsv.Header, text);
            rowCount += text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
        }

        Assert.Equal(120, rowCount);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, FixtureHandler handler, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public FixtureHandler Handler { get; } = handler;

        public HttpClient CreateClient() => CreateHttpClient(address);

        public async Task LoginAsync(HttpClient target, string code)
        {
            var callback = await BeginLoginAsync(target, code);
            using var response = await target.GetAsync(callback);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        public async Task<string> BeginLoginAsync(HttpClient target, string code = "account-a")
        {
            using var login = await target.GetAsync("/api/v1/auth/trakt/login");
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var query = new Uri(login.Headers.Location!.ToString()).Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
            return $"/api/v1/auth/trakt/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(query["state"])}";
        }

        public async Task<string> GetCsrfTokenAsync(HttpClient target)
        {
            var token = await target.GetFromJsonAsync<CsrfTokenResponse>("/api/v1/auth/csrf");
            return token!.RequestToken;
        }

        public async Task<HttpResponseMessage> PostWithCsrfAsync(HttpClient target, string path)
        {
            var csrf = await GetCsrfTokenAsync(target);
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            return await target.SendAsync(request);
        }

        public async Task<HttpResponseMessage> DeleteWithCsrfAsync(HttpClient target, string path)
        {
            var csrf = await GetCsrfTokenAsync(target);
            using var request = new HttpRequestMessage(HttpMethod.Delete, path);
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            return await target.SendAsync(request);
        }

        public static async Task<TestHost> StartAsync(
            bool fail = false,
            bool large = false,
            HttpStatusCode upstreamFailureStatus = HttpStatusCode.BadGateway)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllers().AddApplicationPart(typeof(YearReviewsController).Assembly);
            builder.Services.AddOpenApi();
            builder.Services.AddSingleton<IApplicationAuthenticationService, AppAuthenticationService>();
            builder.Services.AddInfrastructureServices(builder.Configuration);
            builder.Services.RemoveAll<ITraktOAuthClient>();
            builder.Services.AddSingleton<ITraktOAuthClient, FakeOAuthClient>();
            builder.Services.PostConfigure<TraktOptions>(options =>
            {
                options.ClientId = "fake-client-id";
                options.CallbackUrl = "http://localhost/callback";
                options.ApiBaseUrl = "https://api.example.test";
                options.Timezone = "UTC";
            });
            var handler = new FixtureHandler(fail, large, upstreamFailureStatus);
            builder.Services.AddHttpClient<TraktGateway>().ConfigurePrimaryHttpMessageHandler(() => handler);
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.Name = "TraktMyYear.Session";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.Path = "/";
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = false;
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
                            Status = StatusCodes.Status401Unauthorized
                        });
                    };
                });
            builder.Services.AddAuthorization();
            builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1"))
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers.CacheControl = "no-store";
                        return Task.CompletedTask;
                    });
                }
                await next();
            });
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1/year-reviews")
                    && context.User.Identity?.IsAuthenticated == true)
                {
                    var sessionId = context.User.FindFirstValue("app_session");
                    var store = context.RequestServices.GetRequiredService<IApplicationSessionStore>();
                    var session = sessionId is null ? null : store.GetSession(sessionId, touch: true);
                    if (session is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    using var registration = session.LifetimeToken.Register(context.Abort);
                    await next();
                    return;
                }
                await next();
            });
            app.UseAuthorization();
            app.MapControllers();
            app.MapOpenApi();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var host = new TestHost(app, CreateHttpClient(new Uri(address)), handler, new Uri(address));
            await host.LoginAsync(host.Client, "account-a");
            return host;
        }

        private static HttpClient CreateHttpClient(Uri address) =>
            new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
            {
                BaseAddress = address
            };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeOAuthClient : ITraktOAuthClient
    {
        public string BuildAuthorizationUrl(string state, string codeChallenge) =>
            $"https://auth.trakt.tv/oauth/authorize?state={Uri.EscapeDataString(state)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";

        public Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktToken($"access-{code}", $"refresh-{code}", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktToken($"refreshed-{refreshToken}", $"rotated-{refreshToken}", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<TraktAccount> GetAccountAsync(TraktToken token, CancellationToken cancellationToken = default)
        {
            var accountId = new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(token.AccessToken))).ToString("D");
            return Task.FromResult(new TraktAccount(accountId, token.AccessToken["access-".Length..]));
        }
    }

    private sealed class FixtureHandler(bool fail, bool large, HttpStatusCode upstreamFailureStatus) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (fail)
            {
                return Task.FromResult(new HttpResponseMessage(upstreamFailureStatus)
                {
                    Content = new StringContent("private-upstream-data")
                });
            }

            var path = request.RequestUri!.AbsolutePath;
            var account = request.Headers.Authorization?.Parameter?.Replace("access-", string.Empty, StringComparison.Ordinal)
                .Replace("refreshed-refresh-account-a", "account-a", StringComparison.Ordinal)
                .Replace("refreshed-refresh-account-b", "account-b", StringComparison.Ordinal) ?? "anonymous";
            var title = large ? new string('x', 50_000) : $"Example {account}";
            var movie = new { title, year = 2000, ids = new { trakt = 1, imdb = "tt0000001", tmdb = 52 } };
            var show = new { title = "Example show", ids = new { trakt = 42, tvdb = 62 } };
            var body = "[]";
            if (path.EndsWith("history/movies") && request.RequestUri.Query.Contains("2026-"))
            {
                body = JsonSerializer.Serialize(Enumerable.Range(1, 120).Select(id => new
                {
                    type = "movie",
                    watched_at = "2026-01-02T10:00:00Z",
                    movie = new { title = $"{title} {id}", year = 2000, ids = new { trakt = id, imdb = $"tt{id:D7}", tmdb = id } }
                }));
            }
            else if (path.EndsWith("history/shows"))
            {
                body = JsonSerializer.Serialize(new[] { new { type = "episode", watched_at = "2026-01-02T10:00:00Z", episode = new { season = 1, number = 5 }, show } });
            }
            else if (path.EndsWith("watched/movies"))
            {
                body = JsonSerializer.Serialize(new[] { new { last_watched_at = "2026-01-02T10:00:00Z", movie } });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Pagination-Page-Count", "1");
            return Task.FromResult(response);
        }
    }
}