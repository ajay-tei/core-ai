namespace Diva.Rag.Abstractions;

/// <summary>A single chunk retrieved from the vector store with its relevance score.</summary>
public sealed class RetrievedChunk
{
    public string VectorId { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? SourceUri { get; set; }
    public string Text { get; set; } = "";
    public float Score { get; set; }
    public int ChunkIndex { get; set; }
    public string? ScopeType { get; set; }
    public string? Domain { get; set; }
    public string? Product { get; set; }
    public string? ContentType { get; set; }
    public Dictionary<string, string> Tags { get; set; } = [];
    public List<EntityLink> EntityLinks { get; set; } = [];
    public bool IsPinned { get; set; }
}
