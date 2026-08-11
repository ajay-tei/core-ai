namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Snapshot serializer for agents — thin wrapper around the existing <see cref="IAgentExportService"/>
/// rather than reimplementing bundle/rule serialization. Resolves the specific (TenantId,
/// EnvironmentId, LogicalId) row itself and passes it via <see cref="AgentImportOptions.TargetAgentId"/>,
/// since ImportAsync's own by-Name matching is tenant-wide, not environment-scoped.
/// </summary>
public sealed class AgentSnapshotSerializer : IPromotableSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDatabaseProviderFactory _db;
    private readonly IAgentExportService _export;
    private readonly ILogger<AgentSnapshotSerializer> _logger;

    public string ObjectType => "Agent";

    public AgentSnapshotSerializer(IDatabaseProviderFactory db, IAgentExportService export, ILogger<AgentSnapshotSerializer> logger)
    {
        _db = db;
        _export = export;
        _logger = logger;
    }

    public async Task<SerializedSnapshot?> SerializeAsync(int tenantId, int environmentId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var agent = await db.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.EnvironmentId == environmentId && a.LogicalId == logicalId, ct);
        if (agent is null)
        {
            return null;
        }

        var bundle = await _export.ExportAsync(agent.Id, TenantContext.System(tenantId), ct);

        // Normalize volatile export metadata before hashing/storing — ExportAsync stamps a fresh
        // ExportedAt on every call (fine for the standalone "download as JSON" feature this method
        // is shared with), but the ledger's content-hash dedup (PromotionLedgerService) hashes this
        // exact JSON, so an ever-changing timestamp would mint a brand-new version on every single
        // promotion/publish even when the agent's actual configuration hasn't changed at all.
        var normalized = bundle with { ExportedAt = default, SourceTenantId = 0 };
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        return new SerializedSnapshot { SnapshotJson = json, Name = bundle.Agent.Name };
    }

    public async Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct)
    {
        var bundle = JsonSerializer.Deserialize<AgentExportBundle>(snapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid agent snapshot JSON.");

        // Resolve the specific row (if any) already live for THIS (tenant, environment, logicalId)
        // ourselves, and pass it as TargetAgentId — ImportAsync's own by-Name matching is tenant-wide,
        // not environment-scoped, and would otherwise find whichever same-named agent exists in ANY
        // environment (typically the source's own row on a first promotion), overwriting it in place
        // instead of creating an independent copy for this environment.
        string? targetAgentId;
        using (var lookupDb = _db.CreateDbContext())
        {
            targetAgentId = await lookupDb.AgentDefinitions
                .Where(a => a.TenantId == tenantId && a.EnvironmentId == environmentId && a.LogicalId == logicalId)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(ct);
        }

        // When no row exists yet for this specific (tenant, environment, logicalId), force a
        // genuine CREATE (OverwriteExisting = false) — otherwise ImportAsync's fallback by-Name
        // search would still find and overwrite whichever OTHER environment's same-named agent
        // happens to exist (typically the source's own row on a first promotion).
        var options = targetAgentId is { Length: > 0 }
            ? new AgentImportOptions { OverwriteExisting = true, ImportRules = true, TargetAgentId = targetAgentId }
            : new AgentImportOptions { OverwriteExisting = false, ImportRules = true };

        var result = await _export.ImportAsync(bundle, TenantContext.System(tenantId), options, ct);

        if (result.Warnings.Count > 0)
        {
            _logger.LogWarning(
                "Agent snapshot materialize for '{Name}' produced warnings: {Warnings}",
                bundle.Agent.Name, string.Join("; ", result.Warnings));
        }

        // ImportAsync predates the environment/logical-id columns — tag the resulting row ourselves.
        using var db = _db.CreateDbContext();
        var agent = await db.AgentDefinitions.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == result.AgentId, ct);
        if (agent is not null)
        {
            agent.LogicalId = logicalId;
            agent.EnvironmentId = environmentId;
            await db.SaveChangesAsync(ct);
        }
    }
}
