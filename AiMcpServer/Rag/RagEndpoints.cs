using Microsoft.Data.Sqlite;

namespace AiMcpServer.Rag;

public sealed record CreateProjectRequest(string ProjectId);
public sealed record ImportUrlRequest(string ProjectId, string Url);
public sealed record DocumentSearchRequest(string ProjectId, string Query, int Limit = 5, double MinimumScore = 0);

public static class RagEndpoints
{
    public static void MapRagEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/rag");
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            if ((HttpMethods.IsPost(request.Method) || HttpMethods.IsDelete(request.Method))
                && request.Headers["X-Rag-Request"] != "1")
            {
                return Results.Json(new { error = "The X-Rag-Request: 1 header is required." }, statusCode: 403);
            }
            try
            {
                return await next(context);
            }
            catch (RagException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "Invalid multipart upload or file size limit exceeded." });
            }
            catch (SqliteException exception)
            {
                app.Logger.LogError(exception, "RAG database operation failed");
                return Results.Json(new { error = "The RAG database is unavailable. Check server logs and retry." }, statusCode: 503);
            }
        });

        group.MapGet("/projects", async (RagStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListProjectsAsync(cancellationToken)));

        group.MapPost("/projects", async (CreateProjectRequest input, RagStore store, CancellationToken cancellationToken) =>
        {
            var projectId = RagLimits.Required(input.ProjectId, nameof(input.ProjectId));
            if (!await store.CreateProjectAsync(projectId, cancellationToken))
            {
                throw new RagException($"Project '{projectId}' already exists.", 409);
            }
            return Results.Created("/api/rag/projects", new { projectId });
        });

        group.MapDelete("/projects", async (string projectId, RagStore store, CancellationToken cancellationToken) =>
            await store.DeleteProjectAsync(projectId, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapGet("/documents", async (string projectId, RagStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(projectId, cancellationToken)));

        group.MapDelete("/documents/{documentId}", async (string documentId, string projectId,
            RagStore store, CancellationToken cancellationToken) =>
            await store.DeleteAsync(projectId, documentId, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/upload", async (HttpRequest request, RagService service, CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType) throw new RagException("Use a multipart/form-data upload.");
            var form = await request.ReadFormAsync(cancellationToken);
            var project = RagLimits.Required(form["projectId"].ToString(), "projectId");
            var files = form.Files;
            if (files.Count is < 1 or > RagLimits.MaxUploadFiles || files.Any(file => file.Name != "file"))
            {
                throw new RagException($"Upload 1-{RagLimits.MaxUploadFiles} files named 'file'.");
            }
            foreach (var file in files)
            {
                if (file.Length is 0 or > RagLimits.MaxFileBytes)
                {
                    throw new RagException("Each upload must be non-empty and at most 10 MiB.", 413);
                }
                var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (extension is not (".pdf" or ".txt" or ".md"))
                {
                    throw new RagException("Upload .pdf, .txt, or .md files.");
                }
            }

            var documents = new List<RagDocument>(files.Count);
            foreach (var file in files)
            {
                await using var stream = file.OpenReadStream();
                var bytes = await DocumentExtractor.ReadBoundedAsync(stream, cancellationToken);
                documents.Add(await service.ImportFileAsync(project, file.FileName, bytes, cancellationToken));
            }
            return Results.Ok(documents);
        });

        group.MapPost("/url", async (ImportUrlRequest input, RagService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ImportUrlAsync(input.ProjectId, input.Url, cancellationToken)));

        group.MapPost("/search", async (DocumentSearchRequest input, RagService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SearchAsync(input.ProjectId, input.Query, input.Limit, input.MinimumScore, cancellationToken)));
    }
}
