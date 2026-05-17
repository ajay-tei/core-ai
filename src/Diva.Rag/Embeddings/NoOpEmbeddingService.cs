using Diva.Rag.Abstractions;

namespace Diva.Rag.Embeddings;

/// <summary>
/// Fallback embedding service used when no embedding provider is configured.
/// Returns zero vectors and logs a warning when invoked.
/// </summary>
public sealed class NoOpEmbeddingService : IEmbeddingService
{
    public int Dimensions => 1536;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        Task.FromResult(new float[Dimensions]);

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<float[]>>(
            texts.Select(_ => new float[Dimensions]).ToList());
}
