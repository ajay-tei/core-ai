namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Snapshot serializer for agents — thin wrapper around the existing <see cref="IAgentExportService"/>
/// rather than reimplementing bundle/rule serialization. Known limitation: ImportAsync's match-by-Name
/// is not environment-scoped (predates Phase A's environment columns) — acceptable today because every
/// tenant still has exactly one (backfilled) environment; Phase D/E's promotion orchestration is
/// expected to refine this once true multi-environment upsert-by-LogicalId is needed.
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

    public async Task<SerializedSnapshot?> SerializeAsync(int tenantId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var agent = await db.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.LogicalId == logicalId, ct);
        if (agent is null)
        {
            return null;
        }

        var bundle = await _export.ExportAsync(agent.Id, TenantContext.System(tenantId), ct);
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        return new SerializedSnapshot { SnapshotJson = json, Name = bundle.Agent.Name };
    }

    public async Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct)
    {
        var bundle = JsonSerializer.Deserialize<AgentExportBundle>(snapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid agent snapshot JSON.");

        var result = await _export.ImportAsync(
            bundle,
            TenantContext.System(tenantId),
            new AgentImportOptions { OverwriteExisting = true, ImportRules = true },
            ct);

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
