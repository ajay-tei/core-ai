namespace Diva.Rag.Abstractions;

/// <summary>
/// Generates dense vector embeddings from text.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Dimensionality of the embedding vectors produced by this service.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single text string.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Embed a batch of texts (up to provider-specific batch limit).</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
