using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data.Entities;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Admin CRUD for tenant-scoped user groups (a user may belong to many groups).
/// Regular admins are scoped to their JWT tenant; only master admin (TenantId=0) may
/// target another tenant via the <c>tenantId</c> field (<see cref="EffectiveTenantId"/> pattern).
/// </summary>
[ApiController]
[Route("api/user-groups")]
[RequireTenantAdmin]
public class UserGroupsController : ControllerBase
{
    private readonly IUserGroupService _service;
    private readonly ILogger<UserGroupsController> _logger;

    public UserGroupsController(IUserGroupService service, ILogger<UserGroupsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/user-groups?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var groups = await _service.ListAsync(tid, ct);
        return Ok(groups.Select(ToDto));
    }

    // GET /api/user-groups/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var group = await _service.GetAsync(tid, id, ct);
        return group is null ? NotFound() : Ok(ToDto(group));
    }

    // POST /api/user-groups
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var createdBy = HttpContext.TryGetTenantContext()?.UserId;
        var created = await _service.CreateAsync(tid, ToServiceDto(req), createdBy, ct);
        return Ok(ToDto(created));
    }

    // PUT /api/user-groups/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UserGroupRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var updated = await _service.UpdateAsync(tid, id, ToServiceDto(req), ct);
        return updated is null ? NotFound() : Ok(ToDto(updated));
    }

    // DELETE /api/user-groups/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var ok = await _service.DeleteAsync(tid, id, ct);
        return ok ? NoContent() : NotFound();
    }

    private static UserGroupDto ToServiceDto(UserGroupRequest req) => new(
        req.Name,
        req.Description,
        (req.Members ?? []).Select(m => new UserGroupMemberDto(m.UserId, m.Email)).ToList(),
        req.Roles ?? []);

    private static UserGroupResponse ToDto(UserGroupEntity e) => new(
        e.Id,
        e.Name,
        e.Description,
        e.Members.Select(m => new UserGroupMemberResponse(m.UserId, m.Email)).ToArray(),
        e.Roles.Select(r => r.Role).ToArray(),
        e.CreatedAt,
        e.UpdatedAt);
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record UserGroupMemberRequest(string UserId, string? Email = null);

public record UserGroupRequest(
    string Name,
    string? Description = null,
    UserGroupMemberRequest[]? Members = null,
    string[]? Roles = null,
    int TenantId = 1);

public record UserGroupMemberResponse(string UserId, string? Email);

public record UserGroupResponse(
    int Id,
    string Name,
    string? Description,
    UserGroupMemberResponse[] Members,
    string[] Roles,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
