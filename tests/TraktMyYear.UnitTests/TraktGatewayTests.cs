using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
                    "[{\"last_watched_at\":\"2026-01-02T10:00:00Z\",\"plays\":1,\"show\":{\"title\":\"Example Show\",\"year\":2026,\"rating\":8.4,\"ids\":{\"trakt\":42}}}]",
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
        Assert.Contains("/users/pangspang/watched/shows?extended=min&page=1&limit=100", handler.RequestUris[0]);
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
        Assert.Contains("/users/me/watched/shows?extended=min&page=1&limit=100", handler.RequestUris[0]);
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
        Assert.Equal(2, movie.WatchCount);
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