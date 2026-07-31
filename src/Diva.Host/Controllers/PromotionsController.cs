namespace Diva.Host.Controllers;

using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Promotes a promotable object (agent/MCP server/scheduled task/agent group) from one environment
/// to a higher-ranked one within the same tenant, and rolls back a live environment to an older
/// recorded version. Thin wrapper over <see cref="IPromotionOrchestrationService"/> (Phase D).
/// </summary>
[ApiController]
[Route("api/admin/promotions")]
[RequireTenantAdmin]
public class PromotionsController : ControllerBase
{
    private readonly IPromotionOrchestrationService _orchestrator;
    private readonly IPromotionLedgerService _ledger;

    public PromotionsController(IPromotionOrchestrationService orchestrator, IPromotionLedgerService ledger)
    {
        _orchestrator = orchestrator;
        _ledger = ledger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/promotions/preview?tenantId=1&objectType=Agent&logicalId=...&fromEnvironmentId=1&toEnvironmentId=2
    [HttpGet("preview")]
    public async Task<IActionResult> Preview(
        [FromQuery] string objectType, [FromQuery] Guid logicalId,
        [FromQuery] int fromEnvironmentId, [FromQuery] int toEnvironmentId,
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var preview = await _orchestrator.PreviewAsync(tid, objectType, logicalId, fromEnvironmentId, toEnvironmentId, ct);
        return Ok(preview);
    }

    // POST /api/admin/promotions
    [HttpPost]
    public async Task<IActionResult> Promote([FromBody] PromoteRequest req, CancellationToken ct)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var ctx = HttpContext.TryGetTenantContext();
        var result = await _orchestrator.PromoteAsync(
            tid, req.ObjectType, req.LogicalId, req.FromEnvironmentId, req.ToEnvironmentId, ctx?.UserId, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }

    // POST /api/admin/promotions/bulk — promote the same object into several target environments
    // in one call (e.g. rolling an agent out to every client's Play environment at once).
    // Each target is independent: one failing (e.g. a missing LLM config in that client's
    // environment) does not stop the others — inspect each entry's own Success/Error.
    [HttpPost("bulk")]
    public async Task<IActionResult> BulkPromote([FromBody] BulkPromoteRequest req, CancellationToken ct)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var ctx = HttpContext.TryGetTenantContext();
        var results = new List<BulkPromoteResultItem>();
        foreach (var toEnvironmentId in req.ToEnvironmentIds.Distinct())
        {
            var result = await _orchestrator.PromoteAsync(
                tid, req.ObjectType, req.LogicalId, req.FromEnvironmentId, toEnvironmentId, ctx?.UserId, ct);
            results.Add(new BulkPromoteResultItem(toEnvironmentId, result));
        }
        return Ok(results);
    }

    // GET /api/admin/promotions/history?tenantId=1&logicalId=...
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] Guid logicalId, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var history = await _ledger.GetHistoryAsync(tid, logicalId, ct);
        return Ok(history);
    }

    // GET /api/admin/promotions/diff?tenantId=1&fromVersionId=1&toVersionId=2
    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] int fromVersionId, [FromQuery] int toVersionId, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var diff = await _ledger.DiffVersionsAsync(tid, fromVersionId, toVersionId, ct);
        return Ok(diff);
    }

    // POST /api/admin/promotions/rollback
    [HttpPost("rollback")]
    public async Task<IActionResult> Rollback([FromBody] RollbackRequest req, CancellationToken ct)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var ctx = HttpContext.TryGetTenantContext();
        var result = await _orchestrator.RollbackAsync(
            tid, req.ObjectType, req.LogicalId, req.EnvironmentId, req.ToVersionId, ctx?.UserId, ct);
        return result.Success ? Ok(result) : BadRequest(result);
    }
}

public record PromoteRequest(string ObjectType, Guid LogicalId, int FromEnvironmentId, int ToEnvironmentId, int TenantId = 1);

public record BulkPromoteRequest(string ObjectType, Guid LogicalId, int FromEnvironmentId, int[] ToEnvironmentIds, int TenantId = 1);

public record BulkPromoteResultItem(int ToEnvironmentId, PromotionResult Result);

public record RollbackRequest(string ObjectType, Guid LogicalId, int EnvironmentId, int ToVersionId, int TenantId = 1);
