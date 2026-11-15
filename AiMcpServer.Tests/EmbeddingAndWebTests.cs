using System.Net;
using System.Net.Http.Json;
using System.Text;
using AiMcpServer.Rag;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiMcpServer.Tests;

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}

public sealed class EmbeddingAndWebTests
{
    [Fact]
    public async Task OllamaUsesEmbedApiAndNormalizesResults()
    {
        using var http = new HttpClient(new StubHttpHandler(request =>
        {
            Assert.Equal("/api/embed", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { embeddings = new[] { new[] { 3f, 4f } } }) };
        }));
        var client = new OllamaEmbeddingClient(http, Options.Create(new RagOptions()));

        var vector = Assert.Single(await client.EmbedAsync(["Query"], CancellationToken.None));

        Assert.Equal(0.6f, vector[0], 5);
        Assert.Equal(0.8f, vector[1], 5);
    }

    [Theory]
    [InlineData("{\"embeddings\":[]}")]
    [InlineData("{\"embeddings\":[[0,0]]}")]
    [InlineData("not-json")]
    public async Task InvalidEmbeddingResponsesAreRejected(string json)
    {
        using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }));
        var client = new OllamaEmbeddingClient(http, Options.Create(new RagOptions()));
        await Assert.ThrowsAsync<RagException>(() => client.EmbedAsync(["Query"], CancellationToken.None));
    }

    [Fact]
    public async Task MissingModelProducesActionableError()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = new OllamaEmbeddingClient(http, Options.Create(new RagOptions()));
        var error = await Assert.ThrowsAsync<RagException>(() => client.EmbedAsync(["Query"], CancellationToken.None));
        Assert.Contains("ollama pull nomic-embed-text", error.Message);
        Assert.Equal(502, error.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2002:7f00:1::")]
    public void PrivateAddressesAreNotPublic(string address)
        => Assert.False(WebDocumentFetcher.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressesAreAccepted(string address)
        => Assert.True(WebDocumentFetcher.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("http://localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://example.com:8080/")]
    [InlineData("https://user:password@example.com/")]
    public void UnsafeUrlsAreRejected(string url)
        => Assert.Throws<RagException>(() => WebDocumentFetcher.ValidateUrl(url));

    [Fact]
    public async Task RedirectsToPrivateUrlsAreRejected()
    {
        using var http = new HttpClient(new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://127.0.0.1/");
            return response;
        }));
        var fetcher = new WebDocumentFetcher(http);
        await Assert.ThrowsAsync<RagException>(() => fetcher.FetchAsync("https://example.com/", CancellationToken.None));
    }

    [Fact]
    public async Task DnsResolvedLoopbackIsBlockedBeforeConnection()
    {
        using var http = new HttpClient(WebDocumentFetcher.CreateHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://localhost/"));
    }

    [Fact]
    public async Task WebFetchReturnsSupportedContentAndFinalSource()
    {
        using var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Reference</html>", Encoding.UTF8, "text/html")
        }));
        var document = await new WebDocumentFetcher(http).FetchAsync("https://example.com/guide", CancellationToken.None);
        Assert.Equal("html", document.Format);
        Assert.Equal("https://example.com/guide", document.Source);
    }
}
