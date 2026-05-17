namespace Diva.Rag.Abstractions;

/// <summary>
/// Connects to an enterprise data source and yields raw documents for ingestion.
/// </summary>
public interface IDocumentConnector
{
    /// <summary>Source type identifier (e.g. "File", "Http", "Confluence").</summary>
    string SourceType { get; }

    /// <summary>
    /// Enumerate all documents from the configured source.
    /// </summary>
    IAsyncEnumerable<RawDocument> ConnectAsync(DocumentSourceConfig source, CancellationToken ct);
}
