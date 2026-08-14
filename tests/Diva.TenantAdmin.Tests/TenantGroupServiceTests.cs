using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Groups;
using Diva.Infrastructure.LiteLLM;
using Diva.TenantAdmin.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Integration tests for TenantGroupService.
/// Uses real SQLite (in-memory) per ADR-010 — no mocked DbContext.
/// </summary>
public class TenantGroupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DivaDbContext _db;
    private readonly TenantGroupService _service;
    private readonly IMemoryCache _cache;
    private readonly GroupMembershipCache _membershipCache;
    private readonly ILlmConfigResolver _llmResolver;

    public TenantGroupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DivaDbContext(opts);
        _db.Database.EnsureCreated();

        _cache = new MemoryCache(new MemoryCacheOptions());
        _llmResolver = Substitute.For<ILlmConfigResolver>();
        var factory = new DirectDbFactory(opts);
        _membershipCache = new GroupMembershipCache(factory, _cache, NullLogger<GroupMembershipCache>.Instance);
        _service = new TenantGroupService(
            factory,
            _membershipCache,
            _llmResolver,
            _cache,
            NullLogger<TenantGroupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _connection.Dispose();
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGroupAsync_PersistsGroup()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("Alpha", "Alpha group"), CancellationToken.None);

        Assert.True(group.Id > 0);
        Assert.Equal("Alpha", group.Name);
        Assert.Equal("Alpha group", group.Description);
        Assert.True(group.IsActive);
    }

    [Fact]
    public async Task GetAllGroupsAsync_ReturnsAllGroups()
    {
        _db.TenantGroups.AddRange(
            new TenantGroupEntity { Name = "G1", IsActive = true, CreatedAt = DateTime.UtcNow },
            new TenantGroupEntity { Name = "G2", IsActive = true, CreatedAt = DateTime.UtcNow }
        );
        await _db.SaveChangesAsync();

        var groups = await _service.GetAllGroupsAsync(CancellationToken.None);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task UpdateGroupAsync_UpdatesNameAndActiveState()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("Old", null), CancellationToken.None);

        var updated = await _service.UpdateGroupAsync(group.Id, new UpdateGroupDto("New", "desc", false), CancellationToken.None);

        Assert.Equal("New", updated.Name);
        Assert.Equal("desc", updated.Description);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task UpdateGroupAsync_UnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.UpdateGroupAsync(999, new UpdateGroupDto("X", null, true), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteGroupAsync_RemovesGroup()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("ToDel", null), CancellationToken.None);

        await _service.DeleteGroupAsync(group.Id, CancellationToken.None);

        var groups = await _service.GetAllGroupsAsync(CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task DeleteGroupAsync_UnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.DeleteGroupAsync(999, CancellationToken.None));
    }

    // ── Members ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddMemberAsync_PersistsMembership()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);

        var member = await _service.AddMemberAsync(group.Id, 42, CancellationToken.None);

        Assert.Equal(42, member.TenantId);
        Assert.Equal(group.Id, member.GroupId);
    }

    [Fact]
    public async Task AddMemberAsync_DuplicateMember_ThrowsInvalidOperation()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 42, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.AddMemberAsync(group.Id, 42, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovesMembership()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 10, CancellationToken.None);

        await _service.RemoveMemberAsync(group.Id, 10, CancellationToken.None);

        var members = await _service.GetMembersAsync(group.Id, CancellationToken.None);
        Assert.Empty(members);
    }

    [Fact]
    public async Task RemoveMemberAsync_NotAMember_ThrowsKeyNotFound()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.RemoveMemberAsync(group.Id, 99, CancellationToken.None));
    }

    // ── GetActiveRulesForTenantAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetActiveRulesForTenantAsync_NoGroupMembership_ReturnsEmpty()
    {
        var rules = await _service.GetActiveRulesForTenantAsync(1, "Analytics", CancellationToken.None);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetActiveRulesForTenantAsync_MatchesSpecificAgentType()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 1, CancellationToken.None);
        await _service.CreateBusinessRuleAsync(group.Id, new CreateGroupRuleDto("Analytics", "cat", "k1", "Rule A"), CancellationToken.None);
        await _service.CreateBusinessRuleAsync(group.Id, new CreateGroupRuleDto("Other", "cat", "k2", "Rule B"), CancellationToken.None);

        var rules = await _service.GetActiveRulesForTenantAsync(1, "Analytics", CancellationToken.None);

        Assert.Single(rules);
        Assert.Equal("k1", rules[0].RuleKey);
    }

    [Fact]
    public async Task GetActiveRulesForTenantAsync_WildcardAgentType_IncludedForAllAgents()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 1, CancellationToken.None);
        await _service.CreateBusinessRuleAsync(group.Id, new CreateGroupRuleDto("*", "cat", "global", "Global rule"), CancellationToken.None);

        var rules = await _service.GetActiveRulesForTenantAsync(1, "Analytics", CancellationToken.None);

        Assert.Single(rules);
        Assert.Equal("global", rules[0].RuleKey);
    }

    [Fact]
    public async Task GetActiveRulesForTenantAsync_InactiveRule_NotReturned()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 1, CancellationToken.None);
        var rule = await _service.CreateBusinessRuleAsync(group.Id, new CreateGroupRuleDto("Analytics", "cat", "k1", "Rule"), CancellationToken.None);
        await _service.UpdateBusinessRuleAsync(group.Id, rule.Id, new UpdateGroupRuleDto("cat", "k1", "Rule", null, false, 50), CancellationToken.None);

        var rules = await _service.GetActiveRulesForTenantAsync(1, "Analytics", CancellationToken.None);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetActiveRulesForTenantAsync_TenantInInactiveGroup_ReturnsEmpty()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, 1, CancellationToken.None);
        await _service.CreateBusinessRuleAsync(group.Id, new CreateGroupRuleDto("Analytics", "cat", "k1", "Rule"), CancellationToken.None);

        // Deactivate the group
        await _service.UpdateGroupAsync(group.Id, new UpdateGroupDto("G", null, false), CancellationToken.None);
        _membershipCache.InvalidateForTenant(1); // force cache refresh

        var rules = await _service.GetActiveRulesForTenantAsync(1, "Analytics", CancellationToken.None);

        Assert.Empty(rules);
    }

    // ── Cache invalidation ────────────────────────────────────────────────────

    [Fact]
    public async Task AddMemberAsync_InvalidatesGroupMembershipCache()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);

        // Prime the cache with empty (tenant 5 has no memberships yet)
        var before = await _membershipCache.GetGroupIdsForTenantAsync(5, CancellationToken.None);
        Assert.Empty(before);

        // AddMemberAsync should evict the cached entry for tenant 5
        await _service.AddMemberAsync(group.Id, 5, CancellationToken.None);

        // Next call re-queries DB and finds the new membership
        var after = await _membershipCache.GetGroupIdsForTenantAsync(5, CancellationToken.None);
        Assert.Contains(group.Id, after);
    }

    [Fact]
    public async Task AddMemberAsync_CallsLlmResolverInvalidate()
    {
        var group = await _service.CreateGroupAsync(new CreateGroupDto("G", null), CancellationToken.None);

        await _service.AddMemberAsync(group.Id, 7, CancellationToken.None);

        _llmResolver.Received(1).InvalidateForTenant(7);
    }

    // ── Available LLM configs picker ─────────────────────────────────────────

    [Fact]
    public async Task ListAvailableLlmConfigsForTenantAsync_ExcludesGroupConfig_WhenIdCollidesWithTenantConfig()
    {
        // TenantLlmConfigs and GroupLlmConfigs are separate tables with independent Id sequences,
        // so seeding one of each can naturally produce the same Id in both — reproducing a real
        // reported bug: the picker showed two entries sharing the same value (both "look selected"
        // whenever that Id is chosen), even though LlmConfigResolver always resolves tenant-scope
        // first for a given Id, so the group entry could never actually be selected anyway.
        const int tenantId = 1;
        var group = await _service.CreateGroupAsync(new CreateGroupDto("Alpha", null), CancellationToken.None);
        await _service.AddMemberAsync(group.Id, tenantId, CancellationToken.None);

        var tenantConfig = new TenantLlmConfigEntity { TenantId = tenantId, Name = "COT", Provider = "Anthropic" };
        _db.TenantLlmConfigs.Add(tenantConfig);
        await _db.SaveChangesAsync();

        var groupConfig = new GroupLlmConfigEntity { GroupId = group.Id, Name = "Claude DEV", Provider = "Anthropic" };
        _db.GroupLlmConfigs.Add(groupConfig);
        await _db.SaveChangesAsync();

        Assert.Equal(tenantConfig.Id, groupConfig.Id); // sanity check: the collision this test targets

        var configs = await _service.ListAvailableLlmConfigsForTenantAsync(tenantId, environmentId: null, CancellationToken.None);

        var matching = configs.Where(c => c.Id == tenantConfig.Id).ToList();
        var match = Assert.Single(matching); // not two — the colliding group entry is excluded
        Assert.Equal("tenant", match.Source);
        Assert.Equal("COT", match.DisplayName);
    }
}
