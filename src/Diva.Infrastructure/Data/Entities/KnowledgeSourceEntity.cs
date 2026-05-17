namespace Diva.Infrastructure.Data.Entities;

public class KnowledgeSourceEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>"tenant"|"group"|"platform"|"agent"</summary>
    public string ScopeType { get; set; } = "tenant";
    /// <summary>FK to TenantGroupEntity for group scope; null otherwise.</summary>
    public string? GroupId { get; set; }
    /// <summary>FK to AgentDefinitionEntity for agent scope; null otherwise.</summary>
    public string? AgentId { get; set; }
    /// <summary>"File"|"Http"|"Confluence"|"Jira"|"GitLab"|"SqlServer"|"SharePoint"</summary>
    public string SourceType { get; set; } = "File";
    /// <summary>Source-type-specific config JSON (paths, URLs, credentials, etc.).</summary>
    public string? ConfigJson { get; set; }
    /// <summary>MetadataTaxonomy JSON: domain, product, module, contentType, securityLevel, owner.</summary>
    public string? TaxonomyJson { get; set; }
    /// <summary>"Active"|"Paused"</summary>
    public string Status { get; set; } = "Active";
    /// <summary>HMAC-SHA256 hash of webhook secret for webhook-triggered re-indexing.</summary>
    public string? WebhookSecretHash { get; set; }
    public DateTime? LastIngestedAt { get; set; }
    public int DocumentCount { get; set; }
    public int ChunkCount { get; set; }
    public bool ScheduleEnabled { get; set; }
    public string? ScheduleCron { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
