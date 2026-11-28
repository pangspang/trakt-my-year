using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using TraktMyYear.Application;
using TraktMyYear.Infrastructure;

namespace TraktMyYear.UnitTests;

public sealed class TraktAccountProfileTests
{
    [Fact]
    public async Task ProfileResolutionRequiresAndUsesTraktUuid()
    {
        var handler = new ProfileHandler("""{"user":{"username":"rewinder","ids":{"uuid":"trakt-account-uuid"}}}""");
        var client = new TraktOAuthClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.trakt.tv") },
            Options.Create(new TraktOptions { ClientId = "client" }));

        var account = await client.GetAccountAsync(
            new TraktToken("private-access-token", "private-refresh-token", DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal("trakt-account-uuid", account.Id);
        Assert.Equal("rewinder", account.DisplayName);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("private-access-token", handler.Authorization?.Parameter);
        Assert.Equal("/users/settings", handler.Path);
    }

    [Fact]
    public async Task ProfileWithoutStableUuidCannotEstablishAccount()
    {
        var client = new TraktOAuthClient(
            new HttpClient(new ProfileHandler("""{"user":{"username":"mutable-name","ids":{"slug":"mutable-name"}}}"""))
            {
                BaseAddress = new Uri("https://api.trakt.tv")
            },
            Options.Create(new TraktOptions { ClientId = "client" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccountAsync(
            new TraktToken("access", "refresh", DateTimeOffset.UtcNow.AddHours(1))));
    }

    private sealed class ProfileHandler(string json) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
