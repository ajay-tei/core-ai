namespace Diva.Infrastructure.Data.Entities;

/// <summary>Stable document identity across all re-indexes of the same source document.</summary>
public class KnowledgeDocumentEntity : ITenantEntity
{
    /// <summary>Stable ID: Confluence pageId, GitLab file path hash, Jira issue key, SQL object name.</summary>
    public string DocumentId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public string SourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    /// <summary>Latest version number (matches newest KnowledgeDocumentVersionEntity).</summary>
    public int CurrentVersion { get; set; } = 1;
    /// <summary>Latest version from source system (for fast-path diff).</summary>
    public string? ExternalVersion { get; set; }
    /// <summary>SHA-256 of full document content.</summary>
    public string? ContentHash { get; set; }
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastIndexedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
