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

    public AgentGroupsController(IAgentGroupService service, ILogger<AgentGroupsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/agent-groups?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var groups = await _service.ListAsync(tid, ct);
        return Ok(groups.Select(ToDto));
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
        var dto = new AgentGroupDto(req.Name, req.Description, req.AgentIds ?? [], req.AllowedUserIds ?? [], req.AllowedRoles ?? []);
        var created = await _service.CreateAsync(tid, dto, ct);
        return Ok(ToDto(created));
    }

    // PUT /api/agent-groups/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AgentGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var dto = new AgentGroupDto(req.Name, req.Description, req.AgentIds ?? [], req.AllowedUserIds ?? [], req.AllowedRoles ?? []);
        var updated = await _service.UpdateAsync(tid, id, dto, ct);
        return updated is null ? NotFound() : Ok(ToDto(updated));
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
    int TenantId = 1);

public record AgentGroupResponse(
    string Id,
    string Name,
    string? Description,
    string[] AgentIds,
    string[] AllowedUserIds,
    string[] AllowedRoles,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
