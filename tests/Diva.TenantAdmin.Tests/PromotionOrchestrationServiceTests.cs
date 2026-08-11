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
/// Integration tests for <see cref="PromotionOrchestrationService"/> — rank/lineage guards,
/// dependency-closure preview, promote (with ledger + run recording), idempotent re-promote skip,
/// and rollback. Wires up the real snapshot serializers and dependency resolvers (matching
/// production DI) against real SQLite (in-memory) per ADR-010 — no mocked DbContext.
/// </summary>
public class PromotionOrchestrationServiceTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly PromotionLedgerService _ledger;
    private readonly PromotionOrchestrationService _orchestrator;

    public PromotionOrchestrationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        var factory = new DirectDbFactory(_options);
        _ledger = new PromotionLedgerService(factory, NullLogger<PromotionLedgerService>.Instance);
        var exportService = new AgentExportService(factory, NullLogger<AgentExportService>.Instance);

        IEnumerable<IPromotableSnapshotSerializer> serializers =
        [
            new AgentSnapshotSerializer(factory, exportService, NullLogger<AgentSnapshotSerializer>.Instance),
            new McpServerSnapshotSerializer(factory),
            new ScheduledTaskSnapshotSerializer(factory, NullLogger<ScheduledTaskSnapshotSerializer>.Instance),
            new AgentGroupSnapshotSerializer(factory, NullLogger<AgentGroupSnapshotSerializer>.Instance),
        ];
        IEnumerable<IPromotionDependencyResolver> resolvers =
        [
            new AgentPromotionDependencyResolver(factory),
            new McpServerPromotionDependencyResolver(factory),
            new ScheduledTaskPromotionDependencyResolver(factory),
            new AgentGroupPromotionDependencyResolver(factory),
        ];

        _orchestrator = new PromotionOrchestrationService(factory, _ledger, serializers, resolvers, NullLogger<PromotionOrchestrationService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false, string? clientGroup = null)
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, slug, rank, isDefault, clientGroup);
        return env.Id;
    }

    private async Task<TenantMcpServerEntity> SeedMcpServerAsync(int environmentId, string name = "weather-api")
    {
        using var db = new DivaDbContext(_options);
        var server = new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = name,
            Transport = "http",
            Endpoint = "https://weather.example.com/mcp",
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
        };
        db.TenantMcpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private async Task<AgentDefinitionEntity> SeedAgentAsync(int environmentId, string name = "my-agent", int? llmConfigId = null)
    {
        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity
        {
            TenantId = TenantId,
            Name = name,
            SystemPrompt = "You are helpful.",
            LogicalId = Guid.NewGuid(),
            EnvironmentId = environmentId,
            LlmConfigId = llmConfigId,
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    [Fact]
    public async Task PreviewAsync_TargetRankNotHigher_Blocked()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(qaEnvId); // lives in qa (rank 1)

        // Attempt to promote backwards: qa (rank 1) -> dev (rank 0).
        var preview = await _orchestrator.PreviewAsync(TenantId, "McpServer", server.LogicalId!.Value, qaEnvId, devEnvId, CancellationToken.None);

        Assert.False(preview.CanPromote);
        Assert.Contains("strictly higher", preview.BlockingError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_DifferentClientGroups_Blocked()
    {
        var sharedEnvId = await SeedEnvironmentAsync("qa", 0, isDefault: true);
        var acmeEnvId = await SeedEnvironmentAsync("acme-play", 1, clientGroup: "Acme");
        var globexEnvId = await SeedEnvironmentAsync("globex-live", 2, clientGroup: "Globex");
        _ = sharedEnvId;
        var server = await SeedMcpServerAsync(acmeEnvId);

        // Rank passes (2 > 1) but the two environments belong to different clients.
        var preview = await _orchestrator.PreviewAsync(TenantId, "McpServer", server.LogicalId!.Value, acmeEnvId, globexEnvId, CancellationToken.None);

        Assert.False(preview.CanPromote);
        Assert.Contains("different clients", preview.BlockingError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_NullClientGroupSource_CanFanOutToAnyClient()
    {
        var qaEnvId = await SeedEnvironmentAsync("qa", 0, isDefault: true); // shared tier, ClientGroup = null
        var acmeEnvId = await SeedEnvironmentAsync("acme-play", 1, clientGroup: "Acme");
        var server = await SeedMcpServerAsync(qaEnvId);

        var preview = await _orchestrator.PreviewAsync(TenantId, "McpServer", server.LogicalId!.Value, qaEnvId, acmeEnvId, CancellationToken.None);

        Assert.True(preview.CanPromote);
    }

    [Fact]
    public async Task PreviewAsync_Agent_IncludesCascadedMcpServerDependency()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(devEnvId, name: "weather-api");

        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity
        {
            TenantId = TenantId,
            Name = "weather-agent",
            LogicalId = Guid.NewGuid(),
            EnvironmentId = devEnvId,
            McpServerRefsJson = JsonSerializer.Serialize(new[] { "weather-api" }),
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();

        var preview = await _orchestrator.PreviewAsync(TenantId, "Agent", agent.LogicalId!.Value, devEnvId, qaEnvId, CancellationToken.None);

        Assert.True(preview.CanPromote);
        Assert.Contains(preview.WillPromote, d => d.ObjectType == "Agent" && d.LogicalId == agent.LogicalId);
        Assert.Contains(preview.WillPromote, d => d.ObjectType == "McpServer" && d.LogicalId == server.LogicalId);
    }

    [Fact]
    public async Task PromoteAsync_McpServer_CreatesTargetRowAndRecordsLedgerAndRun()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(devEnvId);

        var result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.RunId);
        Assert.Single(result.PromotedObjects);
        Assert.False(result.PromotedObjects[0].WasSkipped);

        using var db = new DivaDbContext(_options);
        var run = await db.PromotionRuns.SingleAsync(r => r.Id == result.RunId);
        Assert.Equal(devEnvId, run.FromEnvironmentId);
        Assert.Equal(qaEnvId, run.ToEnvironmentId);
        var history = await _ledger.GetHistoryAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal("promotion", history[0].Source);
    }

    [Fact]
    public async Task PromoteAsync_ScheduledTask_ForwardDependencyMissing_Blocked()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);

        using var db = new DivaDbContext(_options);
        var agent = new AgentDefinitionEntity { TenantId = TenantId, Name = "report-agent", LogicalId = Guid.NewGuid(), EnvironmentId = devEnvId };
        db.AgentDefinitions.Add(agent);
        var task = new ScheduledTaskEntity
        {
            TenantId = TenantId,
            AgentId = agent.Id,
            Name = "daily-report",
            PromptText = "Report.",
            LogicalId = Guid.NewGuid(),
            EnvironmentId = devEnvId,
        };
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync();

        // The agent has never been promoted to qa — no live EnvironmentDeployments row for it there.
        var result = await _orchestrator.PromoteAsync(TenantId, "ScheduledTask", task.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Agent", result.Error);
        Assert.Contains("promote it first", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromoteAsync_SecondPromoteWithNoChanges_IsSkipped()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(devEnvId);

        var first = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);
        var second = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);

        Assert.False(first.PromotedObjects[0].WasSkipped);
        Assert.True(second.PromotedObjects[0].WasSkipped);

        using var db = new DivaDbContext(_options);
        var versionCount = await db.PromotableVersions.CountAsync(v => v.LogicalId == server.LogicalId);
        Assert.Equal(1, versionCount); // no duplicate version recorded on the no-op re-promote
    }

    [Fact]
    public async Task RollbackAsync_RestoresOlderVersionContent()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(devEnvId);

        var v1Result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);
        var v1Id = v1Result.PromotedObjects[0].VersionId!.Value;

        using (var db = new DivaDbContext(_options))
        {
            var src = await db.TenantMcpServers.SingleAsync(s => s.Id == server.Id);
            src.Description = "v2 description";
            await db.SaveChangesAsync();
        }
        await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);

        using (var db = new DivaDbContext(_options))
        {
            var target = await db.TenantMcpServers.SingleAsync(s => s.EnvironmentId == qaEnvId);
            Assert.Equal("v2 description", target.Description);
        }

        var rollback = await _orchestrator.RollbackAsync(TenantId, "McpServer", server.LogicalId!.Value, qaEnvId, v1Id, "alice", CancellationToken.None);

        Assert.True(rollback.Success);
        using var verify = new DivaDbContext(_options);
        var rolledBack = await verify.TenantMcpServers.SingleAsync(s => s.EnvironmentId == qaEnvId);
        Assert.Null(rolledBack.Description); // v1 had no description set
        var history = await _ledger.GetHistoryAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        Assert.Equal(3, history.Count); // v1 (promotion), v2 (promotion), v3 (rollback)
        Assert.Equal("rollback", history[0].Source);
    }

    [Fact]
    public async Task PromoteAsync_FromNonDefaultEnvironment_ReadsThatEnvironmentsOwnContent_NotAnUnrelatedRow()
    {
        // Regression test: SerializeAsync used to ignore which environment a promotion was FROM
        // and just grab whichever physical row happened to match the LogicalId first (no ORDER
        // BY — in practice the oldest/lowest-id row). Proves "promote staging->prod" now reads
        // staging's own content, even after dev (the oldest row) has since diverged further.
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var stagingEnvId = await SeedEnvironmentAsync("staging", 1);
        var prodEnvId = await SeedEnvironmentAsync("prod", 2);
        var server = await SeedMcpServerAsync(devEnvId); // lowest Id — created first, in dev

        await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, stagingEnvId, "alice", null, null, CancellationToken.None);

        // Dev keeps evolving after staging's promotion — its row now holds different content.
        using (var db = new DivaDbContext(_options))
        {
            var dev = await db.TenantMcpServers.SingleAsync(s => s.Id == server.Id);
            dev.Endpoint = "https://dev-changed.example.com/mcp";
            await db.SaveChangesAsync();
        }

        var result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, stagingEnvId, prodEnvId, "alice", null, null, CancellationToken.None);
        Assert.True(result.Success);

        using var verify = new DivaDbContext(_options);
        var prod = await verify.TenantMcpServers.SingleAsync(s => s.EnvironmentId == prodEnvId);
        Assert.Equal("https://weather.example.com/mcp", prod.Endpoint); // staging's original endpoint, not dev's later change
    }

    [Fact]
    public async Task PromoteAsync_ChangeNote_IsRecordedOnTheLedgerVersion()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var server = await SeedMcpServerAsync(devEnvId);

        var result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", "Fixed the endpoint URL", null, CancellationToken.None);

        Assert.True(result.Success);
        var history = await _ledger.GetHistoryAsync(TenantId, server.LogicalId!.Value, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal("Fixed the endpoint URL", history[0].ChangeNote);
    }

    [Fact]
    public async Task PromoteAsync_TargetLlmConfigId_OverridesThePromotedAgentsConfigInTargetEnvironment()
    {
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(devEnvId, llmConfigId: 1); // source's own config — should NOT carry over

        var result = await _orchestrator.PromoteAsync(TenantId, "Agent", agent.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, 99, CancellationToken.None);

        Assert.True(result.Success);
        using var db = new DivaDbContext(_options);
        var target = await db.AgentDefinitions.SingleAsync(a => a.EnvironmentId == qaEnvId);
        Assert.Equal(99, target.LlmConfigId);
    }

    [Fact]
    public async Task PromoteAsync_NoTargetLlmConfigId_KeepsTheTargetsExistingConfigUntouched()
    {
        // LlmConfigId is deliberately excluded from the portable snapshot (Phase G design), so a
        // re-promotion with no explicit override must never clobber whatever the target
        // environment's own agent already has configured (e.g. set manually via the model-config
        // endpoint after a prior promotion).
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var qaEnvId = await SeedEnvironmentAsync("qa", 1);
        var agent = await SeedAgentAsync(devEnvId);

        await _orchestrator.PromoteAsync(TenantId, "Agent", agent.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);
        using (var db = new DivaDbContext(_options))
        {
            var target = await db.AgentDefinitions.SingleAsync(a => a.EnvironmentId == qaEnvId);
            target.LlmConfigId = 7; // simulates an admin manually picking qa's own config afterward
            target.SystemPrompt = "Old prompt, about to be re-promoted over.";
            await db.SaveChangesAsync();
        }

        using (var db = new DivaDbContext(_options))
        {
            var dev = await db.AgentDefinitions.SingleAsync(a => a.Id == agent.Id);
            dev.SystemPrompt = "New prompt from dev.";
            await db.SaveChangesAsync();
        }

        var result = await _orchestrator.PromoteAsync(TenantId, "Agent", agent.LogicalId!.Value, devEnvId, qaEnvId, "alice", null, null, CancellationToken.None);

        Assert.True(result.Success);
        using var verify = new DivaDbContext(_options);
        var final = await verify.AgentDefinitions.SingleAsync(a => a.EnvironmentId == qaEnvId);
        Assert.Equal("New prompt from dev.", final.SystemPrompt); // content re-promoted
        Assert.Equal(7, final.LlmConfigId); // but qa's own LLM config choice survives untouched
    }
}
