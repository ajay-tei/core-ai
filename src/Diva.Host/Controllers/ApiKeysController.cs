using Diva.Core.Configuration;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/admin/api-keys")]
[RequireTenantAdmin]
public class ApiKeysController : ControllerBase
{
    private readonly IPlatformApiKeyService _keys;
    private readonly ILogger<ApiKeysController> _logger;

    public ApiKeysController(IPlatformApiKeyService keys, ILogger<ApiKeysController> logger)
    {
        _keys = keys;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/api-keys?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var keys = await _keys.ListAsync(tid, ct);
        return Ok(keys);
    }

    // POST /api/admin/api-keys
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        var ctx = HttpContext.TryGetTenantContext();

        var request = new CreateApiKeyRequest(dto.Name, dto.Scope, dto.AllowedAgentIds, dto.ExpiresAt, dto.AllowedGroupIds);
        var result = await _keys.CreateAsync(tid, ctx?.UserId ?? "unknown", request, ct);

        _logger.LogInformation("API key created: {Name} (scope={Scope}) for tenant {TenantId}",
            result.Name, result.Scope, tid);

        // RawKey is returned ONCE — client must store it securely
        return Ok(result);
    }

    // DELETE /api/admin/api-keys/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Revoke(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        await _keys.RevokeAsync(tid, id, ct);
        return NoContent();
    }

    // POST /api/admin/api-keys/{id}/rotate
    [HttpPost("{id:int}/rotate")]
    public async Task<IActionResult> Rotate(int id, [FromBody] RotateApiKeyDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        var ctx = HttpContext.TryGetTenantContext();
        var result = await _keys.RotateAsync(tid, id, ctx?.UserId ?? "unknown", ct);

        // New RawKey returned ONCE
        return Ok(result);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record CreateApiKeyDto(
    string Name,
    string Scope = "invoke",
    string[]? AllowedAgentIds = null,
    DateTime? ExpiresAt = null,
    int TenantId = 1,
    string[]? AllowedGroupIds = null);

public record RotateApiKeyDto(int TenantId = 1);
