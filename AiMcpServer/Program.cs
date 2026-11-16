using AiMcpServer.Memory;
using AiMcpServer.Rag;
using Microsoft.AspNetCore.Http.Features;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5050);
    options.Limits.MaxRequestBodySize = RagLimits.MaxUploadRequestBytes;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = RagLimits.MaxUploadRequestBytes);

var databasePath = builder.Configuration["Memory:DatabasePath"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiMcpServer",
        "memory.db");
}
builder.Services.AddSingleton(new TaskMemoryStore(databasePath));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
var ragDatabasePath = builder.Configuration["Rag:DatabasePath"];
if (string.IsNullOrWhiteSpace(ragDatabasePath))
{
    ragDatabasePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "rag.db");
}
builder.Services.AddSingleton(new RagStore(ragDatabasePath));
builder.Services.AddSingleton<DocumentExtractor>();
builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHttpClient<WebDocumentFetcher>()
    .ConfigurePrimaryHttpMessageHandler(WebDocumentFetcher.CreateHandler);
builder.Services.AddSingleton<RagService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<RandomNumberTools>()
    .WithTools<TaskMemoryTools>()
    .WithTools<DocumentSearchTools>();

var app = builder.Build();
await app.Services.GetRequiredService<TaskMemoryStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<RagStore>().InitializeAsync(app.Lifetime.ApplicationStopping);

app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)
        || !(host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1" || host == "[::1]"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var origin)
        && (origin.Count != 1 || !(origin[0] == "http://localhost:5050"
            || origin[0] == "http://127.0.0.1:5050" || origin[0] == "http://[::1]:5050")))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
});
app.MapRagEndpoints();
app.MapMcp("/mcp");
await app.RunAsync();

public partial class Program;
