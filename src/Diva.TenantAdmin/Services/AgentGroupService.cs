using System.Text.Json;
using Diva.Core.Configuration;
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
    private readonly IUserGroupResolver _userGroups;
    private readonly ILogger<AgentGroupService> _logger;

    private const string CachePrefix = "agentgroups:restricted:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public AgentGroupService(
        IDatabaseProviderFactory db,
        IMemoryCache cache,
        IUserGroupResolver userGroups,
        ILogger<AgentGroupService> logger)
    {
        _db = db;
        _cache = cache;
        _userGroups = userGroups;
        _logger = logger;
    }

    /// <summary>One group's grants for a member agent — used to evaluate whether ANY of an agent's
    /// containing groups actually grants the caller. Allow-list only: a group with no allow-list
    /// entries at all grants access to NOBODY (except admins, checked separately).</summary>
    private sealed record GroupGrant(
        string GroupId,
        HashSet<string> AllowedUserIds,
        HashSet<string> AllowedRoles,
        HashSet<int> AllowedUserGroupIds);

    // ── CRUD ────────────────────────────────────────────────────────────────

    public async Task<List<AgentGroupEntity>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.AgentGroups
            .Where(g => g.TenantId == tenantId)
            .Include(g => g.UserGroupLinks)
            .OrderBy(g => g.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<AgentGroupEntity?> GetAsync(int tenantId, string id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.AgentGroups
            .Include(g => g.UserGroupLinks)
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
    }

    public async Task<AgentGroupEntity> CreateAsync(int tenantId, AgentGroupDto dto, int? environmentId, CancellationToken ct)
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
            UserGroupLinks = BuildUserGroupLinks(tenantId, dto.AllowedUserGroupIds),
            // Fresh logical identity for promotion tracking + tag to the caller's current
            // environment (untagged = visible from every environment, by the fallback rule).
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId is > 0 ? environmentId : null,
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
        var entity = await db.AgentGroups
            .Include(g => g.UserGroupLinks)
            .FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (entity is null) return null;

        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.AgentIdsJson = Serialize(dto.AgentIds);
        entity.AllowedUserIdsJson = Serialize(dto.AllowedUserIds);
        entity.AllowedRolesJson = Serialize(dto.AllowedRoles);
        entity.UpdatedAt = DateTime.UtcNow;

        // Replace the user-group grant rows wholesale.
        db.AgentGroupUserGroups.RemoveRange(entity.UserGroupLinks);
        entity.UserGroupLinks = BuildUserGroupLinks(tenantId, dto.AllowedUserGroupIds);

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
        if (tenant.IsAdmin || tenant.IsMasterAdmin) return true;

        var map = await GetAgentGroupMapAsync(tenant.TenantId, ct);
        // Allow-list only: an agent not in ANY group (or in groups that grant nobody) is hidden.
        if (!map.TryGetValue(agentId, out var groups) || groups.Count == 0)
            return false;

        var userGroupIds = await ResolveUserGroupIdsAsync(groups, tenant, ct);
        return groups.Any(g => IsGranted(g, tenant, userGroupIds));
    }

    public async Task<HashSet<string>> GetDeniedAgentIdsAsync(IEnumerable<string> candidateAgentIds, TenantContext tenant, CancellationToken ct)
    {
        var denied = new HashSet<string>(StringComparer.Ordinal);
        if (tenant.IsAdmin || tenant.IsMasterAdmin) return denied;

        var map = await GetAgentGroupMapAsync(tenant.TenantId, ct);

        // Resolve the caller's user-group ids once for the whole candidate set.
        var anyGroupUsesUserGroups = map.Values.Any(list => list.Any(g => g.AllowedUserGroupIds.Count > 0));
        var userGroupIds = anyGroupUsesUserGroups
            ? (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet()
            : new HashSet<int>();

        foreach (var agentId in candidateAgentIds)
        {
            if (string.IsNullOrEmpty(agentId)) continue;
            // Allow-list only: not in ANY group, or in groups but none of them grant this caller.
            if (!map.TryGetValue(agentId, out var groups) || groups.Count == 0 || !groups.Any(g => IsGranted(g, tenant, userGroupIds)))
                denied.Add(agentId);
        }
        return denied;
    }

    public async Task<HashSet<int>> GetAllowedUserGroupIdsForAgentAsync(string agentId, int tenantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(agentId)) return new HashSet<int>();

        var map = await GetAgentGroupMapAsync(tenantId, ct);
        if (!map.TryGetValue(agentId, out var groups) || groups.Count == 0)
            return new HashSet<int>();

        var allowed = new HashSet<int>();
        foreach (var g in groups)
            allowed.UnionWith(g.AllowedUserGroupIds);
        return allowed;
    }

    /// <summary>Resolves the caller's user-group ids only if at least one candidate group uses them.</summary>
    private async Task<HashSet<int>> ResolveUserGroupIdsAsync(
        List<GroupGrant> groups, TenantContext tenant, CancellationToken ct)
    {
        if (!groups.Any(g => g.AllowedUserGroupIds.Count > 0)) return new HashSet<int>();
        return (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet();
    }

    private static bool IsGranted(GroupGrant group, TenantContext tenant, HashSet<int> userGroupIds)
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

        // User-group membership match.
        foreach (var gid in userGroupIds)
            if (group.AllowedUserGroupIds.Contains(gid)) return true;

        return false;
    }

    // ── Cache ───────────────────────────────────────────────────────────────

    public void InvalidateForTenant(int tenantId)
    {
        _cache.Remove(CachePrefix + tenantId);
    }

    /// <summary>
    /// Builds (or returns cached) map of agentId → ALL groups containing it (allow-list only —
    /// every group is tracked, including ones with empty allow-lists, since an agent must belong
    /// to at least one group that actually grants the caller to be visible/invocable at all).
    /// </summary>
    private async Task<Dictionary<string, List<GroupGrant>>> GetAgentGroupMapAsync(int tenantId, CancellationToken ct)
    {
        var key = CachePrefix + tenantId;
        if (_cache.TryGetValue(key, out Dictionary<string, List<GroupGrant>>? cached) && cached is not null)
            return cached;

        using var db = _db.CreateDbContext();
        var groups = await db.AgentGroups
            .Where(g => g.TenantId == tenantId)
            .Include(g => g.UserGroupLinks)
            .AsNoTracking()
            .ToListAsync(ct);

        var map = new Dictionary<string, List<GroupGrant>>(StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var info = new GroupGrant(
                g.Id,
                new HashSet<string>(Deserialize(g.AllowedUserIdsJson), StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(Deserialize(g.AllowedRolesJson), StringComparer.OrdinalIgnoreCase),
                new HashSet<int>(g.UserGroupLinks.Select(l => l.UserGroupId)));

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

    private static List<AgentGroupUserGroupEntity> BuildUserGroupLinks(int tenantId, int[] userGroupIds)
    {
        if (userGroupIds is not { Length: > 0 }) return [];
        return userGroupIds
            .Where(id => id > 0)
            .Distinct()
            .Select(id => new AgentGroupUserGroupEntity { TenantId = tenantId, UserGroupId = id })
            .ToList();
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
