using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Learning;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/learned-rules")]
[RequireTenantAdmin]
public class LearnedRulesController : ControllerBase
{
    private readonly IRuleLearningService _learning;

    public LearnedRulesController(IRuleLearningService learning) => _learning = learning;

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/learned-rules?tenantId=1
    [HttpGet]
    public async Task<IActionResult> GetPending([FromQuery] int tenantId = 1, CancellationToken ct = default)
        => Ok(await _learning.GetPendingRulesAsync(EffectiveTenantId(tenantId), ct));

    // POST /api/learned-rules/{id}/approve?tenantId=1
    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        await _learning.ApproveRuleAsync(EffectiveTenantId(tenantId), id, "admin", ct);
        return NoContent();
    }

    // POST /api/learned-rules/{id}/reject
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(
        int id,
        [FromQuery] int tenantId = 1,
        [FromBody] RejectRuleRequest? body = null,
        CancellationToken ct = default)
    {
        await _learning.RejectRuleAsync(EffectiveTenantId(tenantId), id, "admin", body?.Notes ?? "", ct);
        return NoContent();
    }
}

public sealed record RejectRuleRequest(string? Notes);
