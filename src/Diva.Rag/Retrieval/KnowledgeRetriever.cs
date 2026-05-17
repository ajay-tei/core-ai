using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Retrieval;

/// <summary>
/// Core retrieval pipeline: embed query → build filter → vector search → assemble context.
/// </summary>
public sealed class KnowledgeRetriever(
    IEmbeddingService embedding,
    IVectorRepository vectorRepo,
    IContextAssembler assembler,
    IOptions<RagOptions> opts,
    ILogger<KnowledgeRetriever> logger) : IKnowledgeRetriever
{
    private readonly RagOptions _opts = opts.Value;

    public async Task<RetrievalResult> RetrieveAsync(string query, KnowledgeFilter filter, CancellationToken ct)
    {
        // 1. Embed the query
        var queryVector = await embedding.EmbedAsync(query, ct);

        // 2. Build metadata filter from the knowledge filter
        var searchOptions = MetadataFilterBuilder.Build(filter);

        // 3. Vector search
        var results = await vectorRepo.SearchAsync(queryVector, searchOptions, ct: ct);

        // 4. Map to retrieved chunks
        var chunks = results.Select(r => new RetrievedChunk
        {
            VectorId = r.VectorId,
            Text = r.Payload.TryGetValue("text", out var text) ? text?.ToString() ?? "" : "",
            Score = r.Score,
            Title = r.Payload.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "",
            SourceUri = r.Payload.TryGetValue("source_uri", out var u) ? u?.ToString() ?? "" : "",
            Domain = r.Payload.TryGetValue("domain", out var d) ? d?.ToString() : null,
            Product = r.Payload.TryGetValue("product", out var p) ? p?.ToString() : null,
            ContentType = r.Payload.TryGetValue("content_type", out var ct2) ? ct2?.ToString() : null,
            DocumentId = r.Payload.TryGetValue("document_id", out var did) ? did?.ToString() ?? "" : "",
            ChunkIndex = r.Payload.TryGetValue("chunk_index", out var ci) && ci is int idx ? idx : 0,
        }).ToList();

        logger.LogDebug("Retrieved {Count} chunks for query '{Query}' (min score: {MinScore})",
            chunks.Count, query.Length > 50 ? query[..50] + "…" : query, filter.MinScore);

        // 5. Assemble context string
        var contextText = assembler.Assemble(chunks, _opts.MaxRetrievalTokens);

        return new RetrievalResult
        {
            Query = query,
            Chunks = chunks,
            AssembledContext = contextText,
        };
    }
}
