using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.AgentExport;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Promotion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Shared seeding helper for the Promotion subsystem test files (this file, PromotionLedgerServiceTests,
/// PromotionOrchestrationServiceTests). The 4 promotable entity types have a real FK from EnvironmentId
/// to TenantEnvironmentEntity.Id (see DivaDbContext.OnModelCreating), so every test must seed a real
/// environment row rather than using an arbitrary int, or SaveChangesAsync throws a FK violation.
/// </summary>
internal static class PromotionTestHelpers
{
    public static async Task<TenantEnvironmentEntity> CreateEnvironmentAsync(
        DivaDbContext db, int tenantId, string slug, int rank, bool isDefault = false, string? clientGroup = null)
    {
        var env = new TenantEnvironmentEntity
        {
            TenantId = tenantId,
            Slug = slug,
            DisplayName = slug,
            Rank = rank,
            IsDefault = isDefault,
            ClientGroup = clientGroup,
        };
        db.TenantEnvironments.Add(env);
        await db.SaveChangesAsync();
        return env;
    }
}

/// <summary>
/// Integration tests for <see cref="AgentGroupSnapshotSerializer"/>. Uses real SQLite (in-memory)
/// per ADR-010 — no mocked DbContext. Reuses the project-wide <see cref="DirectDbFactory"/> helper
/// (defined in TenantBusinessRulesServiceTests.cs).
/// </summary>
public class AgentGroupSnapshotSerializerTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly AgentGroupSnapshotSerializer _serializer;

    public AgentGroupSnapshotSerializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        _serializer = new AgentGroupSnapshotSerializer(new DirectDbFactory(_options), NullLogger<AgentGroupSnapshotSerializer>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false)
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, slug, rank, isDefault);
        return env.Id;
    }

    private async Task<(AgentGroupEntity Group, AgentDefinitionEntity Agent)> SeedGroupWithMemberAgentAsync(
        int environmentId, bool withAllowedUserIds = false, bool withUserGroupLink = false, string agentName = "agent-a", string groupName = "finance")
    {
        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity { TenantId = TenantId, Name = agentName, LogicalId = Guid.NewGuid(), EnvironmentId = environmentId };
        db.AgentDefinitions.Add(agent);
        var group = new AgentGroupEntity
        {
            TenantId = TenantId,
            Name = groupName,
            Description = "Finance team access",
            AllowedRolesJson = JsonSerializer.Serialize(new[] { "finance-team" }),
            AllowedUserIdsJson = withAllowedUserIds ? JsonSerializer.Serialize(new[] { "alice", "bob" }) : null,
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
        };
        db.AgentGroups.Add(group);
        await db.SaveChangesAsync(); // flush so agent.Id/group.Id are populated

        group.AgentIdsJson = JsonSerializer.Serialize(new[] { agent.Id });
        await db.SaveChangesAsync();

        if (withUserGroupLink)
        {
            var userGroup = new UserGroupEntity { TenantId = TenantId, Name = $"{groupName}-ug" };
            db.UserGroups.Add(userGroup);
            await db.SaveChangesAsync();
            db.AgentGroupUserGroups.Add(new AgentGroupUserGroupEntity { TenantId = TenantId, AgentGroupId = group.Id, UserGroupId = userGroup.Id });
            await db.SaveChangesAsync();
        }

        return (group, agent);
    }

    [Fact]
    public async Task SerializeAsync_NoMatchingRow_ReturnsNull()
    {
        await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var result = await _serializer.SerializeAsync(TenantId, Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SerializeAsync_ExcludesAllowedUserIds_ButIncludesRolesAndMemberNames()
    {
        var envId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var (group, agent) = await SeedGroupWithMemberAgentAsync(envId, withAllowedUserIds: true);

        var snapshot = await _serializer.SerializeAsync(TenantId, group.LogicalId!.Value, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("finance", snapshot!.Name);
        Assert.Contains(agent.Name, snapshot.SnapshotJson);
        Assert.Contains("finance-team", snapshot.SnapshotJson);
        Assert.DoesNotContain("alice", snapshot.SnapshotJson);
        Assert.DoesNotContain("bob", snapshot.SnapshotJson);
    }

    [Fact]
    public async Task MaterializeAsync_NoExistingRow_CreatesNewRowTaggedWithLogicalIdAndEnvironment()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var (group, _) = await SeedGroupWithMemberAgentAsync(sourceEnvId);

        var snapshot = await _serializer.SerializeAsync(TenantId, group.LogicalId!.Value, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, group.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        // The SOURCE row must still exist in dev — promotion creates an independent copy, it does
        // not relocate the object out of its source environment (the bug this fix addresses).
        var source = await db.AgentGroups.SingleAsync(g => g.EnvironmentId == sourceEnvId);
        Assert.Equal(group.Id, source.Id);

        var target = await db.AgentGroups.SingleAsync(g => g.EnvironmentId == targetEnvId);
        Assert.Equal("finance", target.Name);
        Assert.Equal(group.LogicalId, target.LogicalId);
        Assert.NotEqual(group.Id, target.Id); // distinct physical row from the source
    }

    [Fact]
    public async Task MaterializeAsync_SameNameDifferentLogicalId_CreatesIndependentRow_DoesNotRepurposeExisting()
    {
        // Confirms the fix: MaterializeAsync now matches by (TenantId, EnvironmentId, LogicalId),
        // not by Name alone — so a different logical object can share the same Name (a realistic
        // scenario once promotion legitimately produces same-named copies across environments)
        // without silently repurposing an unrelated existing row.
        var envId = await SeedEnvironmentAsync("qa", 0, isDefault: true);
        var (existing, _) = await SeedGroupWithMemberAgentAsync(envId, groupName: "shared-name");
        var incomingLogicalId = Guid.NewGuid(); // deliberately different from existing.LogicalId
        var incomingSnapshotJson = JsonSerializer.Serialize(new
        {
            name = "shared-name",
            description = "From a different logical object",
            allowedRolesJson = (string?)null,
            agentNames = Array.Empty<string>(),
        });

        await _serializer.MaterializeAsync(TenantId, envId, incomingLogicalId, incomingSnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var rows = await db.AgentGroups.Where(g => g.Name == "shared-name").ToListAsync();
        Assert.Equal(2, rows.Count); // independent row created, existing one left untouched
        Assert.Contains(rows, r => r.Id == existing.Id && r.LogicalId == existing.LogicalId);
        Assert.Contains(rows, r => r.LogicalId == incomingLogicalId && r.Id != existing.Id);
    }

    [Fact]
    public async Task MaterializeAsync_CalledTwiceForSameTarget_UpdatesInPlace_DoesNotDuplicate()
    {
        var envId = await SeedEnvironmentAsync("qa", 0, isDefault: true);
        var logicalId = Guid.NewGuid();
        var snapshot1 = JsonSerializer.Serialize(new { name = "finance", description = "v1", allowedRolesJson = (string?)null, agentNames = Array.Empty<string>() });
        var snapshot2 = JsonSerializer.Serialize(new { name = "finance", description = "v2", allowedRolesJson = (string?)null, agentNames = Array.Empty<string>() });

        await _serializer.MaterializeAsync(TenantId, envId, logicalId, snapshot1, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, envId, logicalId, snapshot2, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var rows = await db.AgentGroups.Where(g => g.Name == "finance").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("v2", rows[0].Description);
    }

    [Fact]
    public async Task MaterializeAsync_ExcludesAllowedUserIdsAndUserGroupLinks_EvenWhenSourceHasThem()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var (group, _) = await SeedGroupWithMemberAgentAsync(sourceEnvId, withAllowedUserIds: true, withUserGroupLink: true);

        var snapshot = await _serializer.SerializeAsync(TenantId, group.LogicalId!.Value, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, group.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var target = await db.AgentGroups.Include(g => g.UserGroupLinks).SingleAsync(g => g.EnvironmentId == targetEnvId);
        Assert.Null(target.AllowedUserIdsJson);
        Assert.Empty(target.UserGroupLinks);
        Assert.Contains("finance-team", target.AllowedRolesJson); // role-based access DOES carry over
    }
}

/// <summary>Integration tests for <see cref="McpServerSnapshotSerializer"/>.</summary>
public class McpServerSnapshotSerializerTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly McpServerSnapshotSerializer _serializer;

    public McpServerSnapshotSerializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        _serializer = new McpServerSnapshotSerializer(new DirectDbFactory(_options));
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false)
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, slug, rank, isDefault);
        return env.Id;
    }

    private async Task<TenantMcpServerEntity> SeedServerAsync(int environmentId, string name = "weather-api", string? apiKeyMappings = null)
    {
        using var db = new DivaDbContext(_options);
        var server = new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = name,
            Description = "Weather lookup",
            Transport = "http",
            Endpoint = "https://weather.example.com/mcp",
            PassSsoToken = true,
            DefaultCredentialRef = "weather-key",
            ApiKeyCredentialMappingsJson = apiKeyMappings,
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
        };
        db.TenantMcpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    [Fact]
    public async Task SerializeAsync_NoMatchingRow_ReturnsNull()
    {
        await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var result = await _serializer.SerializeAsync(TenantId, Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SerializeThenMaterialize_RoundTrip_CreatesNewRow_PreservesPortableFields()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedServerAsync(sourceEnvId);

        var snapshot = await _serializer.SerializeAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, server.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var target = await db.TenantMcpServers.SingleAsync(s => s.EnvironmentId == targetEnvId);
        Assert.Equal("weather-api", target.Name);
        Assert.Equal("http", target.Transport);
        Assert.Equal("https://weather.example.com/mcp", target.Endpoint);
        Assert.True(target.PassSsoToken);
        Assert.Equal("weather-key", target.DefaultCredentialRef);
        Assert.Equal(server.LogicalId, target.LogicalId);
    }

    [Fact]
    public async Task MaterializeAsync_ExcludesApiKeyCredentialMappingsJson_EvenWhenSourceHasIt()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var mappings = JsonSerializer.Serialize(new[] { new { apiKeyId = 12, credentialRef = "acme-key" } });
        var server = await SeedServerAsync(sourceEnvId, apiKeyMappings: mappings);

        var snapshot = await _serializer.SerializeAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        Assert.DoesNotContain("apiKeyId", snapshot!.SnapshotJson);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, server.LogicalId!.Value, snapshot.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var target = await db.TenantMcpServers.SingleAsync(s => s.EnvironmentId == targetEnvId);
        Assert.Null(target.ApiKeyCredentialMappingsJson);
    }

    [Fact]
    public async Task MaterializeAsync_PromotingToNewEnvironment_CreatesIndependentCopy_PreservesSource()
    {
        // Confirms the fix: TenantMcpServerEntity's unique index now includes EnvironmentId, and
        // MaterializeAsync matches by (TenantId, EnvironmentId, LogicalId) — so "promoting" a
        // server to a new environment creates an INDEPENDENT copy there while the source
        // environment keeps its own row (previously it silently relocated instead of copying).
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedServerAsync(sourceEnvId);

        var snapshot = await _serializer.SerializeAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, server.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var allRows = await db.TenantMcpServers.Where(s => s.Name == "weather-api").ToListAsync();
        Assert.Equal(2, allRows.Count); // independent copy created, source preserved
        Assert.Contains(allRows, s => s.Id == server.Id && s.EnvironmentId == sourceEnvId);
        Assert.Contains(allRows, s => s.Id != server.Id && s.EnvironmentId == targetEnvId && s.LogicalId == server.LogicalId);
    }
}

/// <summary>Integration tests for <see cref="ScheduledTaskSnapshotSerializer"/>.</summary>
public class ScheduledTaskSnapshotSerializerTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly ScheduledTaskSnapshotSerializer _serializer;

    public ScheduledTaskSnapshotSerializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        _serializer = new ScheduledTaskSnapshotSerializer(new DirectDbFactory(_options), NullLogger<ScheduledTaskSnapshotSerializer>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false)
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, slug, rank, isDefault);
        return env.Id;
    }

    private async Task<AgentDefinitionEntity> SeedAgentAsync(int environmentId, string name = "weather-agent")
    {
        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity { TenantId = TenantId, Name = name, LogicalId = Guid.NewGuid(), EnvironmentId = environmentId };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private async Task<ScheduledTaskEntity> SeedTaskAsync(int environmentId, string agentId, string name = "daily-report", string? runAsUserId = "alice")
    {
        using var db = new DivaDbContext(_options);
        var task = new ScheduledTaskEntity
        {
            TenantId = TenantId,
            AgentId = agentId,
            Name = name,
            ScheduleType = "daily",
            RunAtTime = "09:00",
            TimeZoneId = "UTC",
            PayloadType = "prompt",
            PromptText = "Generate the daily report.",
            IsEnabled = true,
            RunAsUserId = runAsUserId,
            RunAsUserEmail = runAsUserId is null ? null : $"{runAsUserId}@example.com",
            RunAsUserLabel = runAsUserId,
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
        };
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task SerializeAsync_ResolvesAgentIdToAgentName()
    {
        var envId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var agent = await SeedAgentAsync(envId);
        var task = await SeedTaskAsync(envId, agent.Id);

        var snapshot = await _serializer.SerializeAsync(TenantId, task.LogicalId!.Value, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Contains(agent.Name, snapshot!.SnapshotJson);
        Assert.DoesNotContain(agent.Id, snapshot.SnapshotJson); // raw agent Id is not portable
    }

    [Fact]
    public async Task MaterializeAsync_ReResolvesAgentNameToAgentId_InTargetTenant()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(sourceEnvId);
        var task = await SeedTaskAsync(sourceEnvId, agent.Id);

        var snapshot = await _serializer.SerializeAsync(TenantId, task.LogicalId!.Value, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, task.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var target = await db.ScheduledTasks.SingleAsync(t => t.EnvironmentId == targetEnvId);
        Assert.Equal(agent.Id, target.AgentId); // only one agent with that name exists tenant-wide
        Assert.Equal(task.LogicalId, target.LogicalId);
    }

    [Fact]
    public async Task MaterializeAsync_ExcludesRunAsUserFields_EvenWhenSourceHasThem()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(sourceEnvId);
        var task = await SeedTaskAsync(sourceEnvId, agent.Id, runAsUserId: "alice");

        var snapshot = await _serializer.SerializeAsync(TenantId, task.LogicalId!.Value, CancellationToken.None);
        Assert.DoesNotContain("alice", snapshot!.SnapshotJson);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, task.LogicalId!.Value, snapshot.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var target = await db.ScheduledTasks.SingleAsync(t => t.EnvironmentId == targetEnvId);
        Assert.Null(target.RunAsUserId);
        Assert.Null(target.RunAsUserEmail);
        Assert.Null(target.RunAsUserLabel);
        // Source keeps its own RunAsUser values — promotion creates an independent copy.
        var source = await db.ScheduledTasks.SingleAsync(t => t.EnvironmentId == sourceEnvId);
        Assert.Equal("alice", source.RunAsUserId);
    }

    [Fact]
    public async Task MaterializeAsync_AgentNotFoundInTenant_CreatesTaskWithEmptyAgentId_DoesNotThrow()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(sourceEnvId, name: "temp-agent");
        var task = await SeedTaskAsync(sourceEnvId, agent.Id);
        var snapshot = await _serializer.SerializeAsync(TenantId, task.LogicalId!.Value, CancellationToken.None);

        // Remove the agent entirely before materializing — simulates promoting a task whose agent
        // was never promoted first (the orchestrator blocks this in practice via its forward-
        // dependency check, but the serializer itself must not crash if called directly).
        using (var db = new DivaDbContext(_options))
        {
            db.AgentDefinitions.Remove(await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id));
            await db.SaveChangesAsync();
        }

        await _serializer.MaterializeAsync(TenantId, targetEnvId, task.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var verify = new DivaDbContext(_options);
        var target = await verify.ScheduledTasks.SingleAsync(t => t.EnvironmentId == targetEnvId);
        Assert.Equal(string.Empty, target.AgentId); // new row defaults to "" when agent can't be resolved
    }

    [Fact]
    public async Task MaterializeAsync_CalledTwiceForSameTarget_UpdatesInPlace_DoesNotDuplicate()
    {
        var envId = await SeedEnvironmentAsync("qa", 0, isDefault: true);
        var agent = await SeedAgentAsync(envId);
        var logicalId = Guid.NewGuid();
        var snapshot1 = JsonSerializer.Serialize(new
        {
            name = "daily-report",
            description = (string?)null,
            agentName = agent.Name,
            scheduleType = "daily",
            scheduledAtUtc = (DateTime?)null,
            runAtTime = "09:00",
            dayOfWeek = (int?)null,
            timeZoneId = "UTC",
            payloadType = "prompt",
            promptText = "v1",
            parametersJson = (string?)null,
            isEnabled = true,
            notifyEmails = (string?)null,
            notifyOn = (string?)null,
            successKeywords = (string?)null,
        });
        var snapshot2 = JsonSerializer.Serialize(new
        {
            name = "daily-report",
            description = (string?)null,
            agentName = agent.Name,
            scheduleType = "daily",
            scheduledAtUtc = (DateTime?)null,
            runAtTime = "09:00",
            dayOfWeek = (int?)null,
            timeZoneId = "UTC",
            payloadType = "prompt",
            promptText = "v2",
            parametersJson = (string?)null,
            isEnabled = true,
            notifyEmails = (string?)null,
            notifyOn = (string?)null,
            successKeywords = (string?)null,
        });

        await _serializer.MaterializeAsync(TenantId, envId, logicalId, snapshot1, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, envId, logicalId, snapshot2, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var rows = await db.ScheduledTasks.Where(t => t.Name == "daily-report").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("v2", rows[0].PromptText);
    }
}

/// <summary>
/// Integration tests for <see cref="AgentSnapshotSerializer"/>, which wraps the real
/// <see cref="AgentExportService"/> rather than reimplementing bundle serialization.
/// </summary>
public class AgentSnapshotSerializerTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly AgentSnapshotSerializer _serializer;

    public AgentSnapshotSerializerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        var factory = new DirectDbFactory(_options);
        var exportService = new AgentExportService(factory, NullLogger<AgentExportService>.Instance);
        _serializer = new AgentSnapshotSerializer(factory, exportService, NullLogger<AgentSnapshotSerializer>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false)
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, slug, rank, isDefault);
        return env.Id;
    }

    private async Task<AgentDefinitionEntity> SeedAgentAsync(int environmentId, string name = "my-agent")
    {
        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity
        {
            TenantId = TenantId,
            Name = name,
            DisplayName = "My Agent",
            Description = "Test agent",
            AgentType = "generic",
            SystemPrompt = "You are helpful.",
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    [Fact]
    public async Task SerializeAsync_NoMatchingRow_ReturnsNull()
    {
        await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var result = await _serializer.SerializeAsync(TenantId, Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SerializeThenMaterialize_RoundTrip_TagsLogicalIdAndEnvironmentId_ForNewAgent()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(sourceEnvId);

        var snapshot = await _serializer.SerializeAsync(TenantId, agent.LogicalId!.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, agent.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        // Source keeps its own row — promotion creates an independent copy, not a relocation.
        var source = await db.AgentDefinitions.SingleAsync(a => a.EnvironmentId == sourceEnvId);
        Assert.Equal(agent.Id, source.Id);

        var target = await db.AgentDefinitions.SingleAsync(a => a.EnvironmentId == targetEnvId);
        Assert.Equal("my-agent", target.Name);
        Assert.Equal(agent.LogicalId, target.LogicalId);
        Assert.NotEqual(agent.Id, target.Id); // distinct physical row from the source
    }

    [Fact]
    public async Task MaterializeAsync_ImportsLinkedRules()
    {
        var sourceEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var targetEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(sourceEnvId);
        using (var db = new DivaDbContext(_options))
        {
            db.BusinessRules.Add(new TenantBusinessRuleEntity
            {
                Guid = Guid.NewGuid().ToString(),
                TenantId = TenantId,
                AgentId = agent.Id,
                AgentType = "generic",
                RuleCategory = "Behaviour",
                RuleKey = "tone",
                PromptInjection = "Be concise.",
                HookPoint = "OnInit",
                HookRuleType = "inject_prompt",
            });
            await db.SaveChangesAsync();
        }

        var snapshot = await _serializer.SerializeAsync(TenantId, agent.LogicalId!.Value, CancellationToken.None);
        await _serializer.MaterializeAsync(TenantId, targetEnvId, agent.LogicalId!.Value, snapshot!.SnapshotJson, CancellationToken.None);

        using var verify = new DivaDbContext(_options);
        var target = await verify.AgentDefinitions.SingleAsync(a => a.EnvironmentId == targetEnvId);
        var rules = await verify.BusinessRules.Where(r => r.AgentId == target.Id).ToListAsync();
        Assert.Single(rules);
        Assert.Equal("Be concise.", rules[0].PromptInjection);
    }
}
