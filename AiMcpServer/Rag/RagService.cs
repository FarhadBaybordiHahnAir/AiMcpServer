namespace AiMcpServer.Rag;

public sealed class RagService(RagStore store, DocumentExtractor extractor,
    IEmbeddingClient embeddings, WebDocumentFetcher web) : IDisposable
{
    private readonly SemaphoreSlim ingestionGate = new(1, 1);

    public Task<RagDocument> ImportFileAsync(string projectId, string fileName, byte[] bytes,
        CancellationToken cancellationToken)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        fileName = RagLimits.Required(Path.GetFileName(fileName.Replace('\\', '/')), nameof(fileName), 300);
        var format = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "pdf",
            ".txt" or ".md" => "text",
            _ => throw new RagException("Upload a .pdf, .txt, or .md file.")
        };
        return ImportAsync(projectId, fileName, fileName, format, bytes, cancellationToken);
    }

    public async Task<RagDocument> ImportUrlAsync(string projectId, string url, CancellationToken cancellationToken)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        WebDocument document;
        try
        {
            document = await web.FetchAsync(url, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new RagException("Unable to fetch this public web source. Private/reserved addresses and network proxies are blocked.", 502);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RagException("Web import exceeded its 30-second timeout.", 504);
        }
        return await ImportAsync(projectId, document.Source, document.Source, document.Format, document.Bytes, cancellationToken);
    }

    private async Task<RagDocument> ImportAsync(string projectId, string title, string source,
        string format, byte[] bytes, CancellationToken cancellationToken)
    {
        if (!await ingestionGate.WaitAsync(0, cancellationToken))
        {
            throw new RagException("Another document is being indexed. Wait for it to finish and retry.", 429);
        }
        try
        {
            var extracted = extractor.Extract(bytes, format, title, cancellationToken);
            var chunks = DocumentExtractor.Chunk(extracted.Pages);
            var embedded = new List<EmbeddedChunk>();
            foreach (var batch in chunks.Chunk(16))
            {
                var vectors = await embeddings.EmbedAsync(batch.Select(chunk => chunk.Text).ToArray(), cancellationToken);
                if (vectors.Count != batch.Length) throw new RagException("The embedding provider returned an incorrect batch size.", 502);
                embedded.AddRange(batch.Select((chunk, index) => new EmbeddedChunk(chunk, vectors[index])));
            }
            var document = new RagDocument(Guid.NewGuid().ToString("N"), projectId,
                extracted.Title, source, format, embeddings.Model, embedded[0].Vector.Length,
                chunks.Count, DateTimeOffset.UtcNow);
            await store.SaveAsync(document, embedded, cancellationToken);
            return document;
        }
        finally
        {
            ingestionGate.Release();
        }
    }

    public async Task<IReadOnlyList<RetrievedPassage>> SearchAsync(string projectId, string query,
        int limit = 5, double minimumScore = 0, CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        query = RagLimits.Required(query, nameof(query), 2000);
        if (limit is < 1 or > 20 || !double.IsFinite(minimumScore) || minimumScore is < -1 or > 1)
        {
            throw new RagException("limit must be 1-20 and minimumScore must be between -1 and 1.");
        }
        var documents = await store.ListAsync(projectId, cancellationToken);
        if (documents.Count == 0) return [];
        var vectors = await embeddings.EmbedAsync([query], cancellationToken);
        return await store.SearchAsync(projectId, embeddings.Model, vectors[0], limit, minimumScore, cancellationToken);
    }

    public void Dispose() => ingestionGate.Dispose();
}
