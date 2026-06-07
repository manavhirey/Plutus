using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Plutus.Core.SimpleFin;

namespace Plutus.Core.Tests;

public sealed class SimpleFinClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public async Task ClaimAsync_decodes_token_posts_and_returns_access_url()
    {
        const string claimUrl = "https://claim.example/abc";
        const string accessUrl = "https://user:pass@bridge.simplefin.org/simplefin";
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(claimUrl));

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(accessUrl),
        });
        var client = new SimpleFinClient(new HttpClient(handler), NullLogger<SimpleFinClient>.Instance);

        var result = await client.ClaimAsync(token);

        Assert.Equal(accessUrl, result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(claimUrl, handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetAccountsAsync_strips_credentials_and_sends_basic_auth()
    {
        const string json = """
        {"accounts":[{"id":"a1","name":"Checking","currency":"USD","balance":"100.00","balance-date":1700000000,"org":{"name":"Bank"},"transactions":[{"id":"t1","posted":1700000500,"amount":"-10.00","description":"Coffee"}]}]}
        """;

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var client = new SimpleFinClient(new HttpClient(handler), NullLogger<SimpleFinClient>.Instance);

        var start = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var end = DateTimeOffset.FromUnixTimeSeconds(1700100000);
        var result = await client.GetAccountsAsync("https://user:pass@bridge.simplefin.org/simplefin", start, end);

        // Parsed payload
        var account = Assert.Single(result.Accounts);
        Assert.Equal("Checking", account.Name);
        var txn = Assert.Single(account.Transactions!);
        Assert.Equal("-10.00", txn.Amount);

        // Request shape: credentials moved to the Authorization header, not the URL.
        var request = handler.LastRequest!;
        Assert.Equal(
            $"https://bridge.simplefin.org/simplefin/accounts?start-date=1700000000&end-date=1700100000",
            request.RequestUri!.ToString());
        Assert.Null(request.RequestUri.UserInfo is { Length: > 0 } ? request.RequestUri.UserInfo : null);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass")),
            request.Headers.Authorization.Parameter);
    }
}
