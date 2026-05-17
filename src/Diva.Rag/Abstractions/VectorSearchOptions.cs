namespace Diva.Rag.Abstractions;

/// <summary>Vector search options passed to IVectorRepository.SearchAsync.</summary>
public sealed class VectorSearchOptions
{
    public int TopK { get; set; } = 5;
    public float ScoreThreshold { get; set; } = 0.45f;
    public Dictionary<string, object>? Filter { get; set; }
    public string? CollectionOverride { get; set; }
}
