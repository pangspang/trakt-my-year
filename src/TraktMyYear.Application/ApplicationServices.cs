namespace TraktMyYear.Application;

public sealed class AuthenticationService(
    IOAuthStateStore stateStore,
    ITraktOAuthClient traktOAuthClient,
    IApplicationSessionStore sessionStore,
    IYearReviewStore yearReviewStore) : IAuthenticationService
{
    public OAuthAuthorization BeginAuthorization(string browserId, string? originatingSessionId = null)
    {
        var oauthState = stateStore.Create(browserId, originatingSessionId);
        var codeChallenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(oauthState.CodeVerifier)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return new OAuthAuthorization(
            traktOAuthClient.BuildAuthorizationUrl(oauthState.State, codeChallenge),
            oauthState.State);
    }

    public async Task<AuthenticatedLogin> CompleteAuthorizationAsync(
        TraktAuthorizationCallback callback,
        string browserId,
        string? replaceSessionId,
        CancellationToken cancellationToken = default)
    {
        if (!stateStore.TryConsume(callback.State, browserId, out var oauthState))
        {
            throw new InvalidOperationException("The OAuth state is invalid, expired, or belongs to another browser.");
        }

        if (!string.Equals(replaceSessionId, oauthState.OriginatingSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The originating application session has changed.");
        }

        var originatingSession = replaceSessionId is null
            ? null
            : sessionStore.GetSession(replaceSessionId)
                ?? throw new InvalidOperationException("The originating application session is no longer active.");
        var previousAccountId = originatingSession?.AccountId;

        var token = await traktOAuthClient.ExchangeCodeAsync(callback.Code, oauthState.CodeVerifier, cancellationToken);
        var account = await traktOAuthClient.GetAccountAsync(token, cancellationToken);
        if (string.IsNullOrWhiteSpace(account.Id) || string.IsNullOrWhiteSpace(account.DisplayName))
        {
            throw new InvalidOperationException("Trakt did not return a valid account identity.");
        }

        if (!sessionStore.IsAccountActive(account.Id))
        {
            await yearReviewStore.RemoveAccountAsync(account.Id, cancellationToken);
        }

        var login = sessionStore.CreateSession(
            account,
            token,
            replaceSessionId,
            originatingSession?.LifetimeToken);
        if (previousAccountId is not null && previousAccountId != account.Id
            && !sessionStore.IsAccountActive(previousAccountId))
        {
            await yearReviewStore.RemoveAccountAsync(previousAccountId, cancellationToken);
        }

        return login;
    }

    public async Task DisconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        sessionStore.DisconnectAccount(accountId);
        await yearReviewStore.RemoveAccountAsync(accountId, cancellationToken);
    }
}
