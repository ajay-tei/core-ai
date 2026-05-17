using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Rag;
using Diva.Rag.Abstractions;
using Diva.Rag.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/rag")]
public class RagController : ControllerBase
{
    private readonly KnowledgeSourceService _sources;
    private readonly IDocumentIngestionPipeline _ingestion;
    private readonly IKnowledgeRetriever _retriever;
    private readonly IDatabaseProviderFactory _db;
    private readonly RagOptions _opts;

    public RagController(
        KnowledgeSourceService sources,
        IDocumentIngestionPipeline ingestion,
        IKnowledgeRetriever retriever,
        IDatabaseProviderFactory db,
        IOptions<RagOptions> opts)
    {
        _sources   = sources;
        _ingestion = ingestion;
        _retriever = retriever;
        _db        = db;
        _opts      = opts.Value;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── Knowledge Sources ─────────────────────────────────────────────────────

    [HttpPost("sources")]
    public async Task<IActionResult> CreateSource([FromBody] CreateSourceRequest req, CancellationToken ct)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var entity = new KnowledgeSourceEntity
        {
            TenantId = tid,
            Name = req.Name,
            ScopeType = req.ScopeType ?? "tenant",
            AgentId = req.AgentId,
            SourceType = req.SourceType,
            ConfigJson = req.ConfigJson,
            TaxonomyJson = req.TaxonomyJson,
            Status = "Active",
        };
        var created = await _sources.CreateAsync(entity, ct);
        return Ok(created);
    }

    [HttpGet("sources")]
    public async Task<IActionResult> ListSources([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var sources = await _sources.ListAsync(tid, ct: ct);
        return Ok(sources);
    }

    [HttpGet("sources/{id}")]
    public async Task<IActionResult> GetSource(string id, CancellationToken ct)
    {
        var source = await _sources.GetAsync(id, ct);
        if (source is null) return NotFound();
        return Ok(source);
    }

    [HttpPut("sources/{id}")]
    public async Task<IActionResult> UpdateSource(string id, [FromBody] UpdateSourceRequest req, CancellationToken ct)
    {
        var source = await _sources.GetAsync(id, ct);
        if (source is null) return NotFound();

        if (req.Name is not null) source.Name = req.Name;
        if (req.ConfigJson is not null) source.ConfigJson = req.ConfigJson;
        if (req.TaxonomyJson is not null) source.TaxonomyJson = req.TaxonomyJson;
        if (req.Status is not null) source.Status = req.Status;

        await _sources.UpdateAsync(source, ct);
        return Ok(source);
    }

    [HttpDelete("sources/{id}")]
    public async Task<IActionResult> DeleteSource(string id, CancellationToken ct)
    {
        await _sources.DeleteAsync(id, ct);
        return NoContent();
    }

    // ── Ingestion ─────────────────────────────────────────────────────────────

    [HttpPost("sources/{sourceId}/ingest")]
    public async Task IngestSource(
        string sourceId,
        [FromQuery] string? documentUri = null,
        CancellationToken ct = default)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var source = await _sources.GetAsync(sourceId, ct);
        if (source is null)
        {
            Response.StatusCode = 404;
            return;
        }

        var tid = EffectiveTenantId(source.TenantId);

        await _ingestion.IngestAsync(sourceId, tid, documentUri, async (evt) =>
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }, ct);

        await Response.WriteAsync("data: {\"phase\":\"done\"}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] RagQueryRequest req, CancellationToken ct)
    {
        if (!_opts.Enabled) return BadRequest(new { error = "RAG is not enabled" });

        var tid = EffectiveTenantId(req.TenantId);
        var filter = new KnowledgeFilter
        {
            TenantId = tid,
            AgentId = req.AgentId,
            TopK = req.TopK ?? _opts.DefaultTopK,
            MinScore = req.MinScore ?? _opts.DefaultMinScore,
            Domain = req.Domain,
            Product = req.Product,
            ContentType = req.ContentType,
            SourceId = req.SourceId,
        };

        var result = await _retriever.RetrieveAsync(req.Query, filter, ct);
        return Ok(result);
    }

    // ── Documents ─────────────────────────────────────────────────────────────

    [HttpGet("sources/{sourceId}/documents")]
    public async Task<IActionResult> ListDocuments(string sourceId, CancellationToken ct)
    {
        using var ctx = _db.CreateDbContext();
        var docs = await ctx.KnowledgeDocuments
            .Where(d => d.SourceId == sourceId)
            .AsNoTracking()
            .OrderBy(d => d.Title)
            .ToListAsync(ct);
        return Ok(docs);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        return Ok(new
        {
            _opts.Enabled,
            _opts.QdrantUrl,
            _opts.EmbeddingProvider,
            _opts.EmbeddingModel,
            _opts.EmbeddingDimensions,
            _opts.DefaultChunkSize,
            _opts.DefaultChunkOverlap,
            _opts.DefaultTopK,
            _opts.DefaultMinScore,
            _opts.MaxRetrievalTokens,
            _opts.EnableAgentMemory,
        });
    }

    // ── Agent Memory (Phase 26.2) ─────────────────────────────────────────────

    [HttpGet("memory")]
    public async Task<IActionResult> ListMemories(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? agentId = null,
        [FromQuery] string? memoryType = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!_opts.EnableAgentMemory) return BadRequest(new { error = "Agent memory is not enabled" });
        var tid = EffectiveTenantId(tenantId);

        await using var scope = HttpContext.RequestServices.CreateAsyncScope();
        var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();
        var memories = await memService.ListAsync(tid, agentId, memoryType, skip, take, ct);
        return Ok(memories);
    }

    [HttpDelete("memory/{id}")]
    public async Task<IActionResult> DeleteMemory(Guid id, CancellationToken ct)
    {
        if (!_opts.EnableAgentMemory) return BadRequest(new { error = "Agent memory is not enabled" });

        await using var scope = HttpContext.RequestServices.CreateAsyncScope();
        var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();
        var deleted = await memService.ForgetMemoryAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    [HttpDelete("memory/agent/{agentId}/type/{memoryType}")]
    public async Task<IActionResult> ClearMemoryType(string agentId, string memoryType,
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        if (!_opts.EnableAgentMemory) return BadRequest(new { error = "Agent memory is not enabled" });
        var tid = EffectiveTenantId(tenantId);

        using var db = _db.CreateDbContext();
        var memories = await db.AgentMemories
            .Where(m => m.TenantId == tid && m.AgentId == agentId && m.MemoryType == memoryType)
            .ToListAsync(ct);

        if (memories.Count == 0) return Ok(new { deleted = 0 });

        await using var scope = HttpContext.RequestServices.CreateAsyncScope();
        var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();

        int deleted = 0;
        foreach (var m in memories)
        {
            if (await memService.ForgetMemoryAsync(m.Id, ct)) deleted++;
        }

        return Ok(new { deleted });
    }
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record CreateSourceRequest(
    int TenantId,
    string Name,
    string SourceType,
    string? ScopeType = null,
    string? AgentId = null,
    string? ConfigJson = null,
    string? TaxonomyJson = null);

public record UpdateSourceRequest(
    string? Name = null,
    string? ConfigJson = null,
    string? TaxonomyJson = null,
    string? Status = null);

public record RagQueryRequest(
    string Query,
    int TenantId = 1,
    string? AgentId = null,
    int? TopK = null,
    float? MinScore = null,
    string? Domain = null,
    string? Product = null,
    string? ContentType = null,
    string? SourceId = null);
