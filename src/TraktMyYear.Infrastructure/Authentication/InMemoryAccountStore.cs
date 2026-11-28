using TraktMyYear.Application;

namespace TraktMyYear.Infrastructure.Authentication;

public sealed class InMemoryAccountStore(TimeProvider? timeProvider = null) : ITokenStore, IApplicationSessionStore
{
    private static readonly TimeSpan AbsoluteSessionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan IdleSessionLifetime = TimeSpan.FromMinutes(30);
    private const int MaximumAccounts = 5;
    private const int MaximumSessionsPerAccount = 10;

    private readonly object gate = new();
    private readonly Dictionary<string, AccountEntry> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionEntry> sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> expiredAccountsPendingCacheCleanup = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public ApplicationSession? GetSession(string sessionId, bool touch = false)
    {
        lock (gate)
        {
            CleanupExpired();
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                return null;
            }

            if (!accounts.TryGetValue(session.AccountId, out var account))
            {
                sessions.Remove(sessionId);
                return null;
            }

            if (touch)
            {
                session.LastAccessedAt = clock.GetUtcNow();
            }

            return ToModel(session, account);
        }
    }

    public AuthenticatedLogin CreateSession(
        TraktAccount account,
        TraktToken token,
        string? replaceSessionId = null,
        CancellationToken? expectedSessionLifetime = null)
    {
        lock (gate)
        {
            CleanupExpired();
            if (expectedSessionLifetime.HasValue && replaceSessionId is null)
            {
                throw new ArgumentException("An expected lifetime requires an originating session.", nameof(expectedSessionLifetime));
            }

            if (replaceSessionId is not null
                && (!sessions.TryGetValue(replaceSessionId, out var expectedSession)
                    || !accounts.TryGetValue(expectedSession.AccountId, out var expectedAccount)
                    || expectedSessionLifetime.HasValue && expectedAccount.Lifetime.Token != expectedSessionLifetime.Value))
            {
                throw new InvalidOperationException("The originating application session is no longer active.");
            }

            var existing = accounts.GetValueOrDefault(account.Id);
            if (existing is null && accounts.Count >= MaximumAccounts)
            {
                throw new InvalidOperationException("The in-memory account limit has been reached.");
            }

            var replaceExisting = !string.IsNullOrEmpty(replaceSessionId)
                && sessions.TryGetValue(replaceSessionId, out var previousSession)
                && previousSession.AccountId == account.Id;
            if (existing is not null && existing.Sessions.Count - (replaceExisting ? 1 : 0) >= MaximumSessionsPerAccount)
            {
                throw new InvalidOperationException("The session limit for this Trakt account has been reached.");
            }

            if (!string.IsNullOrEmpty(replaceSessionId))
            {
                if (RemoveSessionLocked(replaceSessionId, out var previousAccount)
                    && previousAccount.Sessions.Count == 0
                    && previousAccount.Id != account.Id)
                {
                    RemoveAccountLocked(previousAccount);
                }
            }

            existing ??= new AccountEntry(account.Id, account.DisplayName);
            existing.DisplayName = account.DisplayName;
            existing.Token = token;
            accounts[account.Id] = existing;

            var sessionId = RandomId();
            var now = clock.GetUtcNow();
            var session = new SessionEntry(sessionId, account.Id, now, now);
            sessions.Add(sessionId, session);
            existing.Sessions.Add(sessionId);
            return new AuthenticatedLogin(account.Id, account.DisplayName, sessionId);
        }
    }

    public ApplicationSession? GetAccountSession(string accountId)
    {
        lock (gate)
        {
            CleanupExpired();
            if (!accounts.TryGetValue(accountId, out var account))
            {
                return null;
            }

            var session = account.Sessions.Select(id => sessions.GetValueOrDefault(id)).FirstOrDefault(item => item is not null);
            return session is null ? null : ToModel(session, account);
        }
    }

    public bool IsAccountActive(string accountId)
    {
        lock (gate)
        {
            CleanupExpired();
            return accounts.ContainsKey(accountId);
        }
    }

    public CancellationToken GetAccountLifetimeToken(string accountId)
    {
        lock (gate)
        {
            CleanupExpired();
            return accounts.TryGetValue(accountId, out var account)
                ? account.Lifetime.Token
                : throw new TraktAccountNotConnectedException();
        }
    }

    public void DisconnectAccount(string accountId)
    {
        lock (gate)
        {
            if (!accounts.TryGetValue(accountId, out var account))
            {
                return;
            }

            RemoveAccountLocked(account);
        }
    }

    public void RemoveSession(string sessionId)
    {
        lock (gate)
        {
            if (RemoveSessionLocked(sessionId, out var account) && account.Sessions.Count == 0)
            {
                RemoveAccountLocked(account);
            }
        }
    }

    public IReadOnlyList<string> CleanupExpiredSessions()
    {
        lock (gate)
        {
            CleanupExpired();
            var expiredAccounts = expiredAccountsPendingCacheCleanup.ToArray();
            expiredAccountsPendingCacheCleanup.Clear();
            return expiredAccounts;
        }
    }

    public Task SaveAsync(string accountId, TraktToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!accounts.TryGetValue(accountId, out var account))
            {
                throw new TraktAccountNotConnectedException();
            }

            account.Token = token;
        }

        return Task.CompletedTask;
    }

    public Task<TraktToken?> GetAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            CleanupExpired();
            return Task.FromResult(accounts.GetValueOrDefault(accountId)?.Token);
        }
    }

    public Task<bool> TryReplaceAsync(
        string accountId,
        string expectedAccessToken,
        TraktToken replacement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!accounts.TryGetValue(accountId, out var account) || account.Token?.AccessToken != expectedAccessToken)
            {
                return Task.FromResult(false);
            }

            account.Token = replacement;
            return Task.FromResult(true);
        }
    }

    private bool RemoveSessionLocked(string sessionId, out AccountEntry account)
    {
        account = null!;
        if (!sessions.Remove(sessionId, out var session)
            || !accounts.TryGetValue(session.AccountId, out var existingAccount))
        {
            return false;
        }

        account = existingAccount;
        account.Sessions.Remove(sessionId);
        return true;
    }

    private void RemoveSessionLocked(string sessionId)
    {
        if (RemoveSessionLocked(sessionId, out var account) && account.Sessions.Count == 0)
        {
            RemoveAccountLocked(account);
        }
    }

    private void RemoveAccountLocked(AccountEntry account)
    {
        foreach (var sessionId in account.Sessions)
        {
            sessions.Remove(sessionId);
        }

        account.Sessions.Clear();
        accounts.Remove(account.Id);
        account.Lifetime.Cancel();
    }

    private void CleanupExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var session in sessions.Values
                     .Where(item => now - item.CreatedAt >= AbsoluteSessionLifetime
                         || now - item.LastAccessedAt >= IdleSessionLifetime)
                     .ToArray())
        {
            RemoveSessionLocked(session.Id);
            if (!accounts.ContainsKey(session.AccountId))
            {
                expiredAccountsPendingCacheCleanup.Add(session.AccountId);
            }
        }
    }

    private static ApplicationSession ToModel(SessionEntry session, AccountEntry account) =>
        new(session.Id, session.AccountId, account.DisplayName, session.CreatedAt, session.LastAccessedAt, account.Lifetime.Token);

    private static string RandomId() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed class AccountEntry(string id, string displayName)
    {
        public string Id { get; } = id;
        public string DisplayName { get; set; } = displayName;
        public TraktToken? Token { get; set; }
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);
        public CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed class SessionEntry(string id, string accountId, DateTimeOffset createdAt, DateTimeOffset lastAccessedAt)
    {
        public string Id { get; } = id;
        public string AccountId { get; } = accountId;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset LastAccessedAt { get; set; } = lastAccessedAt;
    }
}
