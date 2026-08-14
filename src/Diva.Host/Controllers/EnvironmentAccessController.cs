using Diva.Core.Configuration;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data.Entities;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Non-admin-reachable, read-only environment info — distinct from the admin-only
/// EnvironmentsController (api/admin/environments). Returns every environment the caller
/// may access (TenantEnvironmentEntity.AllowedRolesJson / linked user groups, evaluated via
/// IEnvironmentAccessResolver) so the frontend can reuse EnvironmentSwitcher unchanged
/// whether that's 0, 1, or several environments.
/// </summary>
[ApiController]
[Route("api/environments")]
public class EnvironmentAccessController : ControllerBase
{
    private readonly IEnvironmentService _service;
    private readonly IEnvironmentAccessResolver _access;

    public EnvironmentAccessController(IEnvironmentService service, IEnvironmentAccessResolver access)
    {
        _service = service;
        _access = access;
    }

    [HttpGet("accessible")]
    public async Task<IActionResult> Accessible(CancellationToken ct)
    {
        var tenant = HttpContext.TryGetTenantContext();
        if (tenant is null) return Ok(Array.Empty<TenantEnvironmentEntity>());

        var all = await _service.ListAsync(tenant.TenantId, ct);
        var accessibleIds = (await _access.GetAccessibleEnvironmentIdsAsync(tenant, ct)).ToHashSet();
        return Ok(all.Where(e => accessibleIds.Contains(e.Id)).ToList());
    }
}
