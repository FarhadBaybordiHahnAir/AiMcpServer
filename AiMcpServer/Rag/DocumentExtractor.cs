using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AiMcpServer.Rag;

public sealed record ExtractedDocument(string Title, IReadOnlyList<TextPage> Pages);

public sealed class DocumentExtractor
{
    public ExtractedDocument Extract(byte[] bytes, string format, string title,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length is 0 or > RagLimits.MaxFileBytes)
        {
            throw new RagException("Documents must contain 1 byte to 10 MiB.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var pages = new List<TextPage>();
            if (format == "pdf")
            {
                using var pdf = PdfDocument.Open(bytes);
                if (pdf.NumberOfPages > 500) throw new RagException("PDFs are limited to 500 pages.");
                var total = 0;
                foreach (var page in pdf.GetPages())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = ContentOrderTextExtractor.GetText(page);
                    total += text.Length;
                    if (total > RagLimits.MaxTextCharacters) throw new RagException("Extracted text exceeds 500,000 characters.");
                    pages.Add(new TextPage(page.Number, text));
                }
            }
            else if (format == "html")
            {
                var html = new HtmlDocument();
                using var stream = new MemoryStream(bytes);
                html.Load(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var pageTitle = html.DocumentNode.SelectSingleNode("//title")?.InnerText;
                if (!string.IsNullOrWhiteSpace(pageTitle)) title = HtmlEntity.DeEntitize(pageTitle).Trim();
                var excluded = html.DocumentNode.SelectNodes("//script|//style|//nav|//footer|//header|//aside|//form|//noscript|//iframe|//svg|//head");
                if (excluded is not null)
                {
                    foreach (var node in excluded) node.Remove();
                }
                var nodes = html.DocumentNode.SelectNodes("//text()[normalize-space(.) != '']");
                var text = nodes is null ? "" : string.Join("\n", nodes.Select(node =>
                    Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim()));
                pages.Add(new TextPage(null, text));
            }
            else if (format == "text")
            {
                var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                if (text.Contains('\0')) throw new RagException("Text files must not contain binary data.");
                pages.Add(new TextPage(null, text));
            }
            else
            {
                throw new RagException("Unsupported document format.");
            }
            if (pages.Sum(page => (long)page.Text.Length) > RagLimits.MaxTextCharacters)
            {
                throw new RagException("Extracted text exceeds 500,000 characters.");
            }
            if (pages.All(page => string.IsNullOrWhiteSpace(page.Text)))
            {
                throw new RagException("No readable text was found. Scanned PDFs require OCR before import.");
            }
            return new ExtractedDocument(title.Length > 300 ? title[..300] : title, pages);
        }
        catch (Exception exception) when (exception is not RagException and not OperationCanceledException)
        {
            throw new RagException("The document could not be decoded. Use a valid text-based PDF or UTF-8 text document.");
        }
    }

    public static IReadOnlyList<TextChunk> Chunk(IReadOnlyList<TextPage> pages)
    {
        var chunks = new List<TextChunk>();
        foreach (var page in pages)
        {
            var text = page.Text.Trim();
            for (var start = 0; start < text.Length;)
            {
                var end = Math.Min(start + 1200, text.Length);
                if (end < text.Length)
                {
                    var boundary = text.LastIndexOf(' ', end - 1, end - start);
                    if (boundary > start + 600) end = boundary;
                    if (char.IsHighSurrogate(text[end - 1])) end--;
                }
                var content = text[start..end].Trim();
                if (content.Length > 0) chunks.Add(new TextChunk(chunks.Count, page.Page, content));
                if (chunks.Count > RagLimits.MaxDocumentChunks) throw new RagException("The document produces too many chunks.");
                if (end == text.Length) break;
                start = Math.Max(start + 1, end - 200);
                if (char.IsLowSurrogate(text[start])) start++;
            }
        }
        if (chunks.Count == 0) throw new RagException("No readable text was found.");
        return chunks;
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > RagLimits.MaxFileBytes) throw new RagException("Documents are limited to 10 MiB.", 413);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}
