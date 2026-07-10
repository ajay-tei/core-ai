using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Resolves a caller's user-group memberships from the relational UserGroup tables,
/// caching the per-tenant membership rules in <see cref="IMemoryCache"/> (5-min TTL)
/// since the lookup runs on the hot invoke path. Singleton-safe: creates a new
/// DbContext per call via <see cref="IDatabaseProviderFactory"/>.
///
/// Known limitation (accepted, consistent with other tenant-config caches): in a
/// multi-instance deployment a write invalidates only the local cache; staleness is
/// bounded by the TTL.
/// </summary>
public sealed class UserGroupMembershipCache : IUserGroupResolver
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserGroupMembershipCache> _logger;

    private const string CachePrefix = "usergroups:membership:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public UserGroupMembershipCache(
        IDatabaseProviderFactory db,
        IMemoryCache cache,
        ILogger<UserGroupMembershipCache> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>One group's membership rule (explicit users/emails + role/SSO-group includes).</summary>
    private sealed record GroupRule(
        int GroupId,
        HashSet<string> UserIds,
        HashSet<string> Emails,
        HashSet<string> Roles);

    public async Task<IReadOnlyCollection<int>> GetGroupIdsForUserAsync(TenantContext tenant, CancellationToken ct)
    {
        if (tenant is null) return [];

        var rules = await GetRulesAsync(tenant.TenantId, ct);
        if (rules.Count == 0) return [];

        // SortedSet → ascending id order so the oldest group wins any downstream tie-break.
        var matched = new SortedSet<int>();
        foreach (var rule in rules)
        {
            if (!string.IsNullOrEmpty(tenant.UserId) && rule.UserIds.Contains(tenant.UserId))
            {
                matched.Add(rule.GroupId);
                continue;
            }
            if (!string.IsNullOrEmpty(tenant.UserEmail) && rule.Emails.Contains(tenant.UserEmail))
            {
                matched.Add(rule.GroupId);
                continue;
            }

            // Role / SSO group auto-include (union of roles and SSO groups).
            var roleHit = false;
            foreach (var r in tenant.UserRoles)
                if (rule.Roles.Contains(r)) { roleHit = true; break; }
            if (!roleHit)
                foreach (var g in tenant.UserGroups)
                    if (rule.Roles.Contains(g)) { roleHit = true; break; }

            if (roleHit) matched.Add(rule.GroupId);
        }

        return matched;
    }

    public void Invalidate(int tenantId) => _cache.Remove(CachePrefix + tenantId);

    private async Task<List<GroupRule>> GetRulesAsync(int tenantId, CancellationToken ct)
    {
        var key = CachePrefix + tenantId;
        if (_cache.TryGetValue(key, out List<GroupRule>? cached) && cached is not null)
            return cached;

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var groups = await db.UserGroups
            .Where(g => g.TenantId == tenantId)
            .Select(g => new
            {
                g.Id,
                Users = g.Members.Select(m => m.UserId).ToList(),
                Emails = g.Members.Select(m => m.Email).ToList(),
                Roles = g.Roles.Select(r => r.Role).ToList(),
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var rules = groups.Select(g => new GroupRule(
            g.Id,
            new HashSet<string>(g.Users.Where(u => !string.IsNullOrEmpty(u)), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(g.Emails.Where(e => !string.IsNullOrEmpty(e))!, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(g.Roles.Where(r => !string.IsNullOrEmpty(r)), StringComparer.OrdinalIgnoreCase)))
            .ToList();

        _cache.Set(key, rules, CacheTtl);
        return rules;
    }
}
