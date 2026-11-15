using Microsoft.Data.Sqlite;

namespace AiMcpServer.Rag;

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
            if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new RagException("Upload exactly one file named 'file'.");
            var file = form.Files[0];
            if (file.Length is 0 or > RagLimits.MaxFileBytes) throw new RagException("Upload a non-empty file of at most 10 MiB.", 413);
            await using var stream = file.OpenReadStream();
            var bytes = await DocumentExtractor.ReadBoundedAsync(stream, cancellationToken);
            var document = await service.ImportFileAsync(project, file.FileName, bytes, cancellationToken);
            return Results.Ok(document);
        });

        group.MapPost("/url", async (ImportUrlRequest input, RagService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ImportUrlAsync(input.ProjectId, input.Url, cancellationToken)));

        group.MapPost("/search", async (DocumentSearchRequest input, RagService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.SearchAsync(input.ProjectId, input.Query, input.Limit, input.MinimumScore, cancellationToken)));
    }
}
