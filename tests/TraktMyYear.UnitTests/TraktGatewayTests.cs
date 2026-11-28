using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using TraktMyYear.Infrastructure.Authentication;

namespace TraktMyYear.UnitTests;

public sealed class TraktGatewayTests
{
    [Fact]
    public async Task WatchedShowsUseTheAuthenticatedAccountToken()
    {
        var handler = new FixtureHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[{\"last_watched_at\":\"2026-01-02T10:00:00Z\",\"plays\":1,\"show\":{\"title\":\"Example Show\",\"year\":2026,\"images\":{\"poster\":[\"https://images.example/poster.jpg\"]},\"ids\":{\"trakt\":42,\"slug\":\"example-show\",\"imdb\":\"tt0000042\",\"tmdb\":52,\"tvdb\":62}}}]",
                    Encoding.UTF8,
                    "application/json")
            });
        var accounts = CreateAccountStore();
        var gateway = CreateGateway(handler, accounts);

        var result = await gateway.GetWatchedAsync(
            "test-account",
            MediaType.Show,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(result);
        Assert.Equal("access", handler.AuthorizationHeaders[0]?.Parameter);
        Assert.Equal("https://images.example/poster.jpg", result[0].PosterUrl);
        Assert.Equal("https://trakt.tv/shows/example-show", result[0].TraktUrl);
        Assert.Equal(new TitleExternalIds("tt0000042", 52, 62), result[0].ExternalIds);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), result[0].WatchedAt);
        Assert.Contains("/users/me/watched/shows?extended=full&page=1&limit=100", handler.RequestUris[0]);
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
        var tokens = CreateAccountStore(new TraktToken("expired", "refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var oauth = new FakeOAuthClient();
        var gateway = CreateGateway(handler, tokens, oauth);

        var result = await gateway.GetWatchedAsync(
            "test-account",
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
        var accounts = CreateAccountStore();
        var gateway = CreateGateway(handler, accounts);

        var result = await gateway.GetWatchedAsync(
            "test-account",
            MediaType.Movie,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var movie = Assert.Single(result);
        Assert.Equal(42, movie.TraktId);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), movie.WatchedAt);
    }

    [Fact]
    public async Task RateLimitedResponsePreservesTraktStatusCode()
    {
        var gateway = CreateGateway(
            new FixtureHandler(new HttpResponseMessage((HttpStatusCode)420)),
            CreateAccountStore());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.GetWatchedAsync(
            "test-account",
            MediaType.Movie,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal((HttpStatusCode)420, exception.StatusCode);
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
        var accounts = CreateAccountStore();
        var gateway = CreateGateway(handler, accounts);

        var items = await gateway.GetExportTitlesAsync("test-account", MediaType.Movie,
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

    private static InMemoryAccountStore CreateAccountStore(TraktToken? token = null)
    {
        var store = new InMemoryAccountStore();
        store.CreateSession(
            new TraktAccount("test-account", "test"),
            token ?? new TraktToken("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        return store;
    }

    private static TraktGateway CreateGateway(HttpMessageHandler handler, InMemoryAccountStore accounts, FakeOAuthClient? oauth = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            accounts, oauth ?? new FakeOAuthClient(), accounts, new AccountTokenRefreshCoordinator());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task SimklTraversesHistoryBeyondTheLegacyHundredPageLimit()
    {
        var handler = new PagedHistoryHandler();
        var accounts = CreateAccountStore();
        var gateway = CreateGateway(handler, accounts);

        var items = await gateway.GetExportTitlesAsync("test-account", MediaType.Movie,
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
        var accounts = CreateAccountStore();
        var gateway = CreateGateway(handler, accounts);

        var item = Assert.Single(await gateway.GetExportTitlesAsync("test-account", MediaType.Show,
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
        var gateway = CreateGateway(new FixtureHandler(JsonResponse(json)), CreateAccountStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetExportTitlesAsync("test-account", MediaType.Movie,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T00:00:00Z")));
    }

    [Fact]
    public async Task SimklRejectsUnexpectedResponseShapeRatherThanReturningAnEmptyExport()
    {
        var gateway = CreateGateway(new FixtureHandler(JsonResponse("{\"error\":\"unexpected\"}")), CreateAccountStore());

        await Assert.ThrowsAsync<JsonException>(() => gateway.GetExportTitlesAsync("test-account", MediaType.Movie,
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

        public Task<TraktAccount> GetAccountAsync(TraktToken token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktAccount("test-account", "test"));
    }
}