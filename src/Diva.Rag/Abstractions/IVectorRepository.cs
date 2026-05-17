namespace Diva.Rag.Abstractions;

/// <summary>
/// Vector database operations for knowledge chunks.
/// </summary>
public interface IVectorRepository
{
    /// <summary>Upsert a batch of chunk vectors into the specified collection.</summary>
    Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, string? collectionOverride = null, CancellationToken ct = default);

    /// <summary>Search for similar vectors.</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryVector, VectorSearchOptions options, CancellationToken ct);

    /// <summary>Mark all vectors for a document as stale.</summary>
    Task MarkStaleAsync(string documentId, string? collectionOverride = null, CancellationToken ct = default);

    /// <summary>Delete all vectors belonging to a specific document.</summary>
    Task DeleteByDocumentAsync(string documentId, string? collectionOverride = null, CancellationToken ct = default);

    /// <summary>Delete all vectors belonging to a specific source.</summary>
    Task DeleteBySourceAsync(string sourceId, string? collectionOverride = null, CancellationToken ct = default);

    /// <summary>Delete specific vector points by their IDs.</summary>
    Task DeleteByIdsAsync(IReadOnlyList<string> vectorIds, string? collectionOverride = null, CancellationToken ct = default);

    // ── Memory-specific operations (typed, no ChunkVector reuse) ──────────

    /// <summary>Upsert memory vectors with a strongly-typed payload (no ExtraPayload bag).</summary>
    Task UpsertMemoryAsync(IReadOnlyList<MemoryVector> memories, string collection, CancellationToken ct = default);

    /// <summary>Search memory vectors with typed scope filtering.</summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchMemoryAsync(float[] queryVector, MemorySearchOptions options, CancellationToken ct);
}

/// <summary>Search options specific to the memory collection.</summary>
public sealed class MemorySearchOptions
{
    public required string Collection { get; init; }
    public int TopK { get; init; } = 5;
    public float ScoreThreshold { get; init; } = 0.3f;
    public required int TenantId { get; init; }
    public MemoryScope? Scope { get; init; }
    public string? MemoryType { get; init; }
    public string? SessionId { get; init; }
    public bool CurrentSessionOnly { get; init; }
}
