namespace AiMcpServer.Memory;

public sealed record MemoryEntry(
    string Id,
    string ProjectId,
    string TaskId,
    string AgentId,
    string Content,
    DateTimeOffset CreatedAt);
