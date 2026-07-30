namespace Diva.Infrastructure.Promotion;

using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <see cref="IPromotionOrchestrationService"/>. Singleton-safe — creates a new
/// DbContext per call via <see cref="IDatabaseProviderFactory"/>.
/// </summary>
public sealed class PromotionOrchestrationService : IPromotionOrchestrationService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IPromotionLedgerService _ledger;
    private readonly IReadOnlyDictionary<string, IPromotableSnapshotSerializer> _serializers;
    private readonly IReadOnlyDictionary<string, IPromotionDependencyResolver> _resolvers;
    private readonly ILogger<PromotionOrchestrationService> _logger;

    public PromotionOrchestrationService(
        IDatabaseProviderFactory db,
        IPromotionLedgerService ledger,
        IEnumerable<IPromotableSnapshotSerializer> serializers,
        IEnumerable<IPromotionDependencyResolver> resolvers,
        ILogger<PromotionOrchestrationService> logger)
    {
        _db = db;
        _ledger = ledger;
        _serializers = serializers.ToDictionary(s => s.ObjectType);
        _resolvers = resolvers.ToDictionary(r => r.ObjectType);
        _logger = logger;
    }

    public async Task<PromotionPreview> PreviewAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var rankCheck = await CheckRankAsync(db, tenantId, fromEnvironmentId, toEnvironmentId, ct);
        if (rankCheck is not null)
        {
            return new PromotionPreview { CanPromote = false, BlockingError = rankCheck };
        }

        var (closure, forwardErrors) = await BuildClosureAsync(db, tenantId, objectType, logicalId, fromEnvironmentId, toEnvironmentId, ct);
        if (forwardErrors.Count > 0)
        {
            return new PromotionPreview { CanPromote = false, BlockingError = string.Join(" ", forwardErrors) };
        }

        var deps = new List<PromotableDependency>();
        foreach (var (ot, lid) in closure)
        {
            if (_serializers.TryGetValue(ot, out var serializer))
            {
                var snap = await serializer.SerializeAsync(tenantId, lid, ct);
                if (snap is not null)
                {
                    deps.Add(new PromotableDependency(ot, lid, snap.Name));
                }
            }
        }

        return new PromotionPreview { CanPromote = true, WillPromote = deps };
    }

    public async Task<PromotionResult> PromoteAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, string? createdBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var rankCheck = await CheckRankAsync(db, tenantId, fromEnvironmentId, toEnvironmentId, ct);
        if (rankCheck is not null)
        {
            return new PromotionResult { Success = false, Error = rankCheck };
        }

        var (closure, forwardErrors) = await BuildClosureAsync(db, tenantId, objectType, logicalId, fromEnvironmentId, toEnvironmentId, ct);
        if (forwardErrors.Count > 0)
        {
            return new PromotionResult { Success = false, Error = string.Join(" ", forwardErrors) };
        }

        // Dependencies must be materialized before whatever depends on them — the closure was
        // discovered root-first (BFS), so process it in reverse (leaves first).
        closure.Reverse();

        var results = new List<PromotedObjectResult>();
        foreach (var (ot, lid) in closure)
        {
            if (!_serializers.TryGetValue(ot, out var serializer))
            {
                continue;
            }

            var snapshot = await serializer.SerializeAsync(tenantId, lid, ct);
            if (snapshot is null)
            {
                continue;
            }

            // Idempotent skip: target environment already has this exact content live.
            var targetDeployment = await db.EnvironmentDeployments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.LogicalId == lid && d.TenantId == tenantId && d.EnvironmentId == toEnvironmentId, ct);
            if (targetDeployment?.LiveVersionId is int liveId)
            {
                var liveVersion = await db.PromotableVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == liveId, ct);
                if (liveVersion is not null && liveVersion.SnapshotJson == snapshot.SnapshotJson)
                {
                    results.Add(new PromotedObjectResult(ot, lid, snapshot.Name, liveVersion.Id, liveVersion.Version, WasSkipped: true));
                    continue;
                }
            }

            await serializer.MaterializeAsync(tenantId, toEnvironmentId, lid, snapshot.SnapshotJson, ct);

            var sourceDeployment = await db.EnvironmentDeployments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.LogicalId == lid && d.TenantId == tenantId && d.EnvironmentId == fromEnvironmentId, ct);

            var recorded = await _ledger.RecordVersionAsync(
                tenantId, lid, ot, snapshot.Name, toEnvironmentId,
                snapshot.SnapshotJson, "promotion", sourceDeployment?.LiveVersionId, createdBy, null, ct);

            results.Add(new PromotedObjectResult(ot, lid, snapshot.Name, recorded.Version.Id, recorded.Version.Version, WasSkipped: !recorded.WasNew));
        }

        var run = new Data.Entities.PromotionRunEntity
        {
            TenantId = tenantId,
            RootObjectType = objectType,
            RootLogicalId = logicalId,
            FromEnvironmentId = fromEnvironmentId,
            ToEnvironmentId = toEnvironmentId,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            PromotedVersionsJson = System.Text.Json.JsonSerializer.Serialize(results),
        };
        db.PromotionRuns.Add(run);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Promotion run {RunId}: {ObjectType} {LogicalId} {FromEnv}->{ToEnv} promoted {Count} object(s)",
            run.Id, objectType, logicalId, fromEnvironmentId, toEnvironmentId, results.Count(r => !r.WasSkipped));

        return new PromotionResult { Success = true, RunId = run.Id, PromotedObjects = results };
    }

    public async Task<PromotionResult> RollbackAsync(int tenantId, string objectType, Guid logicalId, int environmentId, int toVersionId, string? createdBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var targetVersion = await db.PromotableVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == toVersionId && v.TenantId == tenantId && v.LogicalId == logicalId, ct);
        if (targetVersion is null)
        {
            return new PromotionResult { Success = false, Error = "Version not found." };
        }

        if (!_serializers.TryGetValue(objectType, out var serializer))
        {
            return new PromotionResult { Success = false, Error = $"No snapshot serializer registered for '{objectType}'." };
        }

        await serializer.MaterializeAsync(tenantId, environmentId, logicalId, targetVersion.SnapshotJson, ct);

        var name = await ResolveNameAsync(db, tenantId, logicalId, ct);
        var recorded = await _ledger.RecordVersionAsync(
            tenantId, logicalId, objectType, name, environmentId, targetVersion.SnapshotJson,
            "rollback", targetVersion.Id, createdBy, $"Rolled back to v{targetVersion.Version}", ct);

        return new PromotionResult
        {
            Success = true,
            PromotedObjects = [new PromotedObjectResult(objectType, logicalId, name, recorded.Version.Id, recorded.Version.Version, WasSkipped: !recorded.WasNew)],
        };
    }

    private static async Task<string> ResolveNameAsync(Data.DivaDbContext db, int tenantId, Guid logicalId, CancellationToken ct)
    {
        var obj = await db.PromotableObjects.AsNoTracking().FirstOrDefaultAsync(o => o.TenantId == tenantId && o.LogicalId == logicalId, ct);
        return obj?.Name ?? string.Empty;
    }

    private static async Task<string?> CheckRankAsync(Data.DivaDbContext db, int tenantId, int fromEnvironmentId, int toEnvironmentId, CancellationToken ct)
    {
        var fromEnv = await db.TenantEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == fromEnvironmentId && e.TenantId == tenantId, ct);
        var toEnv = await db.TenantEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == toEnvironmentId && e.TenantId == tenantId, ct);
        if (fromEnv is null || toEnv is null)
        {
            return "Source or target environment not found.";
        }

        if (toEnv.Rank <= fromEnv.Rank)
        {
            return $"Cannot promote from '{fromEnv.DisplayName}' (rank {fromEnv.Rank}) to '{toEnv.DisplayName}' (rank {toEnv.Rank}) — target must have a strictly higher rank.";
        }

        return null;
    }

    /// <summary>
    /// BFS over cascade dependencies (root included) plus forward-dependency validation for every
    /// node visited. Returns the closure in discovery order (root first) and any forward-dependency
    /// validation errors (non-empty means promotion must be blocked).
    /// </summary>
    private async Task<(List<(string ObjectType, Guid LogicalId)> Closure, List<string> ForwardErrors)> BuildClosureAsync(
        Data.DivaDbContext db, int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, CancellationToken ct)
    {
        var closure = new List<(string, Guid)>();
        var visited = new HashSet<(string, Guid)>();
        var queue = new Queue<(string ObjectType, Guid LogicalId)>();
        var forwardErrors = new List<string>();
        queue.Enqueue((objectType, logicalId));

        var toEnv = await db.TenantEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == toEnvironmentId && e.TenantId == tenantId, ct);

        while (queue.Count > 0)
        {
            var (ot, lid) = queue.Dequeue();
            if (!visited.Add((ot, lid)))
            {
                continue;
            }

            closure.Add((ot, lid));

            if (!_resolvers.TryGetValue(ot, out var resolver))
            {
                continue;
            }

            var forwardDeps = await resolver.GetForwardDependenciesAsync(tenantId, lid, fromEnvironmentId, ct);
            foreach (var dep in forwardDeps)
            {
                var existsInTarget = await db.EnvironmentDeployments.AnyAsync(
                    d => d.LogicalId == dep.LogicalId && d.TenantId == tenantId && d.EnvironmentId == toEnvironmentId && d.LiveVersionId != null, ct);
                if (!existsInTarget)
                {
                    forwardErrors.Add($"{dep.ObjectType} '{dep.DisplayName}' does not exist in '{toEnv?.DisplayName ?? "the target environment"}' yet — promote it first.");
                }
            }

            var cascadeDeps = await resolver.GetCascadeDependenciesAsync(tenantId, lid, fromEnvironmentId, ct);
            foreach (var dep in cascadeDeps)
            {
                if (!visited.Contains((dep.ObjectType, dep.LogicalId)))
                {
                    queue.Enqueue((dep.ObjectType, dep.LogicalId));
                }
            }
        }

        return (closure, forwardErrors);
    }
}
