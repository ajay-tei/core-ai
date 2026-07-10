using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diva.Host.Controllers;

/// <summary>
/// CRUD for tenant-scoped shared MCP tool servers. A shared server decouples the connection
/// (transport/command/endpoint) from individual agents and carries per-API-key credential
/// routing rules, so one server can serve many agents and pick a credential dynamically based
/// on the platform API key used to invoke the agent.
/// </summary>
[ApiController]
[Route("api/admin/mcp-servers")]
[RequireTenantAdmin]
public class McpServersController : ControllerBase
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<McpServersController> _logger;

    public McpServersController(IDatabaseProviderFactory db, ILogger<McpServersController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/mcp-servers?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var items = await db.TenantMcpServers
            .Where(s => s.TenantId == tid)
            .Include(s => s.UserGroupCredentials)
            .OrderBy(s => s.Name)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(items.Select(ToDto));
    }

    // GET /api/admin/mcp-servers/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.TenantMcpServers
            .Include(s => s.UserGroupCredentials)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tid, ct);
        return entity is null ? NotFound() : Ok(ToDto(entity));
    }

    // POST /api/admin/mcp-servers
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMcpServerDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { error = "Name is required." });

        var ctx = HttpContext.TryGetTenantContext();
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));

        var exists = await db.TenantMcpServers.AnyAsync(s => s.TenantId == tid && s.Name == dto.Name, ct);
        if (exists)
            return Conflict(new { error = $"An MCP server named '{dto.Name}' already exists for this tenant." });

        var entity = new TenantMcpServerEntity
        {
            TenantId = tid,
            Name = dto.Name.Trim(),
            Description = dto.Description,
            Transport = string.IsNullOrWhiteSpace(dto.Transport) ? "stdio" : dto.Transport.Trim(),
            Command = dto.Command,
            ArgsJson = dto.ArgsJson,
            EnvJson = dto.EnvJson,
            Endpoint = dto.Endpoint,
            PassSsoToken = dto.PassSsoToken,
            PassTenantHeaders = dto.PassTenantHeaders,
            DefaultCredentialRef = dto.DefaultCredentialRef,
            ApiKeyCredentialMappingsJson = dto.ApiKeyCredentialMappingsJson,
            CreatedByUserId = ctx?.UserId,
            UserGroupCredentials = BuildGroupCredentials(tid, dto.UserGroupCredentials),
        };

        db.TenantMcpServers.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Shared MCP server created: {Name} (transport={Transport}) for tenant {TenantId}",
            entity.Name, entity.Transport, tid);

        return Ok(ToDto(entity));
    }

    // PUT /api/admin/mcp-servers/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateMcpServerDto dto, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(dto.TenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.TenantMcpServers
            .Include(s => s.UserGroupCredentials)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tid, ct);
        if (entity is null) return NotFound();

        if (dto.Name is not null && !dto.Name.Equals(entity.Name, StringComparison.Ordinal))
        {
            var clash = await db.TenantMcpServers.AnyAsync(
                s => s.TenantId == tid && s.Name == dto.Name && s.Id != id, ct);
            if (clash)
                return Conflict(new { error = $"An MCP server named '{dto.Name}' already exists for this tenant." });
            entity.Name = dto.Name.Trim();
        }

        if (dto.Description is not null) entity.Description = dto.Description;
        if (dto.Transport is not null) entity.Transport = string.IsNullOrWhiteSpace(dto.Transport) ? "stdio" : dto.Transport.Trim();
        if (dto.Command is not null) entity.Command = dto.Command;
        if (dto.ArgsJson is not null) entity.ArgsJson = dto.ArgsJson;
        if (dto.EnvJson is not null) entity.EnvJson = dto.EnvJson;
        if (dto.Endpoint is not null) entity.Endpoint = dto.Endpoint;
        if (dto.PassSsoToken.HasValue) entity.PassSsoToken = dto.PassSsoToken.Value;
        if (dto.PassTenantHeaders.HasValue) entity.PassTenantHeaders = dto.PassTenantHeaders.Value;
        if (dto.DefaultCredentialRef is not null) entity.DefaultCredentialRef = dto.DefaultCredentialRef;
        if (dto.ApiKeyCredentialMappingsJson is not null) entity.ApiKeyCredentialMappingsJson = dto.ApiKeyCredentialMappingsJson;

        // Replace the per-user-group credential rows wholesale when provided.
        if (dto.UserGroupCredentials is not null)
        {
            db.McpServerUserGroupCredentials.RemoveRange(entity.UserGroupCredentials);
            entity.UserGroupCredentials = BuildGroupCredentials(tid, dto.UserGroupCredentials);
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Shared MCP server updated: {Name} for tenant {TenantId}", entity.Name, tid);
        return Ok(ToDto(entity));
    }

    // DELETE /api/admin/mcp-servers/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tid));
        var entity = await db.TenantMcpServers.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tid, ct);
        if (entity is null) return NotFound();

        db.TenantMcpServers.Remove(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Shared MCP server deleted: {Name} for tenant {TenantId}", entity.Name, tid);
        return NoContent();
    }

    private static McpServerDto ToDto(TenantMcpServerEntity s) => new(
        s.Id, s.Name, s.Description, s.Transport, s.Command, s.ArgsJson, s.EnvJson,
        s.Endpoint, s.PassSsoToken, s.PassTenantHeaders, s.DefaultCredentialRef,
        s.ApiKeyCredentialMappingsJson,
        s.UserGroupCredentials
            .OrderBy(c => c.UserGroupId)
            .Select(c => new UserGroupCredentialMapping(c.UserGroupId, c.CredentialRef))
            .ToArray(),
        s.CreatedAt, s.UpdatedAt, s.CreatedByUserId);

    private static List<McpServerUserGroupCredentialEntity> BuildGroupCredentials(
        int tenantId, UserGroupCredentialMapping[]? mappings)
    {
        if (mappings is not { Length: > 0 }) return [];
        var seen = new HashSet<int>();
        var result = new List<McpServerUserGroupCredentialEntity>();
        foreach (var m in mappings)
        {
            if (m.UserGroupId <= 0 || string.IsNullOrWhiteSpace(m.CredentialRef)) continue;
            if (!seen.Add(m.UserGroupId)) continue;   // one credential per group per server
            result.Add(new McpServerUserGroupCredentialEntity
            {
                TenantId = tenantId,
                UserGroupId = m.UserGroupId,
                CredentialRef = m.CredentialRef.Trim(),
            });
        }
        return result;
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record McpServerDto(
    int Id,
    string Name,
    string? Description,
    string Transport,
    string? Command,
    string? ArgsJson,
    string? EnvJson,
    string? Endpoint,
    bool PassSsoToken,
    bool PassTenantHeaders,
    string? DefaultCredentialRef,
    string? ApiKeyCredentialMappingsJson,
    UserGroupCredentialMapping[] UserGroupCredentials,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? CreatedByUserId);

/// <summary>Per-user-group credential mapping for a shared MCP server (relational row).</summary>
public record UserGroupCredentialMapping(int UserGroupId, string CredentialRef);

public record CreateMcpServerDto(
    string Name,
    string? Description,
    string? Transport,
    string? Command,
    string? ArgsJson,
    string? EnvJson,
    string? Endpoint,
    bool PassSsoToken,
    bool PassTenantHeaders,
    string? DefaultCredentialRef,
    string? ApiKeyCredentialMappingsJson,
    UserGroupCredentialMapping[]? UserGroupCredentials = null,
    int TenantId = 1);

public record UpdateMcpServerDto(
    string? Name,
    string? Description,
    string? Transport,
    string? Command,
    string? ArgsJson,
    string? EnvJson,
    string? Endpoint,
    bool? PassSsoToken,
    bool? PassTenantHeaders,
    string? DefaultCredentialRef,
    string? ApiKeyCredentialMappingsJson,
    UserGroupCredentialMapping[]? UserGroupCredentials = null,
    int TenantId = 1);
