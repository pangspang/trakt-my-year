namespace TraktMyYear.Application;

public sealed class AuthenticationService(
    IOAuthStateStore stateStore,
    ITraktOAuthClient traktOAuthClient,
    ITokenStore tokenStore) : IAuthenticationService
{
    public OAuthAuthorization BeginAuthorization()
    {
        var oauthState = stateStore.Create();
        var codeChallenge = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(oauthState.CodeVerifier)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return new OAuthAuthorization(
            traktOAuthClient.BuildAuthorizationUrl(oauthState.State, codeChallenge),
            oauthState.State);
    }

    public async Task CompleteAuthorizationAsync(
        TraktAuthorizationCallback callback,
        CancellationToken cancellationToken = default)
    {
        if (!stateStore.TryConsume(callback.State, out var codeVerifier))
        {
            throw new InvalidOperationException("The OAuth state is invalid or has expired.");
        }

        var token = await traktOAuthClient.ExchangeCodeAsync(callback.Code, codeVerifier, cancellationToken);
        await tokenStore.SaveAsync(token, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        tokenStore.ClearAsync(cancellationToken);
}
