using TraktMyYear.Application;
using TraktMyYear.Infrastructure;
using TraktMyYear.Infrastructure.Authentication;

namespace TraktMyYear.UnitTests;

public sealed class AuthenticationServiceTests
{
    [Fact]
    public void OAuthStateIsSingleUse()
    {
        var store = new InMemoryOAuthStateStore();
        var state = store.Create("browser");

        Assert.False(store.TryConsume(state.State, "other-browser", out _));
        Assert.True(store.TryConsume(state.State, "browser", out _));
        Assert.False(store.TryConsume(state.State, "browser", out _));
    }

    [Fact]
    public void OAuthStateExpiresAndIsRemovedFromBrowserIndex()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new InMemoryOAuthStateStore(clock);
        var state = store.Create("browser");

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.False(store.TryConsume(state.State, "browser", out _));
        var replacement = store.Create("browser");
        Assert.True(store.TryConsume(replacement.State, "browser", out _));
    }

    [Fact]
    public async Task CallbackWithInvalidStateDoesNotExchangeCode()
    {
        var client = new FakeOAuthClient();
        var accounts = new InMemoryAccountStore();
        var service = new AuthenticationService(
            new InMemoryOAuthStateStore(),
            client,
            accounts,
            new FakeYearReviewStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAuthorizationAsync(
            new TraktAuthorizationCallback("code", "invalid-state"), "browser", null));

        Assert.False(client.ExchangeCalled);
        Assert.False(accounts.IsAccountActive("account-id"));
    }

    [Fact]
    public async Task ValidCallbackStoresTokenAndDisconnectClearsIt()
    {
        var stateStore = new InMemoryOAuthStateStore();
        var client = new FakeOAuthClient();
        var accounts = new InMemoryAccountStore();
        var service = new AuthenticationService(stateStore, client, accounts, new FakeYearReviewStore());
        var authorization = service.BeginAuthorization("browser");

        var login = await service.CompleteAuthorizationAsync(
            new TraktAuthorizationCallback("code", authorization.State), "browser", null);

        Assert.NotNull(await accounts.GetAsync(login.AccountId));
        Assert.True(client.ExchangeCalled);

        await service.DisconnectAsync(login.AccountId);

        Assert.Null(await accounts.GetAsync(login.AccountId));
        Assert.Null(accounts.GetSession(login.SessionId));
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

        public Task<TraktAccount> GetAccountAsync(TraktToken token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TraktAccount("account-id", "account"));
    }

    private sealed class FakeYearReviewStore : IYearReviewStore
    {
        public Task<YearReviewSnapshot?> GetAsync(string accountId, int year, CancellationToken cancellationToken = default) =>
            Task.FromResult<YearReviewSnapshot?>(null);

        public Task SetAsync(string accountId, YearReviewSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string accountId, int year, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}