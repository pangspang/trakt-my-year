namespace TraktMyYear.Application;

public sealed record OAuthAuthorization(string AuthorizationUrl, string State);

public sealed record OAuthState(string State, string CodeVerifier, string? OriginatingSessionId = null);

public sealed record TraktAccount(string Id, string DisplayName);

public sealed record ApplicationSession(
    string Id,
    string AccountId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    CancellationToken LifetimeToken);

public sealed record AuthenticatedLogin(string AccountId, string DisplayName, string SessionId);

public sealed record TraktToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenType = "bearer");

public sealed record TraktAuthorizationCallback(string Code, string State);

public sealed class TraktAccountNotConnectedException : InvalidOperationException
{
    public TraktAccountNotConnectedException()
        : base("Connect a Trakt account before requesting year reviews.")
    {
    }
}

public interface IOAuthStateStore
{
    OAuthState Create(string browserId, string? originatingSessionId = null);
    bool TryConsume(string state, string browserId, out OAuthState oauthState);
    void CleanupExpired();
}

public interface ITokenStore
{
    Task SaveAsync(string accountId, TraktToken token, CancellationToken cancellationToken = default);
    Task<TraktToken?> GetAsync(string accountId, CancellationToken cancellationToken = default);
    Task<bool> TryReplaceAsync(
        string accountId,
        string expectedAccessToken,
        TraktToken replacement,
        CancellationToken cancellationToken = default);
}

public interface ITraktOAuthClient
{
    string BuildAuthorizationUrl(string state, string codeChallenge);
    Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default);
    Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<TraktAccount> GetAccountAsync(TraktToken token, CancellationToken cancellationToken = default);
}

public interface IApplicationSessionStore
{
    ApplicationSession? GetSession(string sessionId, bool touch = false);
    AuthenticatedLogin CreateSession(
        TraktAccount account,
        TraktToken token,
        string? replaceSessionId = null,
        CancellationToken? expectedSessionLifetime = null);
    ApplicationSession? GetAccountSession(string accountId);
    bool IsAccountActive(string accountId);
    CancellationToken GetAccountLifetimeToken(string accountId);
    void DisconnectAccount(string accountId);
    void RemoveSession(string sessionId);
    IReadOnlyList<string> CleanupExpiredSessions();
}

public interface IAuthenticationService
{
    OAuthAuthorization BeginAuthorization(string browserId, string? originatingSessionId = null);
    Task<AuthenticatedLogin> CompleteAuthorizationAsync(
        TraktAuthorizationCallback callback,
        string browserId,
        string? replaceSessionId,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(string accountId, CancellationToken cancellationToken = default);
}