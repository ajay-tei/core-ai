namespace Diva.Rag.Abstractions;

/// <summary>A raw document fetched from an enterprise source before chunking.</summary>
public sealed class RawDocument
{
    /// <summary>Stable identifier across re-indexes (e.g. Confluence pageId, Jira issue key, file path hash).</summary>
    public required string DocumentId { get; init; }
    public string SourceId { get; init; } = "";
    public required string Title { get; init; }
    public required string Uri { get; init; }
    public required string Content { get; init; }
    public string? ContentType { get; init; }
    /// <summary>Source-system version for fast-path skip (e.g. page.version.number, commit SHA).</summary>
    public string? ExternalVersion { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}
