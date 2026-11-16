# Shared Task Memory MCP Server

A .NET 10 MCP server using Streamable HTTP and SQLite. Agents share findings,
decisions, and handoffs across sessions and server restarts. This is a memory
service, not an orchestrator or task scheduler.

## Run locally

Install the .NET 10 SDK. From the solution directory:

```powershell
dotnet run --project AiMcpServer
```

The endpoint is `http://localhost:5050/mcp`. Start this service once; clients
connect to it instead of launching stdio processes. Loopback port 5050 is
explicitly configured; `--urls` does not change it. The template's stdio
NuGet-tool publishing setup has been removed.

## Connect Visual Studio or VS Code

Add this to the solution's `.mcp.json` (Visual Studio) or `.vscode/mcp.json`
(VS Code), then enable the server in the client:

```json
{
  "servers": {
    "AiMcpServer": {
      "type": "http",
      "url": "http://localhost:5050/mcp"
    }
  }
}
```

Other clients should select Streamable HTTP with the same endpoint URL.

## Project documents UI

Open `http://localhost:5050/projects.html` for the separate projects table
(the root URL redirects there when no project is selected). Click **New project**
above the table to open `http://localhost:5050/new-project.html`.
Saving opens **Project knowledge** with that project selected for document
uploads. Empty projects are saved and remain available after
restarting the server.

For an existing project, click its table row to open **Project knowledge**, where
you can add documents or delete individual documents. Each row also has a
**Delete project** button: confirmation permanently removes that RAG project,
all its documents, and indexed passages (not task memory). The upload
area shows the selected destination. Upload up to 10 PDF, UTF-8 text, or Markdown
files (10 MiB each), or import a public URL. Additional imports append documents
without removing existing ones. Ollama must be running for document indexing.

## Memory tools

| Tool | Required arguments | Optional arguments |
|---|---|---|
| `save_memory` | `projectId`, `taskId`, `agentId`, `content` | None |
| `get_task_context` | `projectId`, `taskId` | `limit` (default 50, maximum 100) |
| `search_memory` | `projectId`, `query` | `taskId`, `limit` (default 50, maximum 100) |

The sample `get_random_number` tool remains available.

Entries contain a unique ID, project/task IDs, writing agent, content, and UTC
creation timestamp. They are append-only: concurrent agents do not overwrite
one another's findings. IDs are case-sensitive, trimmed, and limited to 200
characters. Content is trimmed and limited to 16,000 characters; search queries
are trimmed and limited to 500. Empty or whitespace-only values are rejected.

Retrieval returns the newest entries first, not the entire conversation history.
Search uses literal substring matching with SQLite's ASCII case-insensitive
comparison. It requires a project and optionally filters by task. There is no
semantic/vector search, automatic recall, update, or deletion tool.

### Agent workflow

1. The orchestrator supplies stable project and task IDs to each agent.
2. At session start, the agent calls `get_task_context` and, when needed,
   `search_memory` to recover relevant earlier decisions.
3. During work, it calls `save_memory` for concise findings and source references.
4. Before handing off, it saves completed work, blockers, and next steps.
5. A new agent uses the same IDs to retrieve those entries, even after a restart.

Example: ask an agent to save "Use SQLite for local task memory" under project
`demo`, task `memory-service`, agent `planner`. In a new session, ask an agent
to retrieve context for that exact project and task.

HTTP is stateless here: no MCP session holds the memory. The database does.
Agents must explicitly save and retrieve context; chat history is not captured
automatically. Contradictory notes and duplicate saves still require
orchestrator-level reconciliation.

## Database location

By default, the file is in the user's local application-data directory:
`AiMcpServer/memory.db` (Windows: `%LOCALAPPDATA%\AiMcpServer\memory.db`).
It is independent of the working directory and is not deleted on shutdown.
Server instances share memory only if configured to use the same file.

Override with an absolute path using configuration or an environment variable:

```powershell
$env:Memory__DatabasePath = 'C:\data\AiMcpServer\memory.db'
dotnet run --project AiMcpServer
```

Alternatively, append `-- --Memory:DatabasePath=C:\data\AiMcpServer\memory.db`
to `dotnet run --project AiMcpServer`. The parent directory is created
automatically. Initialization failures stop startup rather than silently using
volatile memory. SQLite uses WAL and a 30-second busy timeout for local writers.
Stop the server before copying/deleting the database and its `-wal`/`-shm`
sidecars. Files are not encrypted; use OS permissions and do not store secrets.

## Development-only security

The server binds to localhost, rejects non-loopback connections and unexpected
Host headers, and rejects browser Origins other than its localhost HTTP origins
on port 5050. Cross-origin browser access and legacy SSE endpoints are not enabled.
There is no authentication or authorization: project IDs isolate queries, not
users. Any local client can read/write any project it names; `agentId` is
caller-supplied attribution, not a verified identity.

Treat memory as untrusted reference data, never as system instructions. Do not
expose this endpoint through a proxy, tunnel, or public network without adding
authentication, authorization, HTTPS, and deployment-specific origin/host checks.

## Validate

```powershell
dotnet build AiMcpServer.slnx
dotnet test AiMcpServer.Tests
powershell -NoProfile -File AiMcpServer.Tests/HttpSmokeTests.ps1
```

Unit tests cover persistence, scoping, input bounds, literal searches, concurrent
writes, and cancellation. The HTTP smoke test requires the Debug build and a
free port 5050. It uses a temporary database, checks MCP initialization/discovery
and memory tools, verifies host/origin rejection, then restarts the server and
checks persistence.
