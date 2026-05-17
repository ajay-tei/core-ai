namespace Diva.Rag.Abstractions;

/// <summary>A document chunk with its computed embedding vector.</summary>
public sealed class ChunkVector
{
    public required string VectorId { get; init; }
    public required DocumentChunk Chunk { get; init; }
    public required float[] Dense { get; init; }

    // Qdrant payload fields
    public required string ScopeType { get; init; }
    public required int ScopeId { get; init; }
    public required string SourceId { get; init; }
    public string? AgentId { get; init; }
    public string? Title { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceType { get; init; }
    public string? ExternalVersion { get; init; }
    public bool IsStale { get; init; }
    public bool IsPinned { get; init; }
    public int DocumentVersion { get; init; }

    /// <summary>
    /// Additional arbitrary payload fields to write to Qdrant.
    /// Used by agent memory to store memory_type, session_id, expires_at, tenant_id, etc.
    /// Keys map to Qdrant payload field names. Supported value types: string, int, long, float, bool, DateTime.
    /// </summary>
    public Dictionary<string, object>? ExtraPayload { get; init; }
}
