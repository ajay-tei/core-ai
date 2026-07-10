using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.TenantAdmin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Integration tests for UserGroupService CRUD and the membership resolver.
/// Membership is stored relationally (member and role rows) — no JSON columns.
/// Uses real SQLite (in-memory) per ADR-010.
/// </summary>
public class UserGroupServiceTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _opts;
    private readonly UserGroupMembershipCache _resolver;
    private readonly UserGroupService _service;

    public UserGroupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _opts = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using (var db = new DivaDbContext(_opts)) db.Database.EnsureCreated();

        var factory = new DirectDbFactory(_opts);
        _resolver = new UserGroupMembershipCache(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserGroupMembershipCache>.Instance);
        _service = new UserGroupService(factory, _resolver, NullLogger<UserGroupService>.Instance);
    }

    private static TenantContext User(string userId = "", string email = "", string[]? roles = null, string[]? ssoGroups = null)
        => new()
        {
            TenantId = TenantId,
            UserId = userId,
            UserEmail = email,
            UserRoles = roles ?? [],
            UserGroups = ssoGroups ?? [],
        };

    [Fact]
    public async Task Create_PersistsMembersAndRoles_AsRows()
    {
        var dto = new UserGroupDto("Finance", "desc",
            [new UserGroupMemberDto("alice", "alice@acme.com"), new UserGroupMemberDto("bob", null)],
            ["finance-role"]);

        var created = await _service.CreateAsync(TenantId, dto, "admin", CancellationToken.None);

        using var db = new DivaDbContext(_opts, currentTenantId: TenantId);
        var members = db.UserGroupMembers.Where(m => m.UserGroupId == created.Id).ToList();
        var roles = db.UserGroupRoles.Where(r => r.UserGroupId == created.Id).ToList();
        Assert.Equal(2, members.Count);
        Assert.Single(roles);
        Assert.Contains(members, m => m.UserId == "alice" && m.Email == "alice@acme.com");
    }

    [Fact]
    public async Task Update_ReplacesChildRows()
    {
        var created = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("alice", null)], ["r1"]), null, CancellationToken.None);

        await _service.UpdateAsync(TenantId, created.Id,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("bob", null)], ["r2"]), CancellationToken.None);

        using var db = new DivaDbContext(_opts, currentTenantId: TenantId);
        var members = db.UserGroupMembers.Where(m => m.UserGroupId == created.Id).Select(m => m.UserId).ToList();
        var roles = db.UserGroupRoles.Where(r => r.UserGroupId == created.Id).Select(r => r.Role).ToList();
        Assert.Equal(["bob"], members);
        Assert.Equal(["r2"], roles);
    }

    [Fact]
    public async Task Delete_CascadesChildRows()
    {
        var created = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("alice", null)], ["r1"]), null, CancellationToken.None);

        Assert.True(await _service.DeleteAsync(TenantId, created.Id, CancellationToken.None));

        using var db = new DivaDbContext(_opts, currentTenantId: TenantId);
        Assert.Empty(db.UserGroupMembers.Where(m => m.UserGroupId == created.Id));
        Assert.Empty(db.UserGroupRoles.Where(r => r.UserGroupId == created.Id));
    }

    [Fact]
    public async Task Resolver_MatchesByUserId()
    {
        var g = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("alice", null)], []), null, CancellationToken.None);

        var ids = await _resolver.GetGroupIdsForUserAsync(User(userId: "alice"), CancellationToken.None);
        Assert.Equal([g.Id], ids);
    }

    [Fact]
    public async Task Resolver_MatchesByEmail()
    {
        var g = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("alice", "alice@acme.com")], []), null, CancellationToken.None);

        var ids = await _resolver.GetGroupIdsForUserAsync(User(userId: "different-id", email: "alice@acme.com"), CancellationToken.None);
        Assert.Equal([g.Id], ids);
    }

    [Fact]
    public async Task Resolver_MatchesByRoleOrSsoGroup()
    {
        var g = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [], ["finance-role"]), null, CancellationToken.None);

        var byRole = await _resolver.GetGroupIdsForUserAsync(User(userId: "x", roles: ["finance-role"]), CancellationToken.None);
        var bySso = await _resolver.GetGroupIdsForUserAsync(User(userId: "y", ssoGroups: ["finance-role"]), CancellationToken.None);
        Assert.Equal([g.Id], byRole);
        Assert.Equal([g.Id], bySso);
    }

    [Fact]
    public async Task Resolver_ReturnsGroupsInAscendingIdOrder()
    {
        var g1 = await _service.CreateAsync(TenantId, new UserGroupDto("A", null, [new UserGroupMemberDto("alice", null)], []), null, CancellationToken.None);
        var g2 = await _service.CreateAsync(TenantId, new UserGroupDto("B", null, [new UserGroupMemberDto("alice", null)], []), null, CancellationToken.None);

        var ids = await _resolver.GetGroupIdsForUserAsync(User(userId: "alice"), CancellationToken.None);
        Assert.Equal([g1.Id, g2.Id], ids);
    }

    [Fact]
    public async Task Resolver_ReflectsWriteAfterInvalidation()
    {
        // Prime cache: alice belongs to no groups.
        Assert.Empty(await _resolver.GetGroupIdsForUserAsync(User(userId: "alice"), CancellationToken.None));

        // Create a group with alice — service invalidates the resolver cache.
        var g = await _service.CreateAsync(TenantId,
            new UserGroupDto("Finance", null, [new UserGroupMemberDto("alice", null)], []), null, CancellationToken.None);

        Assert.Equal([g.Id], await _resolver.GetGroupIdsForUserAsync(User(userId: "alice"), CancellationToken.None));
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
