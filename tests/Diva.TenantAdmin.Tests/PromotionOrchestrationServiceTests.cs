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

        var result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);

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
        var result = await _orchestrator.PromoteAsync(TenantId, "ScheduledTask", task.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);

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

        var first = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);
        var second = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);

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

        var v1Result = await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);
        var v1Id = v1Result.PromotedObjects[0].VersionId!.Value;

        using (var db = new DivaDbContext(_options))
        {
            var src = await db.TenantMcpServers.SingleAsync(s => s.Id == server.Id);
            src.Description = "v2 description";
            await db.SaveChangesAsync();
        }
        await _orchestrator.PromoteAsync(TenantId, "McpServer", server.LogicalId!.Value, devEnvId, qaEnvId, "alice", CancellationToken.None);

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
}
