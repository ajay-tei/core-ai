using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data.Entities;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Non-admin-reachable, read-only environment info — distinct from the admin-only
/// EnvironmentsController (api/admin/environments). Returns only the caller's own
/// server-resolved environment (TenantContext.EnvironmentId, set by TenantContextMiddleware and
/// never spoofable by a non-admin), as a list so the frontend can reuse EnvironmentSwitcher
/// unchanged. Always 0 or 1 item today; a future multi-environment-access feature would only
/// need to change what this action computes, not any frontend code.
/// </summary>
[ApiController]
[Route("api/environments")]
public class EnvironmentAccessController : ControllerBase
{
    private readonly IEnvironmentService _service;

    public EnvironmentAccessController(IEnvironmentService service)
    {
        _service = service;
    }

    // GET /api/environments/accessible
    [HttpGet("accessible")]
    public async Task<IActionResult> Accessible(CancellationToken ct)
    {
        var tenant = HttpContext.TryGetTenantContext();
        if (tenant is null || tenant.EnvironmentId <= 0) return Ok(Array.Empty<TenantEnvironmentEntity>());

        var env = await _service.GetAsync(tenant.TenantId, tenant.EnvironmentId, ct);
        return Ok(env is null ? Array.Empty<TenantEnvironmentEntity>() : [env]);
    }
}
