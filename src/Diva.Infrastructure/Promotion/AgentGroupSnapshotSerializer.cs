namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Snapshot serializer for agent access groups. Member AgentIdsJson is resolved to/from agent
/// Names (not portable across tenants/environments as raw ids) — mirrors AgentExportService's
/// delegate-name resolution pattern. Matched by Name within the tenant on materialize (see
/// <see cref="IPromotableSnapshotSerializer"/> for the environment-scoping caveat).
/// </summary>
public sealed class AgentGroupSnapshotSerializer : IPromotableSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<AgentGroupSnapshotSerializer> _logger;

    public string ObjectType => "AgentGroup";

    public AgentGroupSnapshotSerializer(IDatabaseProviderFactory db, ILogger<AgentGroupSnapshotSerializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SerializedSnapshot?> SerializeAsync(int tenantId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var group = await db.AgentGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.LogicalId == logicalId, ct);
        if (group is null)
        {
            return null;
        }

        var agentNames = await ResolveAgentNamesAsync(db, group.AgentIdsJson, ct);

        var snapshot = new AgentGroupSnapshot
        {
            Name = group.Name,
            Description = group.Description,
            AllowedRolesJson = group.AllowedRolesJson,
            AgentNames = agentNames,
        };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return new SerializedSnapshot { SnapshotJson = json, Name = group.Name };
    }

    public async Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<AgentGroupSnapshot>(snapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid agent group snapshot JSON.");

        using var db = _db.CreateDbContext();

        var warnings = new List<string>();
        var agentIdsJson = await ResolveAgentIdsJsonAsync(db, tenantId, snapshot.AgentNames, warnings, ct);
        if (warnings.Count > 0)
        {
            _logger.LogWarning(
                "Agent group snapshot materialize for '{Name}': {Warnings}",
                snapshot.Name, string.Join("; ", warnings));
        }

        var group = await db.AgentGroups.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Name == snapshot.Name, ct);
        if (group is null)
        {
            group = new AgentGroupEntity { TenantId = tenantId, Name = snapshot.Name, CreatedAt = DateTime.UtcNow };
            db.AgentGroups.Add(group);
        }

        group.Description = snapshot.Description;
        group.AllowedRolesJson = snapshot.AllowedRolesJson;
        group.AgentIdsJson = agentIdsJson;
        group.UpdatedAt = DateTime.UtcNow;
        group.LogicalId = logicalId;
        group.EnvironmentId = environmentId;

        await db.SaveChangesAsync(ct);
    }

    private static async Task<IReadOnlyList<string>> ResolveAgentNamesAsync(DivaDbContext db, string? agentIdsJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentIdsJson) || agentIdsJson.Trim() == "[]")
        {
            return [];
        }

        List<string>? ids;
        try
        {
            ids = JsonSerializer.Deserialize<List<string>>(agentIdsJson);
        }
        catch
        {
            return [];
        }

        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var names = await db.AgentDefinitions
            .Where(a => ids.Contains(a.Id))
            .Select(a => a.Name)
            .ToListAsync(ct);
        return names.AsReadOnly();
    }

    private static async Task<string?> ResolveAgentIdsJsonAsync(
        DivaDbContext db, int tenantId, IReadOnlyList<string> names, List<string> warnings, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var nameList = names.ToList();
        var found = await db.AgentDefinitions
            .Where(a => a.TenantId == tenantId && nameList.Contains(a.Name))
            .Select(a => new { a.Id, a.Name })
            .ToListAsync(ct);

        var foundNames = found.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in nameList.Where(n => !foundNames.Contains(n)))
        {
            warnings.Add($"Member agent '{name}' not found in this tenant — skipped.");
        }

        if (found.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(found.Select(f => f.Id).ToList());
    }
}
