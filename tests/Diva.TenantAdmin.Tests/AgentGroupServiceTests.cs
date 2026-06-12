using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.TenantAdmin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Integration tests for AgentGroupService access evaluation.
/// Uses real SQLite (in-memory) per ADR-010 — no mocked DbContext.
/// </summary>
public class AgentGroupServiceTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DivaDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly AgentGroupService _service;

    public AgentGroupServiceTests()
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
        _service = new AgentGroupService(factory, _cache, NullLogger<AgentGroupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _connection.Dispose();
    }

    private static TenantContext User(string userId, string email = "", string[]? roles = null, string[]? groups = null, string[]? groupAccess = null) => new()
    {
        TenantId = TenantId,
        UserId = userId,
        UserEmail = email,
        UserRoles = roles ?? [],
        UserGroups = groups ?? [],
        GroupAccess = groupAccess ?? [],
    };

    private async Task SeedGroupAsync(string name, string[] agentIds, string[] allowedUsers, string[] allowedRoles)
    {
        await _service.CreateAsync(TenantId, new AgentGroupDto(name, null, agentIds, allowedUsers, allowedRoles), CancellationToken.None);
    }

    // ── Backward compatibility ────────────────────────────────────────────────

    [Fact]
    public async Task CanInvoke_AgentNotInAnyGroup_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], ["alice"], []);
        var ok = await _service.CanInvokeAgentAsync("agent-other", User("bob"), CancellationToken.None);
        Assert.True(ok);
    }

    [Fact]
    public async Task CanInvoke_UnrestrictedGroup_Allowed()
    {
        await SeedGroupAsync("Open", ["agent-a"], [], []);  // empty allow-lists => not restricted
        var ok = await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None);
        Assert.True(ok);
    }

    // ── User / role / group matching ──────────────────────────────────────────

    [Fact]
    public async Task CanInvoke_UserIdMatch_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], ["alice"], []);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("alice"), CancellationToken.None));
        Assert.False(await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_EmailMatch_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], ["alice@x.com"], []);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("alice", "alice@x.com"), CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_RoleMatch_CaseInsensitive_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], [], ["Finance-Team"]);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("bob", roles: ["finance-team"]), CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_SsoGroupMatch_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], [], ["FinanceDept"]);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("bob", groups: ["FinanceDept"]), CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_ApiKeyGroupGrant_Allowed()
    {
        var group = await _service.CreateAsync(TenantId, new AgentGroupDto("Finance", null, ["agent-a"], ["alice"], []), CancellationToken.None);
        var tenant = User("svc-key", groupAccess: [group.Id]);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", tenant, CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_AdminBypass_Allowed()
    {
        await SeedGroupAsync("Finance", ["agent-a"], ["alice"], []);
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("admin-user", roles: ["admin"]), CancellationToken.None));
    }

    [Fact]
    public async Task CanInvoke_NoGrant_Denied()
    {
        await SeedGroupAsync("Finance", ["agent-a"], ["alice"], ["finance"]);
        Assert.False(await _service.CanInvokeAgentAsync("agent-a", User("bob", roles: ["sales"]), CancellationToken.None));
    }

    // ── Denied set ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDeniedAgentIds_ReturnsRestrictedAgentsWithoutGrant()
    {
        await SeedGroupAsync("Finance", ["agent-a", "agent-b"], ["alice"], []);
        await SeedGroupAsync("Open", ["agent-c"], [], []);

        var denied = await _service.GetDeniedAgentIdsAsync(User("bob"), CancellationToken.None);

        Assert.Contains("agent-a", denied);
        Assert.Contains("agent-b", denied);
        Assert.DoesNotContain("agent-c", denied);   // unrestricted
    }

    // ── Cache invalidation ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_InvalidatesCache()
    {
        var group = await _service.CreateAsync(TenantId, new AgentGroupDto("Finance", null, ["agent-a"], ["alice"], []), CancellationToken.None);

        // Prime the cache: bob is denied.
        Assert.False(await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None));

        // Grant bob access; update must invalidate the cached map.
        await _service.UpdateAsync(TenantId, group.Id, new AgentGroupDto("Finance", null, ["agent-a"], ["alice", "bob"], []), CancellationToken.None);

        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_RemovesRestriction()
    {
        var group = await _service.CreateAsync(TenantId, new AgentGroupDto("Finance", null, ["agent-a"], ["alice"], []), CancellationToken.None);
        Assert.False(await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None));

        await _service.DeleteAsync(TenantId, group.Id, CancellationToken.None);

        Assert.True(await _service.CanInvokeAgentAsync("agent-a", User("bob"), CancellationToken.None));
    }

    // ── Tenant isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task CanInvoke_OtherTenantGroup_DoesNotRestrict()
    {
        // Group created for tenant 1; tenant 2 user should be unaffected.
        await SeedGroupAsync("Finance", ["agent-a"], ["alice"], []);
        var otherTenant = new TenantContext { TenantId = 2, UserId = "bob" };
        Assert.True(await _service.CanInvokeAgentAsync("agent-a", otherTenant, CancellationToken.None));
    }
}
