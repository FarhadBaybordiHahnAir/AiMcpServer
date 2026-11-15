using System.Text;
using AiMcpServer.Rag;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace AiMcpServer.Tests;

public sealed class DocumentExtractionTests
{
    [Fact]
    public void TextIsSplitIntoBoundedOverlappingChunksWithoutCrossingPages()
    {
        var first = string.Concat(Enumerable.Repeat("SQLite reference text. ", 200));
        var chunks = DocumentExtractor.Chunk([new TextPage(1, first), new TextPage(2, "Second page")]);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, 1200));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(chunk => chunk.Index));
        Assert.Equal(2, chunks[^1].Page);
        Assert.Equal("Second page", chunks[^1].Text);
        Assert.Contains(chunks[0].Text[^100..], chunks[1].Text);
    }

    [Fact]
    public void HtmlDropsScriptsNavigationAndPreservesTitle()
    {
        const string html = "<html><head><title>Project &amp; Guide</title><script>secretScript()</script></head><body><nav>Navigation</nav><main><h1>SQLite</h1><p>Durable &amp; local memory.</p></main><style>bad-style</style></body></html>";
        var extracted = new DocumentExtractor().Extract(Encoding.UTF8.GetBytes(html), "html", "fallback");

        Assert.Equal("Project & Guide", extracted.Title);
        var text = Assert.Single(extracted.Pages).Text;
        Assert.Contains("Durable & local memory.", text);
        Assert.DoesNotContain("secretScript", text);
        Assert.DoesNotContain("Navigation", text);
        Assert.DoesNotContain("bad-style", text);
    }

    [Fact]
    public void PdfPreservesOneBasedPageNumbers()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("SQLite first page", 12, new PdfPoint(50, 700), font);
        builder.AddPage(PageSize.A4).AddText("Authentication second page", 12, new PdfPoint(50, 700), font);

        var extracted = new DocumentExtractor().Extract(builder.Build(), "pdf", "reference.pdf");

        Assert.Equal(2, extracted.Pages.Count);
        Assert.Equal(1, extracted.Pages[0].Page);
        Assert.Contains("SQLite", extracted.Pages[0].Text);
        Assert.Equal(2, extracted.Pages[1].Page);
        Assert.Contains("Authentication", extracted.Pages[1].Text);
    }

    [Fact]
    public void EmptyScannedPdfIsRejected()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        var error = Assert.Throws<RagException>(() => new DocumentExtractor().Extract(builder.Build(), "pdf", "scan.pdf"));
        Assert.Contains("OCR", error.Message);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("text\0binary")]
    public void InvalidTextIsRejected(string text)
    {
        Assert.Throws<RagException>(() => new DocumentExtractor().Extract(Encoding.UTF8.GetBytes(text), "text", "bad.txt"));
    }

    [Fact]
    public void OversizedTextAndCorruptPdfAreRejected()
    {
        var extractor = new DocumentExtractor();
        Assert.Throws<RagException>(() => extractor.Extract(Encoding.UTF8.GetBytes(new string('x', 500001)), "text", "large.txt"));
        Assert.Throws<RagException>(() => extractor.Extract([1, 2, 3], "pdf", "invalid.pdf"));
        Assert.Throws<RagException>(() => extractor.Extract([0xFF, 0xFE], "text", "invalid.txt"));
    }
}
