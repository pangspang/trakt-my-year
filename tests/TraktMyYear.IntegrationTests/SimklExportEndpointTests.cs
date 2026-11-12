using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TraktMyYear.Api.Controllers;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;

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
        Assert.Equal("Example", Assert.Single(movies.Items).Title);
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

        using var refreshed = await host.Client.PostAsync("/api/v1/year-reviews/2026/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var refreshedOverview = await refreshed.Content.ReadFromJsonAsync<YearReviewOverview>();
        Assert.NotNull(refreshedOverview);
        Assert.Equal(overview.TotalTitleCount, refreshedOverview.TotalTitleCount);
        Assert.True(host.Handler.RequestCount > requestCount);
    }

    [Fact]
    public async Task AuthenticationRoutesPreserveStatusPkceAndCallbackValidation()
    {
        await using var host = await TestHost.StartAsync();
        using var status = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/auth/status"));
        Assert.False(status.RootElement.GetProperty("connected").GetBoolean());

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
        using var disconnect = await host.Client.DeleteAsync("/api/v1/auth/trakt");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        Assert.Equal(0, host.Handler.RequestCount);
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
        Assert.True(paths.GetProperty("/api/v1/year-reviews/{year}").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/v1/year-reviews/{year}/refresh").TryGetProperty("post", out _));
        var responses = paths.GetProperty("/api/v1/year-reviews/{year}/{mediaType}/export.csv")
            .GetProperty("get").GetProperty("responses");
        Assert.True(responses.TryGetProperty("200", out _));
        Assert.True(responses.TryGetProperty("400", out _));
        Assert.True(responses.TryGetProperty("409", out _));
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

    private sealed class TestHost(WebApplication app, HttpClient client, FixtureHandler handler) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public FixtureHandler Handler { get; } = handler;

        public static async Task<TestHost> StartAsync(bool fail = false, bool large = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddControllers().AddApplicationPart(typeof(YearReviewsController).Assembly);
            builder.Services.AddOpenApi();
            builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
            builder.Services.AddInfrastructureServices(builder.Configuration);
            builder.Services.PostConfigure<TraktOptions>(options =>
            {
                options.ClientId = "fake-client-id";
                options.ClientSecret = string.Empty;
                options.CallbackUrl = "http://localhost/callback";
                options.ApiBaseUrl = "https://api.example.test";
                options.Username = "me";
                options.Timezone = "UTC";
            });
            var handler = new FixtureHandler(fail, large);
            builder.Services.AddHttpClient<TraktGateway>().ConfigurePrimaryHttpMessageHandler(() => handler);
            var app = builder.Build();
            app.MapControllers();
            app.MapOpenApi();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TestHost(app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri(address)
            }, handler);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FixtureHandler(bool fail, bool large) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("private-upstream-data")
                });
            }

            var path = request.RequestUri!.AbsolutePath;
            var title = large ? new string('x', 50_000) : "Example";
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