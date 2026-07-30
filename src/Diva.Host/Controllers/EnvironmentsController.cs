using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Admin CRUD for a tenant's environment pipeline (foundation for environment-based agent
/// management — promotion/versioning/runtime routing are separate, not-yet-built phases).
/// Regular admins are scoped to their JWT tenant; only master admin (TenantId=0) may
/// target another tenant via the <c>tenantId</c> field (<see cref="EffectiveTenantId"/> pattern).
/// </summary>
[ApiController]
[Route("api/admin/environments")]
[RequireTenantAdmin]
public class EnvironmentsController : ControllerBase
{
    private readonly IEnvironmentService _service;

    public EnvironmentsController(IEnvironmentService service)
    {
        _service = service;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    public record EnvironmentRequest(string Slug, string DisplayName, int Rank, bool IsDefault, int TenantId = 1);

    // GET /api/admin/environments?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        return Ok(await _service.ListAsync(tid, ct));
    }

    // GET /api/admin/environments/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var env = await _service.GetAsync(tid, id, ct);
        return env is null ? NotFound() : Ok(env);
    }

    // POST /api/admin/environments
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] EnvironmentRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var (entity, error) = await _service.CreateAsync(tid, new EnvironmentDto(req.Slug, req.DisplayName, req.Rank, req.IsDefault), ct);
        return entity is null ? BadRequest(new { error }) : Ok(entity);
    }

    // PUT /api/admin/environments/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] EnvironmentRequest req, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(req.TenantId);
        var (entity, error) = await _service.UpdateAsync(tid, id, new EnvironmentDto(req.Slug, req.DisplayName, req.Rank, req.IsDefault), ct);
        if (entity is not null) return Ok(entity);
        return error == "Environment not found." ? NotFound(new { error }) : BadRequest(new { error });
    }

    // DELETE /api/admin/environments/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var (success, error) = await _service.DeleteAsync(tid, id, ct);
        if (success) return NoContent();
        return error is null ? NotFound() : BadRequest(new { error });
    }
}
