namespace Diva.Rag.Abstractions;

/// <summary>
/// Enriches chunk metadata with organizational taxonomy tags.
/// </summary>
public interface IMetadataEnricher
{
    /// <summary>
    /// Enrich a chunk's metadata with taxonomy values from the source configuration.
    /// </summary>
    DocumentChunk Enrich(DocumentChunk chunk, MetadataTaxonomy taxonomy);
}
