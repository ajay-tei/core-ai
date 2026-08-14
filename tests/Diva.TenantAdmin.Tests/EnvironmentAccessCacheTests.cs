using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Unit tests for <see cref="EnvironmentAccessCache"/> — the Phase 5 environment ACL grant
/// check (AllowedRolesJson + linked user groups), evaluated the same way TenantContextMiddleware
/// and EnvironmentAccessController use it. Uses real SQLite (in-memory) per ADR-010.
/// </summary>
public class EnvironmentAccessCacheTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DivaDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly EnvironmentAccessCache _access;

    public EnvironmentAccessCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DivaDbContext(opts);
        _db.Database.EnsureCreated();

        _cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new DirectDbFactory(opts);
        var userGroups = new UserGroupMembershipCache(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserGroupMembershipCache>.Instance);
        _access = new EnvironmentAccessCache(factory, userGroups, _cache);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _connection.Dispose();
    }

    private static TenantContext User(string userId, string[]? roles = null, string[]? groups = null, bool isAdmin = false, bool isMasterAdmin = false) => new()
    {
        // IsMasterAdmin requires TenantId == 0 (see TenantContext.IsMasterAdmin) — every other
        // caller stays scoped to this test class's tenant.
        TenantId = isMasterAdmin ? 0 : TenantId,
        UserId = userId,
        UserRoles = isAdmin ? [.. roles ?? [], "admin"] : isMasterAdmin ? [.. roles ?? [], "master_admin"] : roles ?? [],
        UserGroups = groups ?? [],
        EnvironmentId = 0,
    };

    private TenantEnvironmentEntity SeedEnvironment(string slug, string[]? allowedRoles = null)
    {
        var env = new TenantEnvironmentEntity
        {
            TenantId = TenantId,
            Slug = slug,
            DisplayName = slug,
            Rank = 0,
            AllowedRolesJson = allowedRoles is { Length: > 0 } ? System.Text.Json.JsonSerializer.Serialize(allowedRoles) : null,
        };
        _db.TenantEnvironments.Add(env);
        _db.SaveChanges();
        return env;
    }

    private UserGroupEntity SeedUserGroupWithMember(string name, string memberUserId)
    {
        var group = new UserGroupEntity { TenantId = TenantId, Name = name };
        group.Members.Add(new UserGroupMemberEntity { TenantId = TenantId, UserId = memberUserId });
        _db.UserGroups.Add(group);
        _db.SaveChanges();
        return group;
    }

    private void LinkEnvironmentToUserGroup(int environmentId, int userGroupId)
    {
        _db.EnvironmentUserGroups.Add(new EnvironmentUserGroupEntity { TenantId = TenantId, EnvironmentId = environmentId, UserGroupId = userGroupId });
        _db.SaveChanges();
    }

    // ── Admin bypass ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CanAccess_Admin_AlwaysGranted_EvenWhenRestricted()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("bob", isAdmin: true), CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public async Task CanAccess_MasterAdmin_AlwaysGranted()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("root", isMasterAdmin: true), CancellationToken.None);
        Assert.True(ok);
    }

    // ── Unrestricted environments ─────────────────────────────────────────────

    [Fact]
    public async Task CanAccess_UnrestrictedEnvironment_GrantedToAnyone()
    {
        var env = SeedEnvironment("dev");
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("alice"), CancellationToken.None);
        Assert.True(ok);
    }

    // ── Role / SSO-group match ────────────────────────────────────────────────

    [Fact]
    public async Task CanAccess_RoleRestricted_MatchingRole_Granted()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("alice", roles: ["finance"]), CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public async Task CanAccess_RoleRestricted_NonMatchingRole_Denied()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("bob", roles: ["sales"]), CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public async Task CanAccess_RoleRestricted_MatchingSsoGroup_Granted()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("carol", groups: ["finance"]), CancellationToken.None);
        Assert.True(ok);
    }

    // ── User-group link match ─────────────────────────────────────────────────

    [Fact]
    public async Task CanAccess_UserGroupRestricted_Member_Granted()
    {
        var env = SeedEnvironment("staging");
        var group = SeedUserGroupWithMember("Finance Team", "alice");
        LinkEnvironmentToUserGroup(env.Id, group.Id);

        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("alice"), CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public async Task CanAccess_UserGroupRestricted_NonMember_Denied()
    {
        var env = SeedEnvironment("staging");
        var group = SeedUserGroupWithMember("Finance Team", "alice");
        LinkEnvironmentToUserGroup(env.Id, group.Id);

        var ok = await _access.CanAccessEnvironmentAsync(env.Id, User("bob"), CancellationToken.None);
        Assert.False(ok);
    }

    // ── Unknown environment ───────────────────────────────────────────────────

    [Fact]
    public async Task CanAccess_UnknownEnvironmentId_Denied()
    {
        var ok = await _access.CanAccessEnvironmentAsync(999, User("alice"), CancellationToken.None);
        Assert.False(ok);
    }

    // ── GetAccessibleEnvironmentIdsAsync ──────────────────────────────────────

    [Fact]
    public async Task GetAccessibleEnvironmentIds_ReturnsOnlyGrantedSubset()
    {
        var open = SeedEnvironment("dev");
        var restricted = SeedEnvironment("staging", ["finance"]);
        SeedEnvironment("prod", ["ops"]);

        var ids = await _access.GetAccessibleEnvironmentIdsAsync(User("alice", roles: ["finance"]), CancellationToken.None);

        Assert.Contains(open.Id, ids);
        Assert.Contains(restricted.Id, ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task GetAccessibleEnvironmentIds_Admin_ReturnsAll()
    {
        SeedEnvironment("dev");
        SeedEnvironment("staging", ["finance"]);
        SeedEnvironment("prod", ["ops"]);

        var ids = await _access.GetAccessibleEnvironmentIdsAsync(User("root", isAdmin: true), CancellationToken.None);
        Assert.Equal(3, ids.Count);
    }

    // ── Cache invalidation ─────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidateForTenant_ForcesFreshResolution_AfterRuleIsUpdated()
    {
        var env = SeedEnvironment("staging", ["finance"]);
        Assert.False(await _access.CanAccessEnvironmentAsync(env.Id, User("bob", roles: ["sales"]), CancellationToken.None));

        env.AllowedRolesJson = System.Text.Json.JsonSerializer.Serialize(new[] { "finance", "sales" });
        _db.SaveChanges();
        _access.InvalidateForTenant(TenantId);

        Assert.True(await _access.CanAccessEnvironmentAsync(env.Id, User("bob", roles: ["sales"]), CancellationToken.None));
    }
}
