using AiMcpServer.Memory;
using Xunit;

namespace AiMcpServer.Tests;

public sealed class TaskMemoryStoreTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "AiMcpServerTests", Guid.NewGuid().ToString("N"));
    private TaskMemoryStore store = null!;

    public async Task InitializeAsync()
    {
        store = new TaskMemoryStore(Path.Combine(directory, "memory.db"));
        await store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SavedMemorySurvivesNewStoreAndRepeatedInitialization()
    {
        var saved = await store.SaveMemoryAsync("project", "task", "agent-1", "Use the existing API.");
        var reopened = new TaskMemoryStore(Path.Combine(directory, "memory.db"));
        await reopened.InitializeAsync();

        var entries = await reopened.GetTaskContextAsync("project", "task");

        Assert.Equal(saved, Assert.Single(entries));
        Assert.Equal(TimeSpan.Zero, saved.CreatedAt.Offset);
    }

    [Fact]
    public async Task ContextIsIsolatedByProjectAndTask()
    {
        var expected = await store.SaveMemoryAsync("project-a", "task-a", "agent", "Relevant");
        await store.SaveMemoryAsync("project-b", "task-a", "agent", "Other project");
        await store.SaveMemoryAsync("project-a", "task-b", "agent", "Other task");

        Assert.Equal(expected, Assert.Single(await store.GetTaskContextAsync("project-a", "task-a")));
        Assert.Empty(await store.GetTaskContextAsync("PROJECT-A", "task-a"));
        Assert.Empty(await store.GetTaskContextAsync("missing", "task-a"));
    }

    [Fact]
    public async Task SearchCanSpanTasksButNeverProjects()
    {
        await store.SaveMemoryAsync("project-a", "task-a", "agent", "SQLite decision");
        await store.SaveMemoryAsync("project-a", "task-b", "agent", "Another SQLITE finding");
        await store.SaveMemoryAsync("project-b", "task-a", "agent", "SQLite secret");
        await store.SaveMemoryAsync("project-a", "task-a", "agent", "Unrelated");

        var projectMatches = await store.SearchMemoryAsync("project-a", "sqlite");
        var taskMatches = await store.SearchMemoryAsync("project-a", "sqlite", "task-b");

        Assert.Equal(2, projectMatches.Count);
        Assert.All(projectMatches, entry => Assert.Equal("project-a", entry.ProjectId));
        Assert.Equal("task-b", Assert.Single(taskMatches).TaskId);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("' OR 1=1 --")]
    public async Task SearchTreatsWildcardsAndSqlAsLiteralText(string query)
    {
        var expected = await store.SaveMemoryAsync("project", "task", "agent", $"Literal {query} value");
        await store.SaveMemoryAsync("project", "task", "agent", "Unrelated text");

        Assert.Equal(expected, Assert.Single(await store.SearchMemoryAsync("project", query)));
    }

    [Fact]
    public async Task ContextReturnsNewestEntriesWithinLimit()
    {
        await store.SaveMemoryAsync("project", "task", "agent", "Old finding");
        await Task.Delay(20);
        var newest = await store.SaveMemoryAsync("project", "task", "agent", "New finding");

        Assert.Equal(newest, Assert.Single(await store.GetTaskContextAsync("project", "task", 1)));
        Assert.Equal(newest, Assert.Single(await store.SearchMemoryAsync("project", "finding", limit: 1)));
    }

    [Fact]
    public async Task ConcurrentAgentsAppendWithoutOverwriting()
    {
        var saved = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(
            () => store.SaveMemoryAsync("project", "task", $"agent-{index}", $"Finding {index}"))));

        var entries = await store.GetTaskContextAsync("project", "task");

        Assert.Equal(12, entries.Count);
        Assert.Equal(12, saved.Select(entry => entry.Id).Distinct().Count());
        Assert.Equal(12, entries.Select(entry => entry.AgentId).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task InvalidLimitsAreRejected(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.GetTaskContextAsync("project", "task", limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.SearchMemoryAsync("project", "query", limit: limit));
    }

    [Fact]
    public async Task InvalidInputIsRejectedWithoutSaving()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync(" ", "task", "agent", "content"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync("project", "", "agent", "content"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync("project", "task", "", "content"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync("project", "task", "agent", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync(new string('p', 201), "task", "agent", "content"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveMemoryAsync("project", "task", "agent", new string('x', 16001)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchMemoryAsync("project", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchMemoryAsync("project", new string('x', 501)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchMemoryAsync("project", "query", " "));
        Assert.Empty(await store.GetTaskContextAsync("project", "task"));
    }

    [Fact]
    public async Task CancelledWriteDoesNotSaveMemory()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveMemoryAsync("project", "task", "agent", "content", cancellation.Token));
        Assert.Empty(await store.GetTaskContextAsync("project", "task"));
    }
}
