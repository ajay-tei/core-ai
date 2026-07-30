using Diva.Core.Configuration;
using Diva.Core.Extensions;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Extensions;
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
    // Returns the full unbounded array — used by dropdown/selector callers (AgentBuilder.tsx,
    // McpServerManager.tsx's own credential-mapping dropdowns).
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var rows = await db.McpCredentials
            .Where(c => c.TenantId == tid)
            .OrderByDescending(c => c.CreatedAt)
            .AsNoTracking()
            .Select(ProjectRow)
            .ToListAsync(ct);

        // Decrypt in memory to expose only a short masked hint (last 4 chars) for verification.
        return Ok(rows.Select(ToListItem));
    }

    // GET /api/admin/credentials/paged?tenantId=1&search=&page=1&pageSize=25
    // Dedicated paginated endpoint for the admin MCP Credentials list page.
    [HttpGet("paged")]
    public async Task<IActionResult> ListPaged(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var query = db.McpCredentials.Where(c => c.TenantId == tid);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(c => c.Name.Contains(q));
        }

        var paged = await query
            .OrderByDescending(c => c.CreatedAt)
            .AsNoTracking()
            .Select(ProjectRow)
            .ToPagedResultAsync(page, pageSize, ct);

        // Decrypt only this page's rows in memory to expose a short masked hint (last 4 chars).
        // Never return the full key. Decryption can fail if the master key was rotated — mask as null.
        return Ok(paged.MapItems(ToListItem));
    }

    private static readonly System.Linq.Expressions.Expression<Func<McpCredentialEntity, CredentialRow>> ProjectRow = c => new CredentialRow(
        c.Id, c.Name, c.AuthScheme, c.CustomHeaderName, c.Description,
        c.CreatedAt, c.ExpiresAt, c.IsActive, c.LastUsedAt, c.CreatedByUserId, c.EncryptedApiKey);

    private object ToListItem(CredentialRow c) => new
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
        c.CreatedByUserId,
        ApiKeyHint = MaskKey(c.EncryptedApiKey)
    };

    private sealed record CredentialRow(
        int Id, string Name, string AuthScheme, string? CustomHeaderName, string? Description,
        DateTime CreatedAt, DateTime? ExpiresAt, bool IsActive, DateTime? LastUsedAt,
        string? CreatedByUserId, string? EncryptedApiKey);


    /// <summary>
    /// Decrypts a stored key and returns a masked hint exposing only the last 4 characters
    /// (e.g. "••••cd12"). Returns null when the key is empty or cannot be decrypted.
    /// </summary>
    private string? MaskKey(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try
        {
            var plain = _encryptor.Decrypt(encrypted);
            if (string.IsNullOrEmpty(plain)) return null;
            var tail = plain.Length <= 4 ? plain : plain[^4..];
            return "••••" + tail;
        }
        catch
        {
            // Undecryptable (e.g. master key rotated) — omit the hint rather than fail the list.
            return null;
        }
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
