using Diva.Infrastructure.Data;
using Diva.Infrastructure.Promotion;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Integration tests for <see cref="PromotionLedgerService"/> — the append-only version history
/// (content-hash dedup, live-version pointer per environment, diffing). Uses real SQLite
/// (in-memory) per ADR-010 — no mocked DbContext.
/// </summary>
public class PromotionLedgerServiceTests : IDisposable
{
    private const int TenantId = 1;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _options;
    private readonly PromotionLedgerService _ledger;

    public PromotionLedgerServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
        using var seed = new DivaDbContext(_options);
        seed.Database.EnsureCreated();

        _ledger = new PromotionLedgerService(new DirectDbFactory(_options), NullLogger<PromotionLedgerService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> SeedEnvironmentAsync()
    {
        using var db = new DivaDbContext(_options);
        var env = await PromotionTestHelpers.CreateEnvironmentAsync(db, TenantId, "dev", 0, isDefault: true);
        return env.Id;
    }

    [Fact]
    public async Task RecordVersionAsync_FirstCall_CreatesObjectVersion1AndDeployment()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();

        var result = await _ledger.RecordVersionAsync(
            TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        Assert.True(result.WasNew);
        Assert.Equal(1, result.Version.Version);

        using var db = new DivaDbContext(_options);
        var obj = await db.PromotableObjects.SingleAsync(o => o.LogicalId == logicalId);
        Assert.Equal("weather-api", obj.Name);
        Assert.Equal(envId, obj.OriginEnvironmentId);
        var deployment = await db.EnvironmentDeployments.SingleAsync(d => d.LogicalId == logicalId && d.EnvironmentId == envId);
        Assert.Equal(result.Version.Id, deployment.LiveVersionId);
    }

    [Fact]
    public async Task RecordVersionAsync_IdenticalContent_DoesNotCreateNewVersion()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        var second = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        Assert.False(second.WasNew);
        Assert.Equal(1, second.Version.Version);
        using var db = new DivaDbContext(_options);
        var count = await db.PromotableVersions.CountAsync(v => v.LogicalId == logicalId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task RecordVersionAsync_ChangedContent_CreatesVersion2_AndAdvancesLiveVersionPointer()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        var second = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":2}", "manual", null, "alice", null, CancellationToken.None);

        Assert.True(second.WasNew);
        Assert.Equal(2, second.Version.Version);
        using var db = new DivaDbContext(_options);
        var deployment = await db.EnvironmentDeployments.SingleAsync(d => d.LogicalId == logicalId && d.EnvironmentId == envId);
        Assert.Equal(second.Version.Id, deployment.LiveVersionId);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsNewestFirst()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":2}", "manual", null, "alice", null, CancellationToken.None);
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":3}", "manual", null, "alice", null, CancellationToken.None);

        var history = await _ledger.GetHistoryAsync(TenantId, logicalId, CancellationToken.None);

        Assert.Equal(3, history.Count);
        Assert.Equal(3, history[0].Version);
        Assert.Equal(2, history[1].Version);
        Assert.Equal(1, history[2].Version);
    }

    [Fact]
    public async Task DiffVersionsAsync_ReturnsChangedFields()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        var v1 = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"description\":\"v1\"}", "manual", null, "alice", null, CancellationToken.None);
        var v2 = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"description\":\"v2\"}", "manual", null, "alice", null, CancellationToken.None);

        var diffs = await _ledger.DiffVersionsAsync(TenantId, v1.Version.Id, v2.Version.Id, CancellationToken.None);

        Assert.Contains(diffs, d => d.FieldPath.Contains("description") && d.OldValue == "v1" && d.NewValue == "v2");
    }

    [Fact]
    public async Task RecordVersionAsync_RenamedObject_UpdatesNameOnPromotableObject()
    {
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "old-name", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "new-name", envId, "{\"v\":2}", "manual", null, "alice", null, CancellationToken.None);

        using var db = new DivaDbContext(_options);
        var obj = await db.PromotableObjects.SingleAsync(o => o.LogicalId == logicalId);
        Assert.Equal("new-name", obj.Name);
    }
}
