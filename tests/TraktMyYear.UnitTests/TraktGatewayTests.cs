using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;

namespace TraktMyYear.UnitTests;

public sealed class TraktGatewayTests
{
    [Fact]
    public async Task WatchedShowsUseApiKeyWhenOAuthIsNotConnected()
    {
        var handler = new FixtureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"last_watched_at\":\"2026-01-02T10:00:00Z\",\"plays\":1,\"show\":{\"title\":\"Example Show\",\"year\":2026,\"images\":{\"poster\":[\"https://images.example/poster.jpg\"]},\"ids\":{\"trakt\":42,\"slug\":\"example-show\",\"imdb\":\"tt0000042\",\"tmdb\":52,\"tvdb\":62}}}]",
                    Encoding.UTF8,
                    "application/json")
            });
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(),
            new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "pangspang" }));

        var result = await gateway.GetWatchedAsync(
            MediaType.Show,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(result);
        Assert.Null(handler.AuthorizationHeaders[0]);
        Assert.Equal("https://images.example/poster.jpg", result[0].PosterUrl);
        Assert.Equal("https://trakt.tv/shows/example-show", result[0].TraktUrl);
        Assert.Equal(new TitleExternalIds("tt0000042", 52, 62), result[0].ExternalIds);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), result[0].WatchedAt);
        Assert.Contains("/users/pangspang/watched/shows?extended=full&page=1&limit=100", handler.RequestUris[0]);
    }

    [Fact]
    public async Task UnauthorizedRequestRefreshesTokenAndMapsShowWithoutEpisodeData()
    {
        var handler = new FixtureHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"last_watched_at\":\"2026-01-02T10:00:00Z\",\"plays\":1,\"episode\":{\"ids\":{\"trakt\":900}},\"show\":{\"title\":\"Example Show\",\"year\":2026,\"rating\":8.4,\"ids\":{\"trakt\":42}}}]",
                    Encoding.UTF8,
                    "application/json")
            });
        var tokens = new InMemoryTokenStore();
        await tokens.SaveAsync(new TraktToken("expired", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var oauth = new FakeOAuthClient();
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            tokens,
            oauth,
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        var result = await gateway.GetWatchedAsync(
            MediaType.Show,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var show = Assert.Single(result);
        Assert.Equal(42, show.TraktId);
        Assert.Equal("Example Show", show.Title);
        Assert.Equal(1, oauth.RefreshCalls);
        var refreshedHeader = Assert.IsType<AuthenticationHeaderValue>(handler.AuthorizationHeaders[1]);
        Assert.Equal("refreshed", refreshedHeader.Parameter);
        Assert.Contains("/users/me/watched/shows?extended=full&page=1&limit=100", handler.RequestUris[0]);
    }

    [Fact]
    public async Task WatchedMoviesAcceptObjectResponse()
    {
        var handler = new FixtureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"last_watched_at\":\"2026-01-02T10:00:00Z\",\"play_count\":2,\"movie\":{\"title\":\"Example Movie\",\"year\":2026,\"rating\":8.4,\"ids\":{\"trakt\":42}}}",
                    Encoding.UTF8,
                    "application/json")
            });
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(),
            new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "pangspang" }));

        var result = await gateway.GetWatchedAsync(
            MediaType.Movie,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var movie = Assert.Single(result);
        Assert.Equal(42, movie.TraktId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), movie.WatchedAt);
    }

    [Fact]
    public async Task SimklUsesYearHistoryAndCurrentRatingsWithoutAddingUnrelatedTitles()
    {
        var handler = new FixtureHandler(
            JsonResponse("""
                [
                  {"type":"movie","watched_at":"2025-01-02T10:00:00Z","movie":{"title":"Rewatched later","year":2000,"ids":{"trakt":42,"imdb":"tt0000042"}}},
                  {"type":"movie","watched_at":"2025-11-02T10:00:00Z","movie":{"title":"Rewatched later","year":2000,"ids":{"trakt":42,"tmdb":52}}},
                  {"type":"movie","watched_at":"2026-01-01T00:00:00Z","movie":{"title":"Outside year","ids":{"trakt":99}}}
                ]
                """),
            JsonResponse("""
                [
                  {"rating":9,"rated_at":"2026-02-01T00:00:00Z","movie":{"title":"Rewatched later","ids":{"trakt":42,"imdb":"tt0000042"}}},
                  {"rating":8,"rated_at":"2025-05-01T00:00:00Z","movie":{"title":"Rated only","ids":{"trakt":43,"tmdb":53}}},
                  {"rating":7,"rated_at":"2024-01-01T00:00:00Z","movie":{"title":"Unrelated","ids":{"trakt":44}}}
                ]
                """));
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(), new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        var items = await gateway.GetExportTitlesAsync(MediaType.Movie,
            DateTimeOffset.Parse("2025-01-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        Assert.Equal(2, items.Count);
        var watched = Assert.Single(items, item => item.TraktId == 42);
        Assert.Equal(DateTimeOffset.Parse("2025-11-02T10:00:00Z"), watched.WatchedAt);
        Assert.Equal(9, watched.PersonalRating);
        Assert.Equal(new TitleExternalIds("tt0000042", 52), watched.ExternalIds);
        Assert.Null(Assert.Single(items, item => item.TraktId == 43).WatchedAt);
        Assert.Contains("/history/movies?", handler.RequestUris[0]);
        Assert.Contains("start_at=", handler.RequestUris[0]);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task SimklTraversesHistoryBeyondTheLegacyHundredPageLimit()
    {
        var handler = new PagedHistoryHandler();
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(), new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        var items = await gateway.GetExportTitlesAsync(MediaType.Movie,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z"));

        Assert.Equal(101, items.Count);
        Assert.Equal(101, handler.HistoryRequests);
        Assert.Contains(items, item => item.TraktId == 101);
    }

    [Fact]
    public async Task SimklAggregatesSpecialsAndOutOfOrderEpisodesAsOneShow()
    {
        var handler = new FixtureHandler(JsonResponse("""
            [
              {"type":"episode","watched_at":"2026-01-03T00:00:00Z","episode":{"season":1,"number":2},"show":{"title":"Show","ids":{"trakt":42,"tvdb":62}}},
              {"type":"episode","watched_at":"2026-01-02T00:00:00Z","episode":{"season":1,"number":8},"show":{"title":"Show","ids":{"trakt":42,"tmdb":52}}},
              {"type":"episode","watched_at":"2026-01-01T00:00:00Z","episode":{"season":0,"number":1},"show":{"title":"Show","ids":{"trakt":42}}},
              {"type":"movie","watched_at":"2026-01-03T00:00:00Z","movie":{"title":"Not a show","ids":{"trakt":42}}}
            ]
            """), JsonResponse("[]"));
        var gateway = new TraktGateway(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(), new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        var item = Assert.Single(await gateway.GetExportTitlesAsync(MediaType.Show,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z")));

        Assert.Equal(MediaType.Show, item.MediaType);
        Assert.Equal(DateTimeOffset.Parse("2026-01-03T00:00:00Z"), item.WatchedAt);
        Assert.Equal(new TitleExternalIds(Tmdb: 52, Tvdb: 62), item.ExternalIds);
        Assert.Equal(new WatchedEpisode(1, 2), item.LastEpisodeWatched);
        var csv = Encoding.UTF8.GetString(Assert.Single(SimklCsv.Serialize([item])));
        Assert.Contains("\"S01E02\"", csv);
        Assert.DoesNotContain("S01E08", csv);
        Assert.DoesNotContain("completed", csv);
    }

    [Theory]
    [InlineData("[{\"type\":\"movie\",\"movie\":{\"title\":\"Missing date\",\"ids\":{\"trakt\":42}}}]")]
    [InlineData("[{\"type\":\"movie\",\"watched_at\":\"2026-01-02T00:00:00Z\",\"movie\":{\"title\":\"Missing ID\"}}]")]
    public async Task SimklDoesNotSilentlyExportIncompleteHistory(string json)
    {
        var gateway = new TraktGateway(
            new HttpClient(new FixtureHandler(JsonResponse(json))) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(), new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetExportTitlesAsync(MediaType.Movie,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z")));
    }

    [Fact]
    public async Task SimklRejectsUnexpectedResponseShapeRatherThanReturningAnEmptyExport()
    {
        var gateway = new TraktGateway(
            new HttpClient(new FixtureHandler(JsonResponse("{\"error\":\"unexpected\"}"))) { BaseAddress = new Uri("https://api.trakt.tv") },
            new InMemoryTokenStore(), new FakeOAuthClient(),
            Microsoft.Extensions.Options.Options.Create(new TraktOptions { Username = "me" }));

        await Assert.ThrowsAsync<JsonException>(() => gateway.GetExportTitlesAsync(MediaType.Movie,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z")));
    }

    private sealed class PagedHistoryHandler : HttpMessageHandler
    {
        public int HistoryRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/ratings/"))
            {
                return Task.FromResult(JsonResponse("[]"));
            }

            HistoryRequests++;
            var response = JsonResponse(JsonSerializer.Serialize(new[]
            {
                new { type = "movie", watched_at = "2026-01-02T00:00:00Z", movie = new { title = "Example", ids = new { trakt = HistoryRequests } } }
            }));
            response.Headers.Add("X-Pagination-Page-Count", "101");
            return Task.FromResult(response);
        }
    }

    private sealed class FixtureHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int responseIndex;
        public List<AuthenticationHeaderValue?> AuthorizationHeaders { get; } = [];
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization);
            RequestUris.Add(request.RequestUri!.ToString());
            var response = responses[Math.Min(responseIndex++, responses.Length - 1)];
            response.Headers.Add("X-Pagination-Page-Count", "1");
            return Task.FromResult(response);
        }
    }

    private sealed class FakeOAuthClient : ITraktOAuthClient
    {
        public int RefreshCalls { get; private set; }

        public string BuildAuthorizationUrl(string state, string codeChallenge) => state;

        public Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktToken("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(new TraktToken("refreshed", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}