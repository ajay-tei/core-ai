using System.Text.Json;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Rag.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Services;

/// <summary>
/// Manages agent memory lifecycle — stores in SQLite (audit) + Qdrant (semantic search).
/// Uses <see cref="MemoryVector"/> for typed Qdrant payload construction.
/// </summary>
public sealed class AgentMemoryService : IAgentMemoryService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IEmbeddingService _embedding;
    private readonly IVectorRepository _vectors;
    private readonly RagOptions _opts;
    private readonly ILogger<AgentMemoryService> _logger;

    public AgentMemoryService(
        IDatabaseProviderFactory db,
        IEmbeddingService embedding,
        IVectorRepository vectors,
        IOptions<RagOptions> opts,
        ILogger<AgentMemoryService> logger)
    {
        _db = db;
        _embedding = embedding;
        _vectors = vectors;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<Guid> SaveMemoryAsync(int tenantId, string agentId, string content, string memoryType,
        string? sessionId = null, string? userId = null,
        IEnumerable<string>? tags = null, int? expiresInMinutes = null,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var vectorId = id.ToString();

        // Calculate expiry
        DateTime? expiresAt = memoryType switch
        {
            "working" => DateTime.UtcNow.AddMinutes(expiresInMinutes ?? _opts.WorkingMemoryDefaultTtlMinutes),
            "episodic" when _opts.EpisodicMemoryDefaultTtlDays > 0 =>
                DateTime.UtcNow.AddDays(_opts.EpisodicMemoryDefaultTtlDays),
            _ => null // semantic = permanent
        };

        var tagList = tags?.ToArray() ?? [];

        // 1. Store in SQLite (audit trail + fallback)
        var entity = new AgentMemoryEntity
        {
            Id = id,
            TenantId = tenantId,
            AgentId = agentId,
            UserId = userId,
            Content = content,
            MemoryType = memoryType,
            SessionId = sessionId,
            VectorId = vectorId,
            TagsJson = tagList.Length > 0 ? JsonSerializer.Serialize(tagList) : null,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };

        using var db = _db.CreateDbContext();
        db.AgentMemories.Add(entity);
        await db.SaveChangesAsync(ct);

        // 2. Embed and store in Qdrant via typed MemoryVector
        try
        {
            var embedding = await _embedding.EmbedAsync(content, ct);

            // Determine scope from parameters
            MemoryScope scope = !string.IsNullOrEmpty(userId)
                ? new MemoryScope.User(userId, agentId)
                : new MemoryScope.Agent(agentId);

            var memoryVector = new MemoryVector
            {
                VectorId = vectorId,
                Embedding = embedding,
                Content = content,
                TenantId = tenantId,
                MemoryType = memoryType,
                Scope = scope,
                SessionId = sessionId,
                Tags = tagList.Length > 0 ? tagList : null,
                ExpiresAt = expiresAt,
            };

            await _vectors.UpsertMemoryAsync([memoryVector], _opts.MemoryCollectionName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to embed memory {MemoryId} to Qdrant — SQLite record preserved", id);
        }

        _logger.LogDebug("Saved {MemoryType} memory {MemoryId} for agent {AgentId} user {UserId}",
            memoryType, id, agentId, userId ?? "(none)");
        return id;
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallMemoryAsync(int tenantId, string agentId, string query,
        string? memoryType = null, string? sessionId = null, string? userId = null,
        bool currentSessionOnly = false, int maxResults = 5,
        CancellationToken ct = default)
    {
        // Empty query: skip semantic search entirely — return most-recent memories by recency.
        // EmbedAsync("") produces a near-zero vector whose cosine similarity is undefined.
        if (string.IsNullOrWhiteSpace(query))
            return await FallbackRecallAsync(tenantId, agentId, "", memoryType, sessionId, userId, currentSessionOnly, maxResults, ct);

        // Build typed scope for search
        MemoryScope scope = !string.IsNullOrEmpty(userId)
            ? new MemoryScope.User(userId, agentId)   // recall both user + agent memories
            : new MemoryScope.Agent(agentId);          // agent-only memories

        // Type weights: relevance dominates; type is a tiebreaker.
        // semantic=1.0 (permanent knowledge) > episodic=0.92 (long-term) > working=0.85 (ephemeral)
        static float TypeWeight(string? type) => type switch
        {
            "semantic" => 1.00f,
            "episodic" => 0.92f,
            "working"  => 0.85f,
            _          => 0.80f,
        };

        try
        {
            var queryVector = await _embedding.EmbedAsync(query, ct);
            var searchOpts = new MemorySearchOptions
            {
                Collection = _opts.MemoryCollectionName,
                TopK = maxResults * 2,   // fetch extra so composite re-ranking has candidates
                ScoreThreshold = 0.3f,
                TenantId = tenantId,
                Scope = scope,
                MemoryType = memoryType,
                SessionId = sessionId,
                CurrentSessionOnly = currentSessionOnly,
            };
            var results = await _vectors.SearchMemoryAsync(queryVector, searchOpts, ct);

            // Composite score = semanticScore × typeWeight — sort descending, then cap to maxResults.
            // Reads all metadata from Qdrant payload (text, memory_type, tags, created_at, memory_id)
            // to avoid a second SQLite round-trip on the hot path.
            return results
                .OrderByDescending(r =>
                {
                    var type = r.Payload.GetValueOrDefault("memory_type")?.ToString();
                    return r.Score * TypeWeight(type);
                })
                .Take(maxResults)
                .Select(r =>
                {
                    var text      = r.Payload.GetValueOrDefault("text")?.ToString() ?? "";
                    var type      = r.Payload.GetValueOrDefault("memory_type")?.ToString() ?? "unknown";
                    var tagsRaw   = r.Payload.GetValueOrDefault("tags")?.ToString();
                    var memTags   = !string.IsNullOrEmpty(tagsRaw)
                        ? tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : Array.Empty<string>();
                    var createdAt = r.Payload.TryGetValue("created_at", out var ca)
                        && DateTime.TryParse(ca?.ToString(), out var dt) ? dt : DateTime.UtcNow;
                    var memId     = r.Payload.TryGetValue("memory_id", out var mid)
                        && Guid.TryParse(mid?.ToString(), out var g) ? g : Guid.Empty;
                    var userId2   = r.Payload.GetValueOrDefault("user_id")?.ToString();
                    var composite = r.Score * TypeWeight(type);
                    return new RecalledMemory(memId, text, type, memTags, composite, createdAt, userId2);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant recall failed — falling back to SQLite text search");
            return await FallbackRecallAsync(tenantId, agentId, query, memoryType, sessionId, userId, currentSessionOnly, maxResults, ct);
        }
    }

    public async Task<bool> ForgetMemoryAsync(Guid memoryId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.AgentMemories.FindAsync([memoryId], ct);
        if (entity is null) return false;

        // Delete from Qdrant
        try
        {
            await _vectors.DeleteByIdsAsync([entity.VectorId], _opts.MemoryCollectionName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete memory {MemoryId} from Qdrant", memoryId);
        }

        db.AgentMemories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var expired = await db.AgentMemories
            .Where(m => m.ExpiresAt != null && m.ExpiresAt < DateTime.UtcNow)
            .ToListAsync(ct);

        if (expired.Count == 0) return 0;

        var vectorIds = expired.Select(m => m.VectorId).Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (vectorIds.Count > 0)
        {
            try
            {
                await _vectors.DeleteByIdsAsync(vectorIds, _opts.MemoryCollectionName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete {Count} expired memories from Qdrant", vectorIds.Count);
            }
        }

        db.AgentMemories.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Cleaned up {Count} expired agent memories", expired.Count);
        return expired.Count;
    }

    public async Task<int> CleanupSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var sessionMemories = await db.AgentMemories
            .Where(m => m.SessionId == sessionId && m.MemoryType == "working")
            .ToListAsync(ct);

        if (sessionMemories.Count == 0) return 0;

        var vectorIds = sessionMemories.Select(m => m.VectorId).Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (vectorIds.Count > 0)
        {
            try
            {
                await _vectors.DeleteByIdsAsync(vectorIds, _opts.MemoryCollectionName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete session working memories from Qdrant");
            }
        }

        db.AgentMemories.RemoveRange(sessionMemories);
        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Cleaned up {Count} working memories for session {SessionId}", sessionMemories.Count, sessionId);
        return sessionMemories.Count;
    }

    public async Task<IReadOnlyList<AgentMemoryDto>> ListAsync(int tenantId, string? agentId = null,
        string? memoryType = null, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        IQueryable<AgentMemoryEntity> q = db.AgentMemories
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedAt);

        if (!string.IsNullOrEmpty(agentId))
            q = q.Where(m => m.AgentId == agentId);
        if (!string.IsNullOrEmpty(memoryType))
            q = q.Where(m => m.MemoryType == memoryType);

        var entities = await q.Skip(skip).Take(take).ToListAsync(ct);
        return entities.Select(e =>
        {
            var tags = !string.IsNullOrEmpty(e.TagsJson)
                ? JsonSerializer.Deserialize<string[]>(e.TagsJson) ?? []
                : Array.Empty<string>();
            return new AgentMemoryDto(e.Id, e.AgentId, e.MemoryType, e.Content,
                e.SessionId, e.UserId, tags, e.ExpiresAt, e.CreatedAt);
        }).ToList();
    }

    private async Task<IReadOnlyList<RecalledMemory>> FallbackRecallAsync(int tenantId, string agentId,
        string query, string? memoryType, string? sessionId, string? userId, bool currentSessionOnly,
        int maxResults, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        IQueryable<AgentMemoryEntity> q = db.AgentMemories
            .Where(m => m.TenantId == tenantId
                        && (m.ExpiresAt == null || m.ExpiresAt > DateTime.UtcNow)
                        && m.Content.Contains(query));

        // Scope: include agent memories + user memories if userId provided
        if (!string.IsNullOrEmpty(userId))
            q = q.Where(m => m.AgentId == agentId || m.UserId == userId);
        else
            q = q.Where(m => m.AgentId == agentId);

        if (!string.IsNullOrEmpty(memoryType))
            q = q.Where(m => m.MemoryType == memoryType);
        if (currentSessionOnly && !string.IsNullOrEmpty(sessionId))
            q = q.Where(m => m.SessionId == sessionId);

        q = q.OrderByDescending(m => m.CreatedAt);

        var entities = await q.Take(maxResults).ToListAsync(ct);
        return entities.Select(e =>
        {
            var tags = !string.IsNullOrEmpty(e.TagsJson)
                ? JsonSerializer.Deserialize<string[]>(e.TagsJson) ?? []
                : Array.Empty<string>();
            return new RecalledMemory(e.Id, e.Content, e.MemoryType, tags, 1.0f, e.CreatedAt, e.UserId);
        }).ToList();
    }

}