namespace Diva.Infrastructure.Data.Entities;

/// <summary>Tracks ingestion job lifecycle.</summary>
public class IngestionJobEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    /// <summary>null = full source; non-null = single-document webhook re-index.</summary>
    public string? DocumentUri { get; set; }
    /// <summary>"Pending"|"Running"|"Completed"|"Failed"|"Canceled"</summary>
    public string Status { get; set; } = "Pending";
    public int DocumentsProcessed { get; set; }
    public int ChunksAdded { get; set; }
    public int ChunksUpdated { get; set; }
    public int ChunksSkipped { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>"manual"|"scheduled"|"webhook"</summary>
    public string TriggerType { get; set; } = "manual";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
