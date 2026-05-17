namespace Diva.Rag.Abstractions;

/// <summary>Filter criteria for knowledge retrieval queries.</summary>
public sealed class KnowledgeFilter
{
    // Scope filters
    public int TenantId { get; set; }
    public List<int>? GroupIds { get; set; }
    public string? AgentId { get; set; }

    // Taxonomy filters (singular = exact match, array = any-of)
    public string? Domain { get; set; }
    public string? Product { get; set; }
    public string? ContentType { get; set; }
    public string? SecurityLevel { get; set; }
    public string? SourceId { get; set; }

    // Search parameters
    public int TopK { get; set; } = 5;
    public float MinScore { get; set; } = 0.3f;

    // Scope inclusion flags
    public bool IncludeAgentSources { get; set; } = true;
    public bool IncludeGroupSources { get; set; } = true;
    public bool IncludePlatformSources { get; set; } = true;
}
