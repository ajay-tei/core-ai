namespace Diva.Rag.Abstractions;

/// <summary>Organizational taxonomy metadata applied to chunks during ingestion.</summary>
public sealed class MetadataTaxonomy
{
    public string? Domain { get; set; }
    public string? Product { get; set; }
    public string? Module { get; set; }
    public string? ContentType { get; set; }
    public string? SecurityLevel { get; set; }
    public string? Owner { get; set; }
    public List<string> CustomTags { get; set; } = [];
}
