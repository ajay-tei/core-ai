namespace Diva.Rag.Abstractions;

/// <summary>
/// A strongly-typed vector representation for agent/user memory.
/// Replaces <see cref="ChunkVector"/> + <c>ExtraPayload</c> in the memory path,
/// carrying only the fields relevant to memory storage and recall.
/// </summary>
public sealed class MemoryVector
{
    public required string VectorId { get; init; }
    public required float[] Embedding { get; init; }
    public required string Content { get; init; }
    public required int TenantId { get; init; }
    public required string MemoryType { get; init; }  // working / episodic / semantic
    public required MemoryScope Scope { get; init; }
    public string? SessionId { get; init; }
    public string[]? Tags { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Discriminated scope for memory ownership.</summary>
public abstract record MemoryScope
{
    /// <summary>Memory scoped to a specific agent (domain knowledge, task learnings).</summary>
    public sealed record Agent(string AgentId) : MemoryScope;

    /// <summary>Memory scoped to a specific user (preferences, identity facts) — visible to all agents.</summary>
    public sealed record User(string UserId, string? AgentId = null) : MemoryScope;
}
