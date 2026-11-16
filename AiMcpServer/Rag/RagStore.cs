using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AiMcpServer.Rag;

public sealed class RagStore
{
    private readonly string path;
    private readonly string connectionString;

    public RagStore(string databasePath)
    {
        path = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS RagDocuments (
                Id TEXT PRIMARY KEY, ProjectId TEXT NOT NULL, Title TEXT NOT NULL,
                Source TEXT NOT NULL, SourceType TEXT NOT NULL, Model TEXT NOT NULL,
                Dimensions INTEGER NOT NULL, ChunkCount INTEGER NOT NULL, CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RagDocuments_Project ON RagDocuments(ProjectId);
            CREATE TABLE IF NOT EXISTS RagProjects (
                ProjectId TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL
            );
            INSERT OR IGNORE INTO RagProjects (ProjectId, CreatedAt)
                SELECT ProjectId, MIN(CreatedAt) FROM RagDocuments GROUP BY ProjectId;
            CREATE TABLE IF NOT EXISTS RagChunks (
                DocumentId TEXT NOT NULL REFERENCES RagDocuments(Id) ON DELETE CASCADE,
                ChunkIndex INTEGER NOT NULL, Page INTEGER, Content TEXT NOT NULL, Vector BLOB NOT NULL,
                PRIMARY KEY(DocumentId, ChunkIndex)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(RagDocument document, IReadOnlyList<EmbeddedChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        if (chunks.Count is < 1 or > RagLimits.MaxDocumentChunks || document.ChunkCount != chunks.Count)
        {
            throw new RagException("Document has an invalid number of chunks.");
        }
        var normalized = chunks.Select(chunk => RagLimits.Normalize(chunk.Vector)).ToArray();
        if (normalized.Any(vector => vector.Length != document.Dimensions))
        {
            throw new RagException("Embedding dimensions are inconsistent.", 502);
        }
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var ensureProject = connection.CreateCommand();
        ensureProject.Transaction = transaction;
        ensureProject.CommandText = "INSERT OR IGNORE INTO RagProjects (ProjectId, CreatedAt) VALUES ($project, $created);";
        ensureProject.Parameters.AddWithValue("$project", document.ProjectId);
        ensureProject.Parameters.AddWithValue("$created", document.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await ensureProject.ExecuteNonQueryAsync(cancellationToken);

        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            SELECT Model, Dimensions, SUM(ChunkCount) FROM RagDocuments
            WHERE ProjectId = $project GROUP BY Model, Dimensions;
            """;
        check.Parameters.AddWithValue("$project", document.ProjectId);
        var count = 0;
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != document.Model || reader.GetInt32(1) != document.Dimensions)
                {
                    throw new RagException("This project uses a different embedding model or dimension. Delete its documents and reimport with the current model.", 409);
                }
                count += reader.GetInt32(2);
            }
        }
        if (count + chunks.Count > RagLimits.MaxProjectChunks)
        {
            throw new RagException($"A project is limited to {RagLimits.MaxProjectChunks} chunks. Delete documents before importing more.", 409);
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO RagDocuments VALUES ($id, $project, $title, $source, $type, $model, $dimensions, $count, $created);
            """;
        insert.Parameters.AddWithValue("$id", document.Id);
        insert.Parameters.AddWithValue("$project", document.ProjectId);
        insert.Parameters.AddWithValue("$title", document.Title);
        insert.Parameters.AddWithValue("$source", document.Source);
        insert.Parameters.AddWithValue("$type", document.SourceType);
        insert.Parameters.AddWithValue("$model", document.Model);
        insert.Parameters.AddWithValue("$dimensions", document.Dimensions);
        insert.Parameters.AddWithValue("$count", document.ChunkCount);
        insert.Parameters.AddWithValue("$created", document.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var chunkCommand = connection.CreateCommand();
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText = "INSERT INTO RagChunks VALUES ($id, $index, $page, $content, $vector);";
            chunkCommand.Parameters.AddWithValue("$id", document.Id);
            chunkCommand.Parameters.AddWithValue("$index", chunks[index].Chunk.Index);
            chunkCommand.Parameters.AddWithValue("$page", (object?)chunks[index].Chunk.Page ?? DBNull.Value);
            chunkCommand.Parameters.AddWithValue("$content", chunks[index].Chunk.Text);
            var bytes = new byte[normalized[index].Length * sizeof(float)];
            Buffer.BlockCopy(normalized[index], 0, bytes, 0, bytes.Length);
            chunkCommand.Parameters.AddWithValue("$vector", bytes);
            await chunkCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RagDocument>> ListAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM RagDocuments WHERE ProjectId = $project ORDER BY CreatedAt DESC, Id;";
        command.Parameters.AddWithValue("$project", projectId);
        var documents = new List<RagDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new RagDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }
        return documents;
    }

    public async Task<bool> CreateProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO RagProjects (ProjectId, CreatedAt) VALUES ($project, $created);";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<RagProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.ProjectId, COUNT(d.Id), COALESCE(MAX(d.CreatedAt), p.CreatedAt)
            FROM RagProjects p LEFT JOIN RagDocuments d ON d.ProjectId = p.ProjectId
            GROUP BY p.ProjectId ORDER BY p.ProjectId COLLATE NOCASE, p.ProjectId;
            """;
        var projects = new List<RagProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(new RagProject(reader.GetString(0), reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }
        return projects;
    }

    public async Task<bool> DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var documents = connection.CreateCommand();
        documents.Transaction = transaction;
        documents.CommandText = "DELETE FROM RagDocuments WHERE ProjectId = $project;";
        documents.Parameters.AddWithValue("$project", projectId);
        await documents.ExecuteNonQueryAsync(cancellationToken);
        using var project = connection.CreateCommand();
        project.Transaction = transaction;
        project.CommandText = "DELETE FROM RagProjects WHERE ProjectId = $project;";
        project.Parameters.AddWithValue("$project", projectId);
        var deleted = await project.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<bool> DeleteAsync(string projectId, string documentId,
        CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        documentId = RagLimits.Required(documentId, nameof(documentId));
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RagDocuments WHERE ProjectId = $project AND Id = $id;";
        command.Parameters.AddWithValue("$project", projectId);
        command.Parameters.AddWithValue("$id", documentId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<RetrievedPassage>> SearchAsync(string projectId, string model,
        float[] queryVector, int limit, double minimumScore, CancellationToken cancellationToken = default)
    {
        projectId = RagLimits.Required(projectId, nameof(projectId));
        if (limit is < 1 or > 20 || !double.IsFinite(minimumScore) || minimumScore is < -1 or > 1)
        {
            throw new RagException("limit must be 1-20 and minimumScore must be between -1 and 1.");
        }
        var query = RagLimits.Normalize(queryVector);
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.Id, d.Title, d.Source, d.Model, d.Dimensions, c.ChunkIndex, c.Page, c.Content, c.Vector
            FROM RagDocuments d JOIN RagChunks c ON c.DocumentId = d.Id WHERE d.ProjectId = $project;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        var hits = new List<RetrievedPassage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetString(3) != model || reader.GetInt32(4) != query.Length)
            {
                throw new RagException("Stored embeddings do not match the current model/dimensions. Reimport this project's documents.", 409);
            }
            var bytes = (byte[])reader.GetValue(8);
            if (bytes.Length != query.Length * sizeof(float))
            {
                throw new RagException("A stored embedding is invalid. Reimport this project's documents.", 409);
            }
            var vector = new float[query.Length];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            var score = Math.Clamp(vector.Select((value, index) => (double)value * query[index]).Sum(), -1, 1);
            if (score < minimumScore) continue;
            var id = reader.GetString(0);
            var chunk = reader.GetInt32(5);
            int? page = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            var citation = $"{id}#chunk-{chunk}" + (page.HasValue ? $";page={page}" : "");
            hits.Add(new RetrievedPassage(citation, id, reader.GetString(1), reader.GetString(2),
                chunk, page, reader.GetString(7), score));
        }
        return hits.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Citation, StringComparer.Ordinal).Take(limit).ToArray();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
