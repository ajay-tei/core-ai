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
        // Uses source="promotion" deliberately: unlike "manual"/"publish" (which now mutate an
        // unshipped version in place), promotion/rollback always create a distinct, auditable
        // version on content change, regardless of whether anything else depends on the old one.
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "promotion", null, "alice", null, CancellationToken.None);

        var second = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":2}", "promotion", null, "alice", null, CancellationToken.None);

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
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "promotion", null, "alice", null, CancellationToken.None);
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":2}", "promotion", null, "alice", null, CancellationToken.None);
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":3}", "promotion", null, "alice", null, CancellationToken.None);

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
        var v1 = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"description\":\"v1\"}", "promotion", null, "alice", null, CancellationToken.None);
        var v2 = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"description\":\"v2\"}", "promotion", null, "alice", null, CancellationToken.None);

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

    [Fact]
    public async Task RecordVersionAsync_ManualSource_ChangedContent_NotYetShipped_MutatesInPlace()
    {
        // Routine editing (Save Changes/Publish) in the object's own environment, with nothing
        // else depending on the current version yet, should NOT burn a new version number on
        // every edit — it updates the same version's content in place.
        var envId = await SeedEnvironmentAsync();
        var logicalId = Guid.NewGuid();
        var first = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);

        var second = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":2}", "manual", null, "bob", "tweak", CancellationToken.None);
        var third = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", envId, "{\"v\":3}", "publish", null, "bob", "another tweak", CancellationToken.None);

        Assert.True(second.WasNew); // content changed — just not via a new row
        Assert.Equal(1, second.Version.Version);
        Assert.Equal(first.Version.Id, second.Version.Id); // same row, mutated in place
        Assert.Equal(1, third.Version.Version);
        Assert.Equal(first.Version.Id, third.Version.Id);

        using var db = new DivaDbContext(_options);
        Assert.Equal(1, await db.PromotableVersions.CountAsync(v => v.LogicalId == logicalId)); // one row total
        var row = await db.PromotableVersions.SingleAsync(v => v.LogicalId == logicalId);
        Assert.Equal("{\"v\":3}", row.SnapshotJson);
        Assert.Equal("publish", row.Source); // reflects the most recent edit
        Assert.Equal("bob", row.CreatedBy);
        Assert.Equal("another tweak", row.ChangeNote);
    }

    [Fact]
    public async Task RecordVersionAsync_ManualSource_VersionAlreadyLiveInAnotherEnvironment_CreatesNewVersionInstead()
    {
        // Once a version has been shipped to (is live in) another environment, editing the
        // source environment again must NOT mutate it in place — that would silently change
        // what the other environment is serving. A new version is required instead.
        var devEnvId = await SeedEnvironmentAsync();
        int qaEnvId;
        using (var envDb = new DivaDbContext(_options))
        {
            qaEnvId = (await PromotionTestHelpers.CreateEnvironmentAsync(envDb, TenantId, "qa", 1)).Id;
        }
        var logicalId = Guid.NewGuid();
        var v1 = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", devEnvId, "{\"v\":1}", "manual", null, "alice", null, CancellationToken.None);
        // Simulate promotion: the same content also goes live in qa (dedup reuses v1).
        await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", qaEnvId, "{\"v\":1}", "promotion", v1.Version.Id, "alice", null, CancellationToken.None);

        var afterEdit = await _ledger.RecordVersionAsync(TenantId, logicalId, "McpServer", "weather-api", devEnvId, "{\"v\":2}", "manual", null, "alice", null, CancellationToken.None);

        Assert.True(afterEdit.WasNew);
        Assert.Equal(2, afterEdit.Version.Version);
        Assert.NotEqual(v1.Version.Id, afterEdit.Version.Id);

        using var db = new DivaDbContext(_options);
        Assert.Equal(2, await db.PromotableVersions.CountAsync(v => v.LogicalId == logicalId));
        // qa's own deployment still points at the original (unmutated) v1 content.
        var qaDeployment = await db.EnvironmentDeployments.SingleAsync(d => d.LogicalId == logicalId && d.EnvironmentId == qaEnvId);
        Assert.Equal(v1.Version.Id, qaDeployment.LiveVersionId);
        var qaLiveVersion = await db.PromotableVersions.SingleAsync(v => v.Id == qaDeployment.LiveVersionId);
        Assert.Equal("{\"v\":1}", qaLiveVersion.SnapshotJson);
    }
}
