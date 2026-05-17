namespace Diva.Rag.Abstractions;

/// <summary>
/// Orchestrates the full document ingestion pipeline: connect → chunk → diff → embed → upsert.
/// </summary>
public interface IDocumentIngestionPipeline
{
    /// <summary>
    /// Ingest documents from a knowledge source. Progress is reported via the optional callback.
    /// </summary>
    Task IngestAsync(
        string sourceId,
        int tenantId,
        string? documentUri = null,
        Action<IngestionProgressEvent>? onProgress = null,
        CancellationToken ct = default);
}
