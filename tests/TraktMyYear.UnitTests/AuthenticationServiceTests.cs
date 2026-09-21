using TraktMyYear.Application;
using TraktMyYear.Infrastructure;

namespace TraktMyYear.UnitTests;

public sealed class AuthenticationServiceTests
{
    [Fact]
    public void OAuthStateIsSingleUse()
    {
        var store = new InMemoryOAuthStateStore();
        var state = store.Create();

        Assert.True(store.TryConsume(state.State, out _));
        Assert.False(store.TryConsume(state.State, out _));
    }

    [Fact]
    public async Task CallbackWithInvalidStateDoesNotExchangeCode()
    {
        var client = new FakeOAuthClient();
        var tokens = new InMemoryTokenStore();
        var service = new AuthenticationService(
            new InMemoryOAuthStateStore(),
            client,
            tokens);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAuthorizationAsync(
            new TraktAuthorizationCallback("code", "invalid-state")));

        Assert.False(client.ExchangeCalled);
        Assert.False(tokens.HasToken);
    }

    [Fact]
    public async Task ValidCallbackStoresTokenAndDisconnectClearsIt()
    {
        var stateStore = new InMemoryOAuthStateStore();
        var client = new FakeOAuthClient();
        var tokens = new InMemoryTokenStore();
        var service = new AuthenticationService(stateStore, client, tokens);
        var authorization = service.BeginAuthorization();

        await service.CompleteAuthorizationAsync(
            new TraktAuthorizationCallback("code", authorization.State));

        Assert.True(tokens.HasToken);
        Assert.True(client.ExchangeCalled);

        await service.DisconnectAsync();

        Assert.False(tokens.HasToken);
    }

    private sealed class FakeOAuthClient : ITraktOAuthClient
    {
        public bool ExchangeCalled { get; private set; }

        public string BuildAuthorizationUrl(string state, string codeChallenge) => $"https://trakt.example/authorize?state={state}";

        public Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default)
        {
            ExchangeCalled = true;
            return Task.FromResult(new TraktToken("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)));
        }

        public Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktToken("access-refreshed", "refresh-refreshed", DateTimeOffset.UtcNow.AddHours(1)));
    }
}