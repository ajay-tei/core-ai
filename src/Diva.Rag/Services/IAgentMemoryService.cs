namespace Diva.Rag.Services;

/// <summary>
/// Manages agent memory lifecycle — save, recall, forget, summarize+archive.
/// Stores content in both SQLite (audit trail) and Qdrant (semantic search).
/// </summary>
public interface IAgentMemoryService
{
    Task<Guid> SaveMemoryAsync(int tenantId, string agentId, string content, string memoryType,
        string? sessionId = null, string? userId = null,
        IEnumerable<string>? tags = null, int? expiresInMinutes = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<RecalledMemory>> RecallMemoryAsync(int tenantId, string agentId, string query,
        string? memoryType = null, string? sessionId = null, string? userId = null,
        bool currentSessionOnly = false, int maxResults = 5,
        CancellationToken ct = default);

    Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default);

    Task<int> CleanupExpiredAsync(CancellationToken ct = default);

    Task<int> CleanupSessionAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentMemoryDto>> ListAsync(int tenantId, string? agentId = null,
        string? memoryType = null, int skip = 0, int take = 50, CancellationToken ct = default);
}

public sealed record RecalledMemory(Guid Id, string Content, string MemoryType, string[] Tags, float Score, DateTime CreatedAt, string? UserId = null);

public sealed record AgentMemoryDto(Guid Id, string AgentId, string MemoryType, string Content,
    string? SessionId, string? UserId, string[] Tags, DateTime? ExpiresAt, DateTime CreatedAt);