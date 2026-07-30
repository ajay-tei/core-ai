using Diva.Core.Extensions;
using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Admin CRUD for tenant-scoped agent access groups. Regular users are scoped to
/// their JWT tenant; only master admin (TenantId=0) may target another tenant via
/// the <c>tenantId</c> query/body field (<see cref="EffectiveTenantId"/> pattern).
/// </summary>
[ApiController]
[Route("api/agent-groups")]
[RequireTenantAdmin]
public class AgentGroupsController : ControllerBase
{
    private readonly IAgentGroupService _service;
    private readonly ILogger<AgentGroupsController> _logger;
    private readonly IEntityDraftService _drafts;
    private readonly IPromotionLedgerService _ledger;
    private readonly IPromotableSnapshotSerializer _snapshotSerializer;

    public AgentGroupsController(
        IAgentGroupService service,
        ILogger<AgentGroupsController> logger,
        IEntityDraftService drafts,
        IPromotionLedgerService ledger,
        IEnumerable<IPromotableSnapshotSerializer> snapshotSerializers)
    {
        _service = service;
        _logger = logger;
        _drafts = drafts;
        _ledger = ledger;
        _snapshotSerializer = snapshotSerializers.First(s => s.ObjectType == "AgentGroup");
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/agent-groups?tenantId=1
    // Returns the full unbounded array — used by dropdown/selector callers (ApiKeyManager.tsx).
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var groups = await _service.ListAsync(tid, ct);
        return Ok(groups.Select(ToDto));
    }

    // GET /api/agent-groups/paged?tenantId=1&search=&page=1&pageSize=25
    // Dedicated paginated endpoint for the admin Agent Access Groups list page.
    [HttpGet("paged")]
    public async Task<IActionResult> ListPaged(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var groups = await _service.ListAsync(tid, ct);
        var filtered = groups.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            filtered = filtered.Where(g =>
                g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (g.Description ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        return Ok(filtered.ToPagedResult(page, pageSize).MapItems(ToDto));
    }

    // GET /api/agent-groups/{id}?tenantId=1
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var group = await _service.GetAsync(tid, id, ct);
        return group is null ? NotFound() : Ok(ToDto(group));
    }

    // POST /api/agent-groups
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgentGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var dto = new AgentGroupDto(req.Name, req.Description, req.AgentIds ?? [], req.AllowedUserIds ?? [], req.AllowedRoles ?? [], req.AllowedUserGroupIds ?? []);
        var created = await _service.CreateAsync(tid, dto, ct);
        return Ok(ToDto(created));
    }

    // PUT /api/agent-groups/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AgentGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var dto = new AgentGroupDto(req.Name, req.Description, req.AgentIds ?? [], req.AllowedUserIds ?? [], req.AllowedRoles ?? [], req.AllowedUserGroupIds ?? []);
        var updated = await _service.UpdateAsync(tid, id, dto, ct);
        return updated is null ? NotFound() : Ok(ToDto(updated));
    }

    // PUT /api/agent-groups/{id}/draft — additive, does NOT touch the live row.
    [HttpPut("{id}/draft")]
    public async Task<IActionResult> SaveDraft(string id, [FromBody] AgentGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (existing.LogicalId is not { } logicalId || existing.EnvironmentId is not { } environmentId)
            return BadRequest(new { error = "Agent group is missing environment/logical identity — cannot draft." });

        var ctx = HttpContext.TryGetTenantContext();
        var json = System.Text.Json.JsonSerializer.Serialize(req);
        await _drafts.SaveDraftAsync(tid, "AgentGroup", logicalId, environmentId, json, ctx?.UserId, ct);
        return Ok(new { message = "Draft saved." });
    }

    // GET /api/agent-groups/{id}/draft
    [HttpGet("{id}/draft")]
    public async Task<IActionResult> GetDraft(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (existing.LogicalId is not { } logicalId || existing.EnvironmentId is not { } environmentId)
            return Ok(new { hasDraft = false });

        var draft = await _drafts.GetDraftAsync(tid, "AgentGroup", logicalId, environmentId, ct);
        if (draft is null) return Ok(new { hasDraft = false });

        var req = System.Text.Json.JsonSerializer.Deserialize<AgentGroupRequest>(draft.DraftJson);
        return Ok(new { hasDraft = true, draft = req, updatedAt = draft.UpdatedAt, updatedBy = draft.UpdatedBy });
    }

    // DELETE /api/agent-groups/{id}/draft
    [HttpDelete("{id}/draft")]
    public async Task<IActionResult> DiscardDraft(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (existing.LogicalId is { } logicalId && existing.EnvironmentId is { } environmentId)
            await _drafts.ClearDraftAsync(tid, "AgentGroup", logicalId, environmentId, ct);
        return NoContent();
    }

    // POST /api/agent-groups/{id}/publish — applies the draft + records a ledger version.
    [HttpPost("{id}/publish")]
    public async Task<IActionResult> Publish(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (existing.LogicalId is not { } logicalId || existing.EnvironmentId is not { } environmentId)
            return BadRequest(new { error = "Agent group is missing environment/logical identity — cannot publish." });

        var draft = await _drafts.GetDraftAsync(tid, "AgentGroup", logicalId, environmentId, ct);
        if (draft is null) return BadRequest(new { error = "No draft to publish." });

        var req = System.Text.Json.JsonSerializer.Deserialize<AgentGroupRequest>(draft.DraftJson)
            ?? throw new InvalidOperationException("Invalid draft JSON.");

        var dto = new AgentGroupDto(req.Name, req.Description, req.AgentIds ?? [], req.AllowedUserIds ?? [], req.AllowedRoles ?? [], req.AllowedUserGroupIds ?? []);
        var updated = await _service.UpdateAsync(tid, id, dto, ct);
        if (updated is null) return NotFound();

        var ctx = HttpContext.TryGetTenantContext();
        var snapshot = await _snapshotSerializer.SerializeAsync(tid, logicalId, ct);
        if (snapshot is not null)
        {
            await _ledger.RecordVersionAsync(
                tid, logicalId, "AgentGroup", snapshot.Name, environmentId,
                snapshot.SnapshotJson, "publish", null, ctx?.UserId, null, ct);
        }

        await _drafts.ClearDraftAsync(tid, "AgentGroup", logicalId, environmentId, ct);
        return Ok(ToDto(updated));
    }

    // DELETE /api/agent-groups/{id}?tenantId=1
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var ok = await _service.DeleteAsync(tid, id, ct);
        return ok ? NoContent() : NotFound();
    }

    private static AgentGroupResponse ToDto(Diva.Infrastructure.Data.Entities.AgentGroupEntity e) => new(
        e.Id,
        e.Name,
        e.Description,
        Parse(e.AgentIdsJson),
        Parse(e.AllowedUserIdsJson),
        Parse(e.AllowedRolesJson),
        e.UserGroupLinks.Select(l => l.UserGroupId).ToArray(),
        e.CreatedAt,
        e.UpdatedAt);

    private static string[] Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record AgentGroupRequest(
    string Name,
    string? Description = null,
    string[]? AgentIds = null,
    string[]? AllowedUserIds = null,
    string[]? AllowedRoles = null,
    int[]? AllowedUserGroupIds = null,
    int TenantId = 1);

public record AgentGroupResponse(
    string Id,
    string Name,
    string? Description,
    string[] AgentIds,
    string[] AllowedUserIds,
    string[] AllowedRoles,
    int[] AllowedUserGroupIds,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
