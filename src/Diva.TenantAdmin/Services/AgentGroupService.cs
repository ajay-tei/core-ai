using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// Manages agent access groups and evaluates per-user / per-role authorization
/// for invoking grouped agents. Singleton-safe — creates a new DbContext per call
/// via <see cref="IDatabaseProviderFactory"/> and caches the per-tenant restricted
/// group map in <see cref="IMemoryCache"/> (5-min TTL) since the access check runs
/// on the hot invoke path.
/// </summary>
public sealed class AgentGroupService : IAgentGroupService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AgentGroupService> _logger;

    private const string CachePrefix = "agentgroups:restricted:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public AgentGroupService(
        IDatabaseProviderFactory db,
        IMemoryCache cache,
        ILogger<AgentGroupService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>One restricted group's grants, indexed per member agent in the cache map.</summary>
    private sealed record RestrictedGroup(string GroupId, HashSet<string> AllowedUserIds, HashSet<string> AllowedRoles);

    // ── CRUD ────────────────────────────────────────────────────────────────

    public async Task<List<AgentGroupEntity>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.AgentGroups
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<AgentGroupEntity?> GetAsync(int tenantId, string id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.AgentGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
    }

    public async Task<AgentGroupEntity> CreateAsync(int tenantId, AgentGroupDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new AgentGroupEntity
        {
            TenantId = tenantId,
            Name = dto.Name,
            Description = dto.Description,
            AgentIdsJson = Serialize(dto.AgentIds),
            AllowedUserIdsJson = Serialize(dto.AllowedUserIds),
            AllowedRolesJson = Serialize(dto.AllowedRoles),
            CreatedAt = DateTime.UtcNow,
        };
        db.AgentGroups.Add(entity);
        await db.SaveChangesAsync(ct);
        InvalidateForTenant(tenantId);
        _logger.LogInformation("Agent group created: {Name} ({Id}) for tenant {TenantId}", entity.Name, entity.Id, tenantId);
        return entity;
    }

    public async Task<AgentGroupEntity?> UpdateAsync(int tenantId, string id, AgentGroupDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.AgentGroups.FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (entity is null) return null;

        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.AgentIdsJson = Serialize(dto.AgentIds);
        entity.AllowedUserIdsJson = Serialize(dto.AllowedUserIds);
        entity.AllowedRolesJson = Serialize(dto.AllowedRoles);
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        InvalidateForTenant(tenantId);
        return entity;
    }

    public async Task<bool> DeleteAsync(int tenantId, string id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.AgentGroups.FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (entity is null) return false;

        db.AgentGroups.Remove(entity);
        await db.SaveChangesAsync(ct);
        InvalidateForTenant(tenantId);
        return true;
    }

    // ── Access evaluation ───────────────────────────────────────────────────

    public async Task<bool> CanInvokeAgentAsync(string agentId, TenantContext tenant, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId)) return true;

        var map = await GetRestrictedMapAsync(tenant.TenantId, ct);
        if (!map.TryGetValue(agentId, out var groups) || groups.Count == 0)
            return true;  // not in any restricted group → open (backward compatible)

        return groups.Any(g => IsGranted(g, tenant));
    }

    public async Task<HashSet<string>> GetDeniedAgentIdsAsync(TenantContext tenant, CancellationToken ct)
    {
        var map = await GetRestrictedMapAsync(tenant.TenantId, ct);
        var denied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (agentId, groups) in map)
        {
            if (!groups.Any(g => IsGranted(g, tenant)))
                denied.Add(agentId);
        }
        return denied;
    }

    private static bool IsGranted(RestrictedGroup group, TenantContext tenant)
    {
        // Admins always pass.
        if (tenant.IsAdmin || tenant.IsMasterAdmin) return true;

        // Explicit group grant (API key or claim).
        if (tenant.GroupAccess.Contains(group.GroupId, StringComparer.OrdinalIgnoreCase)) return true;

        // User identity match (id or email).
        if (!string.IsNullOrEmpty(tenant.UserId) && group.AllowedUserIds.Contains(tenant.UserId)) return true;
        if (!string.IsNullOrEmpty(tenant.UserEmail) && group.AllowedUserIds.Contains(tenant.UserEmail)) return true;

        // Role / SSO group match (union).
        foreach (var r in tenant.UserRoles)
            if (group.AllowedRoles.Contains(r)) return true;
        foreach (var g in tenant.UserGroups)
            if (group.AllowedRoles.Contains(g)) return true;

        return false;
    }

    // ── Cache ───────────────────────────────────────────────────────────────

    public void InvalidateForTenant(int tenantId)
    {
        _cache.Remove(CachePrefix + tenantId);
    }

    /// <summary>
    /// Builds (or returns cached) map of agentId → restricted groups containing it.
    /// Only groups with a non-empty allow-list (users or roles) are "restricted".
    /// </summary>
    private async Task<Dictionary<string, List<RestrictedGroup>>> GetRestrictedMapAsync(int tenantId, CancellationToken ct)
    {
        var key = CachePrefix + tenantId;
        if (_cache.TryGetValue(key, out Dictionary<string, List<RestrictedGroup>>? cached) && cached is not null)
            return cached;

        using var db = _db.CreateDbContext();
        var groups = await db.AgentGroups
            .Where(g => g.TenantId == tenantId)
            .AsNoTracking()
            .ToListAsync(ct);

        var map = new Dictionary<string, List<RestrictedGroup>>(StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var allowedUsers = Deserialize(g.AllowedUserIdsJson);
            var allowedRoles = Deserialize(g.AllowedRolesJson);

            // Not restricted unless there is at least one allow-list entry.
            if (allowedUsers.Length == 0 && allowedRoles.Length == 0) continue;

            var info = new RestrictedGroup(
                g.Id,
                new HashSet<string>(allowedUsers, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(allowedRoles, StringComparer.OrdinalIgnoreCase));

            foreach (var agentId in Deserialize(g.AgentIdsJson))
            {
                if (string.IsNullOrEmpty(agentId)) continue;
                if (!map.TryGetValue(agentId, out var list))
                    map[agentId] = list = [];
                list.Add(info);
            }
        }

        _cache.Set(key, map, CacheTtl);
        return map;
    }

    private static string? Serialize(string[] values)
        => values is { Length: > 0 } ? JsonSerializer.Serialize(values) : null;

    private static string[] Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }
}
