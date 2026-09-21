namespace TraktMyYear.Application;

public sealed record OAuthAuthorization(string AuthorizationUrl, string State);

public sealed record OAuthState(string State, string CodeVerifier);

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
    OAuthState Create();
    bool TryConsume(string state, out string codeVerifier);
}

public interface ITokenStore
{
    bool HasToken { get; }
    Task SaveAsync(TraktToken token, CancellationToken cancellationToken = default);
    Task<TraktToken?> GetAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ITraktOAuthClient
{
    string BuildAuthorizationUrl(string state, string codeChallenge);
    Task<TraktToken> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken cancellationToken = default);
    Task<TraktToken> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);
}

public interface IAuthenticationService
{
    OAuthAuthorization BeginAuthorization();
    Task CompleteAuthorizationAsync(TraktAuthorizationCallback callback, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}