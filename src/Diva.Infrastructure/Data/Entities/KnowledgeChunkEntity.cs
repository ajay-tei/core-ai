namespace Diva.Infrastructure.Data.Entities;

/// <summary>Per-vector chunk tracking in SQLite.</summary>
public class KnowledgeChunkEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int DocumentVersion { get; set; }
    public int ChunkIndex { get; set; }
    /// <summary>SHA-256 of chunk text.</summary>
    public string ChunkHash { get; set; } = string.Empty;
    /// <summary>Qdrant point ID.</summary>
    public string VectorId { get; set; } = string.Empty;
    public int TokenCount { get; set; }
    public bool IsStale { get; set; }
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    /// <summary>JSON array of EntityLink references extracted from chunk text.</summary>
    public string? EntityLinksJson { get; set; }
    public bool IsPinned { get; set; }
    public int PinPriority { get; set; }
}
