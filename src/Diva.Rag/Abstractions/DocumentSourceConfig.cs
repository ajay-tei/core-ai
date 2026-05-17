using System.Text.Json;

namespace Diva.Rag.Abstractions;

/// <summary>Configuration for a document source, deserialized from KnowledgeSourceEntity.ConfigJson.</summary>
public sealed class DocumentSourceConfig
{
    public required string SourceId { get; init; }
    public required string SourceType { get; init; }
    public int TenantId { get; init; }
    public string? AgentId { get; init; }
    public string ScopeType { get; init; } = "tenant";
    public MetadataTaxonomy Taxonomy { get; init; } = new();
    public JsonDocument? Config { get; init; }
}
