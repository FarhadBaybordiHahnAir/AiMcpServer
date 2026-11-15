using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AiMcpServer.Memory;

public sealed class TaskMemoryStore
{
    private readonly string databasePath;
    private readonly string connectionString;

    public TaskMemoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS MemoryEntries (
                Id TEXT PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                TaskId TEXT NOT NULL,
                AgentId TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_MemoryEntries_Project_Task_Created
                ON MemoryEntries (ProjectId, TaskId, CreatedAt DESC, Id DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MemoryEntry> SaveMemoryAsync(
        string projectId,
        string taskId,
        string agentId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var entry = new MemoryEntry(
            Guid.NewGuid().ToString("N"),
            ValidateText(projectId, nameof(projectId), 200),
            ValidateText(taskId, nameof(taskId), 200),
            ValidateText(agentId, nameof(agentId), 200),
            ValidateText(content, nameof(content), 16000),
            DateTimeOffset.UtcNow);

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MemoryEntries (Id, ProjectId, TaskId, AgentId, Content, CreatedAt)
            VALUES ($id, $projectId, $taskId, $agentId, $content, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$projectId", entry.ProjectId);
        command.Parameters.AddWithValue("$taskId", entry.TaskId);
        command.Parameters.AddWithValue("$agentId", entry.AgentId);
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return entry;
    }

    public Task<IReadOnlyList<MemoryEntry>> GetTaskContextAsync(
        string projectId,
        string taskId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        projectId = ValidateText(projectId, nameof(projectId), 200);
        taskId = ValidateText(taskId, nameof(taskId), 200);
        ValidateLimit(limit);
        return ReadAsync(projectId, taskId, null, limit, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(
        string projectId,
        string query,
        string? taskId = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        projectId = ValidateText(projectId, nameof(projectId), 200);
        query = ValidateText(query, nameof(query), 500);
        if (taskId is not null)
        {
            taskId = ValidateText(taskId, nameof(taskId), 200);
        }
        ValidateLimit(limit);
        return ReadAsync(projectId, taskId, query, limit, cancellationToken);
    }

    private async Task<IReadOnlyList<MemoryEntry>> ReadAsync(
        string projectId,
        string? taskId,
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ProjectId, TaskId, AgentId, Content, CreatedAt
            FROM MemoryEntries
            WHERE ProjectId = $projectId
                AND ($taskId IS NULL OR TaskId = $taskId)
                AND ($query IS NULL OR instr(lower(Content), lower($query)) > 0)
            ORDER BY CreatedAt DESC, Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$taskId", (object?)taskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MemoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.None)));
        }
        return entries;
    }

    private static string ValidateText(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Must contain no more than {maxLength} characters.", parameterName);
        }
        return value.Trim();
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        }
    }
}
