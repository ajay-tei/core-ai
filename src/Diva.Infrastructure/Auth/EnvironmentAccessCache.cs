using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Resolves a caller's accessible tenant environments from TenantEnvironmentEntity's
/// AllowedRolesJson + linked user groups (EnvironmentUserGroupEntity), caching the
/// per-tenant rules in <see cref="IMemoryCache"/> (5-min TTL) since environment
/// resolution runs on every authenticated request (TenantContextMiddleware).
/// Singleton-safe: creates a new DbContext per call via <see cref="IDatabaseProviderFactory"/>.
/// </summary>
public sealed class EnvironmentAccessCache : IEnvironmentAccessResolver
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IUserGroupResolver _userGroups;
    private readonly IMemoryCache _cache;

    private const string CachePrefix = "environments:access:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public EnvironmentAccessCache(IDatabaseProviderFactory db, IUserGroupResolver userGroups, IMemoryCache cache)
    {
        _db = db;
        _userGroups = userGroups;
        _cache = cache;
    }

    private sealed record AccessRule(int EnvironmentId, HashSet<string> AllowedRoles, HashSet<int> AllowedUserGroupIds)
    {
        public bool IsUnrestricted => AllowedRoles.Count == 0 && AllowedUserGroupIds.Count == 0;
    }

    private async Task<List<AccessRule>> GetRulesAsync(int tenantId, CancellationToken ct)
    {
        var key = CachePrefix + tenantId;
        if (_cache.TryGetValue(key, out List<AccessRule>? cached) && cached is not null) return cached;

        using var db = _db.CreateDbContext();
        var envs = await db.TenantEnvironments
            .Where(e => e.TenantId == tenantId)
            .Include(e => e.UserGroupLinks)
            .AsNoTracking()
            .ToListAsync(ct);

        var rules = envs.Select(e => new AccessRule(
            e.Id,
            Deserialize(e.AllowedRolesJson),
            new HashSet<int>(e.UserGroupLinks.Select(l => l.UserGroupId)))).ToList();

        _cache.Set(key, rules, CacheTtl);
        return rules;
    }

    private static bool IsGranted(AccessRule rule, TenantContext tenant, HashSet<int> userGroupIds)
    {
        if (rule.IsUnrestricted) return true;

        foreach (var r in tenant.UserRoles) if (rule.AllowedRoles.Contains(r)) return true;
        foreach (var g in tenant.UserGroups) if (rule.AllowedRoles.Contains(g)) return true;
        foreach (var gid in userGroupIds) if (rule.AllowedUserGroupIds.Contains(gid)) return true;

        return false;
    }

    public async Task<bool> CanAccessEnvironmentAsync(int environmentId, TenantContext tenant, CancellationToken ct)
    {
        if (tenant.IsAdmin || tenant.IsMasterAdmin) return true;

        var rules = await GetRulesAsync(tenant.TenantId, ct);
        var rule = rules.FirstOrDefault(r => r.EnvironmentId == environmentId);
        if (rule is null) return false;
        if (rule.IsUnrestricted) return true;

        var userGroupIds = (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet();
        return IsGranted(rule, tenant, userGroupIds);
    }

    public async Task<IReadOnlyCollection<int>> GetAccessibleEnvironmentIdsAsync(TenantContext tenant, CancellationToken ct)
    {
        var rules = await GetRulesAsync(tenant.TenantId, ct);
        if (tenant.IsAdmin || tenant.IsMasterAdmin) return rules.Select(r => r.EnvironmentId).ToList();

        var userGroupIds = rules.Any(r => r.AllowedUserGroupIds.Count > 0)
            ? (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet()
            : [];

        return rules.Where(r => IsGranted(r, tenant, userGroupIds)).Select(r => r.EnvironmentId).ToList();
    }

    public void InvalidateForTenant(int tenantId) => _cache.Remove(CachePrefix + tenantId);

    private static HashSet<string> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { return new HashSet<string>(System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [], StringComparer.OrdinalIgnoreCase); }
        catch (System.Text.Json.JsonException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
}
