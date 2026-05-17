namespace Diva.Rag.Abstractions;

/// <summary>
/// Retrieves relevant knowledge chunks for a query, applying scope filters and reranking.
/// </summary>
public interface IKnowledgeRetriever
{
    /// <summary>
    /// Retrieve knowledge chunks matching the query, using the provided filter for scoping.
    /// </summary>
    Task<RetrievalResult> RetrieveAsync(
        string query,
        KnowledgeFilter filter,
        CancellationToken ct = default);
}
