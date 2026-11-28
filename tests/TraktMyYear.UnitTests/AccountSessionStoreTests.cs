using TraktMyYear.Application;
using TraktMyYear.Infrastructure.Authentication;

namespace TraktMyYear.UnitTests;

public sealed class AccountSessionStoreTests
{
    [Fact]
    public async Task DisconnectInvalidatesEverySessionAndTokenForOnlyThatAccount()
    {
        var store = new InMemoryAccountStore();
        var firstA = Connect(store, "account-a", "access-a");
        var secondA = Connect(store, "account-a", "access-a-2");
        var firstB = Connect(store, "account-b", "access-b");
        var accountLifetime = store.GetSession(firstA.SessionId)!.LifetimeToken;

        store.DisconnectAccount("account-a");

        Assert.Null(store.GetSession(firstA.SessionId));
        Assert.Null(store.GetSession(secondA.SessionId));
        Assert.Null(await store.GetAsync("account-a"));
        Assert.True(accountLifetime.IsCancellationRequested);
        Assert.NotNull(store.GetSession(firstB.SessionId));
        Assert.Equal("access-b", (await store.GetAsync("account-b"))!.AccessToken);
    }

    [Fact]
    public async Task ReauthorizationOnTheOnlySessionReplacesSessionWithoutCancellingAccount()
    {
        var store = new InMemoryAccountStore();
        var old = Connect(store, "account-a", "old");

        var replacement = store.CreateSession(
            new TraktAccount("account-a", "account-a"),
            new TraktToken("new", "refresh-new", DateTimeOffset.UtcNow.AddHours(1)),
            old.SessionId);

        Assert.Null(store.GetSession(old.SessionId));
        Assert.False(store.GetSession(replacement.SessionId)!.LifetimeToken.IsCancellationRequested);
        Assert.Equal("new", (await store.GetAsync("account-a"))!.AccessToken);
    }

    [Fact]
    public void IdleAndAbsoluteLimitsAreIndependentAndExpiredAccountsAreCleaned()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new InMemoryAccountStore(clock);
        var idle = Connect(store, "account-a", "access-a");
        var active = Connect(store, "account-a", "access-a");

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(store.GetSession(active.SessionId, touch: true));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(store.GetSession(idle.SessionId));
        Assert.NotNull(store.GetSession(active.SessionId, touch: true));

        var absoluteExpiry = DateTimeOffset.Parse("2026-01-01T08:00:00Z");
        while (clock.GetUtcNow() < absoluteExpiry - TimeSpan.FromMinutes(1))
        {
            var remaining = absoluteExpiry - TimeSpan.FromMinutes(1) - clock.GetUtcNow();
            clock.Advance(remaining < TimeSpan.FromMinutes(29) ? remaining : TimeSpan.FromMinutes(29));
            Assert.NotNull(store.GetSession(active.SessionId, touch: true));
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains("account-a", store.CleanupExpiredSessions());
        Assert.False(store.IsAccountActive("account-a"));
    }

    [Fact]
    public void ExpiryDiscoveredDuringRequestStillQueuesAccountCacheCleanup()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new InMemoryAccountStore(clock);
        var session = Connect(store, "account-a", "access-a");

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(store.GetSession(session.SessionId));

        Assert.Contains("account-a", store.CleanupExpiredSessions());
    }

    private static AuthenticatedLogin Connect(InMemoryAccountStore store, string accountId, string accessToken) =>
        store.CreateSession(
            new TraktAccount(accountId, accountId),
            new TraktToken(accessToken, $"refresh-{accessToken}", DateTimeOffset.UtcNow.AddHours(1)));

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }
}
