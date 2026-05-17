namespace Diva.Rag.Abstractions;

/// <summary>
/// Splits a raw document into smaller chunks suitable for embedding.
/// </summary>
public interface IDocumentChunker
{
    /// <summary>
    /// Split document content into chunks.
    /// </summary>
    IReadOnlyList<DocumentChunk> Chunk(string content, ChunkingOptions options);
}
