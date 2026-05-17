using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace Diva.Rag.VectorStore;

/// <summary>
/// Ensures Qdrant collections exist with correct schema and payload indexes (idempotent).
/// </summary>
public sealed class QdrantCollectionManager(
    IOptions<RagOptions> opts,
    ILogger<QdrantCollectionManager> logger)
{
    private readonly RagOptions _opts = opts.Value;

    private static readonly string[] KnowledgePayloadIndexes =
    [
        "scope_type", "scope_id", "agent_id", "is_stale", "is_pinned",
        "domain", "product", "module", "content_type", "security_level",
        "source_id", "document_id"
    ];

    private static readonly string[] MemoryPayloadIndexes =
    [
        "tenant_id", "agent_id", "user_id", "session_id", "memory_type", "expires_at", "scope"
    ];

    public async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var client = CreateClient();
        var name = _opts.CollectionName;

        if (await client.CollectionExistsAsync(name, ct))
        {
            logger.LogDebug("Qdrant collection '{Collection}' already exists", name);
        }
        else
        {
            await client.CreateCollectionAsync(name, new VectorParams
            {
                Size = (ulong)_opts.EmbeddingDimensions,
                Distance = Distance.Cosine,
            }, cancellationToken: ct);
            logger.LogInformation("Created Qdrant collection '{Collection}' (dim={Dim})", name, _opts.EmbeddingDimensions);
        }

        // Ensure payload indexes (idempotent — Qdrant ignores if already exists)
        foreach (var field in KnowledgePayloadIndexes)
        {
            try
            {
                await client.CreatePayloadIndexAsync(name, field,
                    field is "scope_id" ? PayloadSchemaType.Integer : PayloadSchemaType.Keyword,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Idempotent — index already created
            }
        }

        logger.LogDebug("Qdrant payload indexes verified for '{Collection}'", name);
    }

    /// <summary>
    /// Ensures the agent memory Qdrant collection exists with correct schema and payload indexes.
    /// Separate collection from knowledge — same dimensions, different namespace.
    /// </summary>
    public async Task EnsureMemoryCollectionAsync(CancellationToken ct)
    {
        if (!_opts.EnableAgentMemory) return;

        var client = CreateClient();
        var name = _opts.MemoryCollectionName;

        if (await client.CollectionExistsAsync(name, ct))
        {
            logger.LogDebug("Qdrant memory collection '{Collection}' already exists", name);
        }
        else
        {
            await client.CreateCollectionAsync(name, new VectorParams
            {
                Size = (ulong)_opts.EmbeddingDimensions,
                Distance = Distance.Cosine,
            }, cancellationToken: ct);
            logger.LogInformation("Created Qdrant memory collection '{Collection}' (dim={Dim})", name, _opts.EmbeddingDimensions);
        }

        foreach (var field in MemoryPayloadIndexes)
        {
            try
            {
                await client.CreatePayloadIndexAsync(name, field,
                    field is "tenant_id" ? PayloadSchemaType.Integer : PayloadSchemaType.Keyword,
                    cancellationToken: ct);
            }
            catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Idempotent — index already created
            }
        }

        logger.LogDebug("Qdrant memory payload indexes verified for '{Collection}'", name);
    }

    private QdrantClient CreateClient()
    {
        var uri = new Uri(_opts.QdrantGrpcUrl);
        return new QdrantClient(uri.Host, uri.Port, apiKey: _opts.QdrantApiKey);
    }
}
