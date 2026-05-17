using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace Diva.Rag.VectorStore;

/// <summary>
/// Qdrant gRPC vector repository. Handles upsert, search, stale marking, and deletion.
/// </summary>
public sealed class QdrantVectorRepository : IVectorRepository
{
    private readonly RagOptions _opts;
    private readonly ILogger<QdrantVectorRepository> _logger;

    public QdrantVectorRepository(IOptions<RagOptions> opts, ILogger<QdrantVectorRepository> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task UpsertAsync(IReadOnlyList<ChunkVector> vectors, string? collectionOverride = null, CancellationToken ct = default)
    {
        if (vectors.Count == 0) return;

        var client = CreateClient();
        var collection = collectionOverride ?? _opts.CollectionName;

        var points = vectors.Select(v =>
        {
            var payload = new Dictionary<string, Value>
            {
                ["scope_type"] = v.ScopeType,
                ["scope_id"] = v.ScopeId,
                ["source_id"] = v.SourceId,
                ["document_id"] = v.Chunk.DocumentId,
                ["document_version"] = v.DocumentVersion,
                ["chunk_index"] = v.Chunk.ChunkIndex,
                ["text"] = v.Chunk.Text,
                ["title"] = v.Title ?? "",
                ["source_uri"] = v.SourceUri ?? "",
                ["source_type"] = v.SourceType ?? "",
                ["external_version"] = v.ExternalVersion ?? "",
                ["is_stale"] = v.IsStale,
                ["is_pinned"] = v.IsPinned,
                ["token_count"] = v.Chunk.TokenCount,
                ["indexed_at"] = DateTime.UtcNow.ToString("o"),
            };

            if (v.Chunk.Domain is not null) payload["domain"] = v.Chunk.Domain;
            if (v.Chunk.Product is not null) payload["product"] = v.Chunk.Product;
            if (v.Chunk.Module is not null) payload["module"] = v.Chunk.Module;
            if (v.Chunk.ContentType is not null) payload["content_type"] = v.Chunk.ContentType;
            if (v.Chunk.SecurityLevel is not null) payload["security_level"] = v.Chunk.SecurityLevel;
            if (v.Chunk.Owner is not null) payload["owner"] = v.Chunk.Owner;
            if (v.AgentId is not null) payload["agent_id"] = v.AgentId;

            // Merge any extra payload fields (e.g. memory_type, session_id, tenant_id for agent memory)
            if (v.ExtraPayload is { Count: > 0 })
            {
                foreach (var (key, val) in v.ExtraPayload)
                {
                    payload[key] = val switch
                    {
                        string s   => new Value { StringValue = s },
                        int i      => new Value { IntegerValue = i },
                        long l     => new Value { IntegerValue = l },
                        float f    => new Value { DoubleValue = f },
                        double d   => new Value { DoubleValue = d },
                        bool b     => new Value { BoolValue = b },
                        DateTime dt => new Value { StringValue = dt.ToString("o") },
                        _          => new Value { StringValue = val.ToString()! },
                    };
                }
            }

            return new PointStruct
            {
                Id = new PointId { Uuid = v.VectorId },
                Vectors = v.Dense,
                Payload = { payload },
            };
        }).ToList();

        await client.UpsertAsync(collection, points, cancellationToken: ct);
        _logger.LogDebug("Upserted {Count} vectors to '{Collection}'", points.Count, collection);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryVector, VectorSearchOptions options, CancellationToken ct)
    {
        var client = CreateClient();
        var collection = options.CollectionOverride ?? _opts.CollectionName;

        Filter? filter = null;
        if (options.Filter is { Count: > 0 })
        {
            filter = BuildFilter(options.Filter);
        }

        var results = await client.SearchAsync(
            collection,
            queryVector,
            limit: (ulong)options.TopK,
            scoreThreshold: options.ScoreThreshold,
            filter: filter,
            cancellationToken: ct);

        return results.Select(r => new VectorSearchResult
        {
            VectorId = r.Id.Uuid,
            Score = r.Score,
            Payload = r.Payload.ToDictionary(
                kv => kv.Key,
                kv => PayloadToObject(kv.Value)),
        }).ToList();
    }

    public async Task MarkStaleAsync(string documentId, string? collectionOverride = null, CancellationToken ct = default)
    {
        var client = CreateClient();
        var collection = collectionOverride ?? _opts.CollectionName;

        await client.SetPayloadAsync(
            collection,
            new Dictionary<string, Value> { ["is_stale"] = true },
            MatchKeyword("document_id", documentId),
            cancellationToken: ct);

        _logger.LogDebug("Marked vectors stale for document '{DocId}' in '{Collection}'", documentId, collection);
    }

    public async Task DeleteByDocumentAsync(string documentId, string? collectionOverride = null, CancellationToken ct = default)
    {
        var client = CreateClient();
        var collection = collectionOverride ?? _opts.CollectionName;

        await client.DeleteAsync(collection, MatchKeyword("document_id", documentId), cancellationToken: ct);
        _logger.LogDebug("Deleted vectors for document '{DocId}' from '{Collection}'", documentId, collection);
    }

    public async Task DeleteBySourceAsync(string sourceId, string? collectionOverride = null, CancellationToken ct = default)
    {
        var client = CreateClient();
        var collection = collectionOverride ?? _opts.CollectionName;

        await client.DeleteAsync(collection, MatchKeyword("source_id", sourceId), cancellationToken: ct);
        _logger.LogDebug("Deleted vectors for source '{SourceId}' from '{Collection}'", sourceId, collection);
    }

    public async Task DeleteByIdsAsync(IReadOnlyList<string> vectorIds, string? collectionOverride = null, CancellationToken ct = default)
    {
        if (vectorIds.Count == 0) return;

        var client = CreateClient();
        var collection = collectionOverride ?? _opts.CollectionName;
        var ids = vectorIds.Select(id => new PointId { Uuid = id }).ToList();

        // Build a filter to match the specific point IDs
        var filter = new Filter();
        foreach (var id in ids)
        {
            filter.Should.Add(new Condition
            {
                HasId = new HasIdCondition { HasId = { id } }
            });
        }

        await client.DeleteAsync(collection, filter, cancellationToken: ct);
        _logger.LogDebug("Deleted {Count} vectors by ID from '{Collection}'", vectorIds.Count, collection);
    }

    private static Filter BuildFilter(Dictionary<string, object> filterDict)
    {
        var filter = new Filter();

        foreach (var (key, value) in filterDict)
        {
            if (key == "_should" && value is List<Dictionary<string, object>> shouldClauses)
            {
                foreach (var clause in shouldClauses)
                    filter.Should.Add(new Condition { Filter = BuildFilter(clause) });
            }
            else if (key == "_must" && value is List<Dictionary<string, object>> mustClauses)
            {
                foreach (var clause in mustClauses)
                    filter.Must.Add(new Condition { Filter = BuildFilter(clause) });
            }
            else if (value is string sv)
            {
                filter.Must.Add(MatchKeyword(key, sv));
            }
            else if (value is int iv)
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = key,
                        Match = new Match { Integer = iv }
                    }
                });
            }
            else if (value is long lv)
            {
                filter.Must.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = key,
                        Match = new Match { Integer = lv }
                    }
                });
            }
            else if (value is bool bv)
            {
                filter.Must.Add(MatchKeyword(key, bv.ToString().ToLowerInvariant()));
            }
        }

        return filter;
    }

    private static object PayloadToObject(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.StringValue => value.StringValue,
        Value.KindOneofCase.IntegerValue => value.IntegerValue,
        Value.KindOneofCase.DoubleValue => value.DoubleValue,
        Value.KindOneofCase.BoolValue => value.BoolValue,
        _ => value.StringValue ?? "",
    };

    // ── Memory-specific operations (typed payload, no ExtraPayload bag) ────

    public async Task UpsertMemoryAsync(IReadOnlyList<MemoryVector> memories, string collection, CancellationToken ct = default)
    {
        if (memories.Count == 0) return;

        var client = CreateClient();

        var points = memories.Select(m =>
        {
            var payload = new Dictionary<string, Value>
            {
                ["memory_id"]   = m.VectorId,   // stored as string (Guid) to avoid SQLite round-trip on recall
                ["tenant_id"]   = m.TenantId,
                ["memory_type"] = m.MemoryType,
                ["text"]        = m.Content,
                ["created_at"]  = m.CreatedAt.ToString("o"),
            };

            // Scope-specific payload fields
            switch (m.Scope)
            {
                case MemoryScope.Agent(var agentId):
                    payload["agent_id"] = agentId;
                    payload["scope"] = "agent";
                    break;
                case MemoryScope.User(var userId, var agentId):
                    payload["user_id"] = userId;
                    payload["scope"] = "user";
                    if (!string.IsNullOrEmpty(agentId))
                        payload["agent_id"] = agentId;
                    break;
            }

            if (!string.IsNullOrEmpty(m.SessionId))
                payload["session_id"] = m.SessionId;
            if (m.ExpiresAt.HasValue)
                payload["expires_at"] = m.ExpiresAt.Value.ToString("o");
            if (m.Tags is { Length: > 0 })
                payload["tags"] = string.Join(",", m.Tags);

            return new PointStruct
            {
                Id = new PointId { Uuid = m.VectorId },
                Vectors = m.Embedding,
                Payload = { payload },
            };
        }).ToList();

        await client.UpsertAsync(collection, points, cancellationToken: ct);
        _logger.LogDebug("Upserted {Count} memory vectors to '{Collection}'", points.Count, collection);
    }

    public async Task<IReadOnlyList<VectorSearchResult>> SearchMemoryAsync(float[] queryVector, MemorySearchOptions options, CancellationToken ct)
    {
        var client = CreateClient();
        var filter = new Filter();

        // Tenant isolation — always required
        filter.Must.Add(new Condition
        {
            Field = new FieldCondition
            {
                Key = "tenant_id",
                Match = new Match { Integer = options.TenantId }
            }
        });

        // Scope filtering
        if (options.Scope is MemoryScope.Agent(var agentId))
        {
            filter.Must.Add(MatchKeyword("agent_id", agentId));
        }
        else if (options.Scope is MemoryScope.User(var userId, var scopeAgentId))
        {
            if (!string.IsNullOrEmpty(scopeAgentId))
            {
                // User memories for a specific agent context: include both
                // user-scoped memories AND agent-scoped memories
                var agentFilter = new Filter();
                agentFilter.Must.Add(MatchKeyword("scope", "agent"));
                agentFilter.Must.Add(MatchKeyword("agent_id", scopeAgentId));

                var userFilter = new Filter();
                userFilter.Must.Add(MatchKeyword("scope", "user"));
                userFilter.Must.Add(MatchKeyword("user_id", userId));

                filter.Should.Add(new Condition { Filter = agentFilter });
                filter.Should.Add(new Condition { Filter = userFilter });
            }
            else
            {
                filter.Must.Add(MatchKeyword("user_id", userId));
            }
        }

        // Memory type filter
        if (!string.IsNullOrEmpty(options.MemoryType))
            filter.Must.Add(MatchKeyword("memory_type", options.MemoryType));

        // Session filter
        if (options.CurrentSessionOnly && !string.IsNullOrEmpty(options.SessionId))
            filter.Must.Add(MatchKeyword("session_id", options.SessionId));

        var results = await client.SearchAsync(
            options.Collection,
            queryVector,
            limit: (ulong)options.TopK,
            scoreThreshold: options.ScoreThreshold,
            filter: filter,
            cancellationToken: ct);

        return results.Select(r => new VectorSearchResult
        {
            VectorId = r.Id.Uuid,
            Score = r.Score,
            Payload = r.Payload.ToDictionary(
                kv => kv.Key,
                kv => PayloadToObject(kv.Value)),
        }).ToList();
    }

    private QdrantClient CreateClient()
    {
        var uri = new Uri(_opts.QdrantGrpcUrl);
        return new QdrantClient(uri.Host, uri.Port, apiKey: _opts.QdrantApiKey);
    }
}
