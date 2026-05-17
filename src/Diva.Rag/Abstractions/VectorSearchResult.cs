namespace Diva.Rag.Abstractions;

/// <summary>A single result from a vector similarity search.</summary>
public sealed class VectorSearchResult
{
    public required string VectorId { get; init; }
    public required float Score { get; init; }
    public Dictionary<string, object> Payload { get; init; } = [];
}
