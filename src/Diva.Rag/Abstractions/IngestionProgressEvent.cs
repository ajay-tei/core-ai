namespace Diva.Rag.Abstractions;

/// <summary>Progress event emitted during document ingestion.</summary>
public sealed class IngestionProgressEvent
{
    public required string Phase { get; init; }
    public int DocumentsProcessed { get; init; }
    public int TotalDocuments { get; init; }
    public int ChunksAdded { get; init; }
    public int ChunksUpdated { get; init; }
    public int ChunksSkipped { get; init; }
    public string? Error { get; init; }
    public string? CurrentDocument { get; init; }
}
