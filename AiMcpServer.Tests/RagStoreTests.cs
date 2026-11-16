using AiMcpServer.Rag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiMcpServer.Tests;

public sealed class RagStoreTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AiMcpRagTests", Guid.NewGuid().ToString("N"));
    private RagStore store = null!;

    public async Task InitializeAsync()
    {
        store = new RagStore(Path.Combine(directory, "rag.db"));
        await store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    private static RagDocument Document(string id, string project = "project", string model = "test-model", int count = 1)
        => new(id, project, "Reference", "reference.pdf", "pdf", model, 2, count, DateTimeOffset.UtcNow);

    [Fact]
    public async Task VectorsAndPageCitationsSurviveReopening()
    {
        await store.SaveAsync(Document("doc-1"), [new(new TextChunk(0, 3, "SQLite reference"), [3, 0])]);
        var reopened = new RagStore(Path.Combine(directory, "rag.db"));
        await reopened.InitializeAsync();

        var hit = Assert.Single(await reopened.SearchAsync("project", "test-model", [4, 0], 5, 0));

        Assert.Equal("doc-1#chunk-0;page=3", hit.Citation);
        Assert.Equal(3, hit.Page);
        Assert.Equal("reference.pdf", hit.Source);
        Assert.Equal("SQLite reference", hit.Text);
        Assert.Equal(1, hit.Score, 5);
        Assert.Equal("test-model", Assert.Single(await reopened.ListAsync("project")).Model);
    }

    [Fact]
    public async Task CreateProjectAddsAnEmptyProjectAndRejectsDuplicateNames()
    {
        Assert.True(await store.CreateProjectAsync("empty-project"));
        Assert.False(await store.CreateProjectAsync("empty-project"));

        var project = Assert.Single(await store.ListProjectsAsync());

        Assert.Equal("empty-project", project.ProjectId);
        Assert.Equal(0, project.DocumentCount);
    }

    [Fact]
    public async Task ProjectListSummarizesDocumentsAndLatestImport()
    {
        var latest = DateTimeOffset.UtcNow;
        var older = latest.AddMinutes(-1);
        await store.SaveAsync(Document("doc-1") with { CreatedAt = older }, [new(new TextChunk(0, null, "First"), [1, 0])]);
        await store.SaveAsync(Document("doc-2") with { CreatedAt = latest }, [new(new TextChunk(0, null, "Second"), [1, 0])]);
        await store.SaveAsync(Document("other-project-doc", "another-project"), [new(new TextChunk(0, null, "Other"), [1, 0])]);

        var listed = await store.ListProjectsAsync();

        Assert.Equal(2, listed.Count);
        Assert.Equal("another-project", listed[0].ProjectId);
        Assert.Equal(1, listed[0].DocumentCount);
        Assert.Equal("project", listed[1].ProjectId);
        Assert.Equal(2, listed[1].DocumentCount);
        Assert.Equal(latest, listed[1].UpdatedAt);
    }

    [Fact]
    public async Task SearchFiltersProjectBeforeRankingAndAppliesLimits()
    {
        await store.SaveAsync(Document("best"), [new(new TextChunk(0, null, "Best"), [1, 0])]);
        await store.SaveAsync(Document("other"), [new(new TextChunk(0, null, "Other"), [0, 1])]);
        await store.SaveAsync(Document("private", "another-project"), [new(new TextChunk(0, null, "Private"), [1, 0])]);

        Assert.Equal("best", Assert.Single(await store.SearchAsync("project", "test-model", [1, 0], 1, -1)).DocumentId);
        Assert.Single(await store.SearchAsync("project", "test-model", [1, 0], 20, 0.9));
        Assert.Equal(2, (await store.SearchAsync("project", "test-model", [1, 0], 20, -1)).Count);
        Assert.Empty(await store.SearchAsync("PROJECT", "test-model", [1, 0], 5, 0));
    }

    [Fact]
    public async Task ModelAndDimensionChangesAreRejected()
    {
        await store.SaveAsync(Document("existing"), [new(new TextChunk(0, null, "Text"), [1, 0])]);

        await Assert.ThrowsAsync<RagException>(() => store.SearchAsync("project", "different-model", [1, 0], 5, 0));
        await Assert.ThrowsAsync<RagException>(() => store.SearchAsync("project", "test-model", [1, 0, 0], 5, 0));
        await Assert.ThrowsAsync<RagException>(() => store.SaveAsync(Document("new", model: "different-model"),
            [new(new TextChunk(0, null, "Text"), [1, 0])]));
        Assert.Single(await store.ListAsync("project"));
    }

    [Fact]
    public async Task DeleteIsProjectScopedAndCascadesToChunks()
    {
        await store.SaveAsync(Document("doc"), [new(new TextChunk(0, 1, "Text"), [1, 0])]);
        Assert.False(await store.DeleteAsync("wrong-project", "doc"));
        Assert.True(await store.DeleteAsync("project", "doc"));
        Assert.Empty(await store.ListAsync("project"));
        var project = Assert.Single(await store.ListProjectsAsync());
        Assert.Equal("project", project.ProjectId);
        Assert.Equal(0, project.DocumentCount);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "rag.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RagChunks;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeleteProjectRemovesOnlyItsDocumentsAndChunks()
    {
        await store.SaveAsync(Document("first", "target"), [new(new TextChunk(0, null, "First"), [1, 0])]);
        await store.SaveAsync(Document("second", "target"), [new(new TextChunk(0, null, "Second"), [1, 0])]);
        await store.SaveAsync(Document("other", "other"), [new(new TextChunk(0, null, "Other"), [1, 0])]);

        Assert.False(await store.DeleteProjectAsync("TARGET"));
        Assert.True(await store.DeleteProjectAsync("target"));
        Assert.False(await store.DeleteProjectAsync("target"));
        Assert.Empty(await store.ListAsync("target"));
        Assert.Equal("other", Assert.Single(await store.ListProjectsAsync()).ProjectId);
        Assert.Single(await store.SearchAsync("other", "test-model", [1, 0], 5, 0));
        var reopened = new RagStore(Path.Combine(directory, "rag.db"));
        await reopened.InitializeAsync();
        Assert.Equal("other", Assert.Single(await reopened.ListProjectsAsync()).ProjectId);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "rag.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM RagChunks;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EmptyProjectsCanBeDeletedAndInvalidNamesAreRejected()
    {
        await store.CreateProjectAsync("empty");
        Assert.True(await store.DeleteProjectAsync("empty"));
        Assert.Empty(await store.ListProjectsAsync());
        await Assert.ThrowsAsync<RagException>(() => store.DeleteProjectAsync(" "));
    }

    [Fact]
    public async Task FailedChunkInsertRollsBackEntireDocument()
    {
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(Document("doc", count: 2),
            [new(new TextChunk(0, null, "First"), [1, 0]), new(new TextChunk(0, null, "Duplicate index"), [1, 0])]));
        Assert.Empty(await store.ListAsync("project"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task InvalidRetrievalLimitsAreRejected(int limit)
    {
        await Assert.ThrowsAsync<RagException>(() => store.SearchAsync("project", "test-model", [1, 0], limit, 0));
    }

    [Fact]
    public void InvalidVectorsAreRejected()
    {
        Assert.Throws<RagException>(() => RagLimits.Normalize([]));
        Assert.Throws<RagException>(() => RagLimits.Normalize([0, 0]));
        Assert.Throws<RagException>(() => RagLimits.Normalize([float.NaN, 1]));
        Assert.Throws<RagException>(() => RagLimits.Normalize([float.PositiveInfinity]));
    }
}
