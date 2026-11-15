namespace AiMcpServer.Rag;

public sealed class RagOptions
{
    public string? DatabasePath { get; set; }
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
}

public sealed record RagDocument(string Id, string ProjectId, string Title, string Source,
    string SourceType, string Model, int Dimensions, int ChunkCount, DateTimeOffset CreatedAt);

public sealed record TextPage(int? Page, string Text);
public sealed record TextChunk(int Index, int? Page, string Text);
public sealed record EmbeddedChunk(TextChunk Chunk, float[] Vector);
public sealed record RetrievedPassage(string Citation, string DocumentId, string Title, string Source,
    int ChunkIndex, int? Page, string Text, double Score);

public sealed class RagException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class RagLimits
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTextCharacters = 500_000;
    public const int MaxDocumentChunks = 1000;
    public const int MaxProjectChunks = 10_000;

    public static string Required(string? value, string name, int maximum = 200)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
        {
            throw new RagException($"{name} must contain 1-{maximum} characters.");
        }
        return value.Trim();
    }

    public static float[] Normalize(float[] vector)
    {
        if (vector.Length is < 1 or > 8192 || vector.Any(value => !float.IsFinite(value)))
        {
            throw new RagException("Embedding vectors must contain 1-8192 finite values.", 502);
        }
        var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
        if (norm == 0)
        {
            throw new RagException("The embedding provider returned a zero vector.", 502);
        }
        return vector.Select(value => (float)(value / norm)).ToArray();
    }
}
