using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiMcpServer.Rag;

public interface IEmbeddingClient
{
    string Model { get; }
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public sealed class OllamaEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient client;
    public string Model { get; }

    public OllamaEmbeddingClient(HttpClient client, IOptions<RagOptions> options)
    {
        this.client = client;
        Model = RagLimits.Required(options.Value.EmbeddingModel, "EmbeddingModel");
        if (!Uri.TryCreate(options.Value.OllamaEndpoint, UriKind.Absolute, out var endpoint)
            || !(endpoint.Scheme == "http" || endpoint.Scheme == "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
        {
            throw new RagException("Rag:OllamaEndpoint must be a localhost HTTP/HTTPS origin.");
        }
        client.BaseAddress = endpoint;
        client.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count is < 1 or > 16) throw new RagException("Embedding batches must contain 1-16 texts.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/embed")
            {
                Content = JsonContent.Create(new { model = Model, input = texts, truncate = false })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new RagException($"Ollama embedding request failed (HTTP {(int)response.StatusCode}). Ensure Ollama is running and run: ollama pull {Model}", 502);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await DocumentExtractor.ReadBoundedAsync(stream, deadline.Token);
            var payload = JsonSerializer.Deserialize<EmbeddingResponse>(bytes, JsonSerializerOptions.Web);
            if (payload?.Embeddings is null || payload.Embeddings.Length != texts.Count
                || payload.Embeddings.Any(vector => vector is null))
            {
                throw new RagException("Ollama returned an unexpected number of embeddings.", 502);
            }
            var vectors = payload.Embeddings.Select(RagLimits.Normalize).ToArray();
            if (vectors.Any(vector => vector.Length != vectors[0].Length))
            {
                throw new RagException("Ollama returned inconsistent embedding dimensions.", 502);
            }
            return vectors;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RagException("Ollama embedding request timed out. Ensure the model is loaded and retry.", 504);
        }
        catch (HttpRequestException)
        {
            throw new RagException("Cannot reach Ollama. Start Ollama and verify Rag:OllamaEndpoint.", 502);
        }
        catch (JsonException)
        {
            throw new RagException("Ollama returned an invalid embedding response.", 502);
        }
    }

    private sealed record EmbeddingResponse(float[][] Embeddings);
}
