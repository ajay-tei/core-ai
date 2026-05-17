namespace Diva.Infrastructure.Data.Entities;

/// <summary>Immutable snapshot per re-index of a document.</summary>
public class KnowledgeDocumentVersionEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    /// <summary>SHA-256 of full document text at this version.</summary>
    public string ContentHash { get; set; } = string.Empty;
    public string? ExternalVersion { get; set; }
    /// <summary>"initial_ingest"|"webhook_update"|"manual_sync"|"scheduled_sync"</summary>
    public string Source { get; set; } = "initial_ingest";
    public int ChunksAdded { get; set; }
    public int ChunksUpdated { get; set; }
    public int ChunksRemoved { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
