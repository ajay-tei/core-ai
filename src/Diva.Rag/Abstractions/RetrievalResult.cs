namespace Diva.Rag.Abstractions;

/// <summary>Result of a knowledge retrieval query.</summary>
public sealed class RetrievalResult
{
    public string Query { get; init; } = "";
    public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
    public string AssembledContext { get; init; } = "";
}
