using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Services;

/// <summary>
/// CRUD service for KnowledgeSourceEntity with cache invalidation.
/// </summary>
public sealed class KnowledgeSourceService(
    IDatabaseProviderFactory db,
    ILogger<KnowledgeSourceService> logger)
{
    public async Task<KnowledgeSourceEntity> CreateAsync(KnowledgeSourceEntity source, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        ctx.KnowledgeSources.Add(source);
        await ctx.SaveChangesAsync(ct);
        logger.LogInformation("Created knowledge source '{Name}' (id={Id}, type={Type})", source.Name, source.Id, source.SourceType);
        return source;
    }

    public async Task<KnowledgeSourceEntity?> GetAsync(string id, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        return await ctx.KnowledgeSources.FindAsync([id], ct);
    }

    public async Task<List<KnowledgeSourceEntity>> ListAsync(int tenantId, IReadOnlyList<int>? groupIds = null, CancellationToken ct = default)
    {
        using var ctx = db.CreateDbContext();
        var query = ctx.KnowledgeSources.AsNoTracking();

        // Visible sources: tenant's own + group + platform + agent-scoped for this tenant
        query = query.Where(s =>
            (s.ScopeType == "tenant" && s.TenantId == tenantId) ||
            (s.ScopeType == "platform") ||
            (s.ScopeType == "agent" && s.TenantId == tenantId) ||
            (s.ScopeType == "group" && groupIds != null && groupIds.Contains(s.TenantId)));

        return await query.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<List<KnowledgeSourceEntity>> ListForAgentAsync(string agentId, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        return await ctx.KnowledgeSources
            .Where(s => s.AgentId == agentId)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(KnowledgeSourceEntity source, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        source.UpdatedAt = DateTime.UtcNow;
        ctx.KnowledgeSources.Update(source);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        var source = await ctx.KnowledgeSources.FindAsync([id], ct);
        if (source is null) return;

        // Delete related documents, chunks, and jobs
        var docs = await ctx.KnowledgeDocuments.Where(d => d.SourceId == id).ToListAsync(ct);
        var docIds = docs.Select(d => d.DocumentId).ToList();

        if (docIds.Count > 0)
        {
            var chunks = await ctx.KnowledgeChunks.Where(c => docIds.Contains(c.DocumentId)).ToListAsync(ct);
            ctx.KnowledgeChunks.RemoveRange(chunks);

            var versions = await ctx.KnowledgeDocumentVersions.Where(v => docIds.Contains(v.DocumentId)).ToListAsync(ct);
            ctx.KnowledgeDocumentVersions.RemoveRange(versions);
        }

        ctx.KnowledgeDocuments.RemoveRange(docs);

        var jobs = await ctx.IngestionJobs.Where(j => j.SourceId == id).ToListAsync(ct);
        ctx.IngestionJobs.RemoveRange(jobs);

        ctx.KnowledgeSources.Remove(source);
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("Deleted knowledge source '{Name}' (id={Id}) with {DocCount} documents", source.Name, id, docs.Count);
    }

    /// <summary>
    /// Get or create an agent-scoped source for auto-indexing via index_file_to_knowledge.
    /// Uses upsert semantics to avoid duplicate sources per agent+sourceType.
    /// </summary>
    public async Task<KnowledgeSourceEntity> GetOrCreateAgentSourceAsync(
        string agentId, int tenantId, string sourceType, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        var existing = await ctx.KnowledgeSources
            .FirstOrDefaultAsync(s => s.AgentId == agentId && s.SourceType == sourceType, ct);

        if (existing is not null) return existing;

        var source = new KnowledgeSourceEntity
        {
            TenantId = tenantId,
            Name = $"Agent-{agentId}-{sourceType}",
            ScopeType = "agent",
            AgentId = agentId,
            SourceType = sourceType,
            Status = "Active",
        };

        ctx.KnowledgeSources.Add(source);
        await ctx.SaveChangesAsync(ct);
        logger.LogInformation("Auto-created agent knowledge source '{Name}' for agent {AgentId}", source.Name, agentId);
        return source;
    }
}
