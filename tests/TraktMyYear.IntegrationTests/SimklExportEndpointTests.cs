using System.IO.Compression;
using System.Net;
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
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(address) }, handler);
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