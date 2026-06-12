using Diva.Core.Configuration;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/admin/credentials")]
[RequireTenantAdmin]
public class CredentialsController : ControllerBase
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ICredentialEncryptor _encryptor;
    private readonly ILogger<CredentialsController> _logger;

    public CredentialsController(
        IDatabaseProviderFactory db,
        ICredentialEncryptor encryptor,
        ILogger<CredentialsController> logger)
    {
        _db = db;
        _encryptor = encryptor;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/credentials?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var items = await db.McpCredentials
            .Where(c => c.TenantId == tid)
            .OrderByDescending(c => c.CreatedAt)
            .AsNoTracking()
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.AuthScheme,
                c.CustomHeaderName,
                c.Description,
                c.CreatedAt,
                c.ExpiresAt,
                c.IsActive,
                c.LastUsedAt,
                c.CreatedByUserId
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    // POST /api/admin/credentials
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCredentialDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        var ctx = HttpContext.TryGetTenantContext();

        var encrypted = _encryptor.Encrypt(dto.ApiKey);

        var entity = new McpCredentialEntity
        {
            TenantId = tid,
            Name = dto.Name,
            EncryptedApiKey = encrypted,
            AuthScheme = dto.AuthScheme ?? "Bearer",
            CustomHeaderName = dto.CustomHeaderName,
            Description = dto.Description,
            ExpiresAt = dto.ExpiresAt,
            CreatedByUserId = ctx?.UserId
        };

        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        db.McpCredentials.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Credential created: {Name} (scheme={Scheme}) for tenant {TenantId}",
            entity.Name, entity.AuthScheme, tid);

        return Ok(new { entity.Id, entity.Name, entity.AuthScheme });
    }

    // PUT /api/admin/credentials/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCredentialDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.McpCredentials.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid, ct);
        if (entity is null) return NotFound();

        if (dto.Name is not null) entity.Name = dto.Name;
        if (dto.AuthScheme is not null) entity.AuthScheme = dto.AuthScheme;
        if (dto.CustomHeaderName is not null) entity.CustomHeaderName = dto.CustomHeaderName;
        if (dto.Description is not null) entity.Description = dto.Description;
        if (dto.ExpiresAt.HasValue) entity.ExpiresAt = dto.ExpiresAt;
        if (dto.IsActive.HasValue) entity.IsActive = dto.IsActive.Value;

        // If a new API key is provided, re-encrypt
        if (!string.IsNullOrEmpty(dto.NewApiKey))
            entity.EncryptedApiKey = _encryptor.Encrypt(dto.NewApiKey);

        await db.SaveChangesAsync(ct);
        return Ok(new { entity.Id, entity.Name, entity.AuthScheme });
    }

    // DELETE /api/admin/credentials/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.McpCredentials.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid, ct);
        if (entity is null) return NotFound();

        db.McpCredentials.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // POST /api/admin/credentials/{id}/rotate?tenantId=1
    [HttpPost("{id:int}/rotate")]
    public async Task<IActionResult> Rotate(int id, [FromBody] RotateCredentialDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.McpCredentials.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tid, ct);
        if (entity is null) return NotFound();

        entity.EncryptedApiKey = _encryptor.Encrypt(dto.NewApiKey);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Credential rotated: {Name} for tenant {TenantId}", entity.Name, tid);
        return Ok(new { entity.Id, entity.Name, Message = "Key rotated successfully" });
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record CreateCredentialDto(
    string Name,
    string ApiKey,
    string? AuthScheme,
    string? CustomHeaderName,
    string? Description,
    DateTime? ExpiresAt,
    int TenantId = 1);

public record UpdateCredentialDto(
    string? Name,
    string? AuthScheme,
    string? CustomHeaderName,
    string? Description,
    DateTime? ExpiresAt,
    bool? IsActive,
    string? NewApiKey,
    int TenantId = 1);

public record RotateCredentialDto(
    string NewApiKey,
    int TenantId = 1);
