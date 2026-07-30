namespace Diva.Infrastructure.Promotion;

using System.Security.Cryptography;
using System.Text;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <see cref="IPromotionLedgerService"/>: append-only version history for promotable
/// objects (agents, MCP servers, scheduled tasks, agent groups), keyed by a stable
/// <see cref="Guid"/> LogicalId that is shared across all of that object's per-environment
/// physical rows. Singleton-safe — creates a new DbContext per call via
/// <see cref="IDatabaseProviderFactory"/> (matches EnvironmentService's pattern).
/// </summary>
public sealed class PromotionLedgerService : IPromotionLedgerService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<PromotionLedgerService> _logger;

    public PromotionLedgerService(IDatabaseProviderFactory db, ILogger<PromotionLedgerService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RecordVersionResult> RecordVersionAsync(
        int tenantId,
        Guid logicalId,
        string objectType,
        string name,
        int environmentId,
        string snapshotJson,
        string source,
        int? promotedFromVersionId,
        string? createdBy,
        string? changeNote,
        CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var obj = await db.PromotableObjects.FirstOrDefaultAsync(o => o.LogicalId == logicalId && o.TenantId == tenantId, ct);
        if (obj is null)
        {
            obj = new PromotableObjectEntity
            {
                LogicalId = logicalId,
                TenantId = tenantId,
                ObjectType = objectType,
                Name = name,
                OriginEnvironmentId = environmentId,
                CreatedAt = DateTime.UtcNow,
            };
            db.PromotableObjects.Add(obj);
        }
        else if (obj.Name != name)
        {
            obj.Name = name;
        }

        var hash = ComputeContentHash(snapshotJson);
        var latest = await db.PromotableVersions
            .Where(v => v.LogicalId == logicalId && v.TenantId == tenantId)
            .OrderByDescending(v => v.Version)
            .FirstOrDefaultAsync(ct);

        PromotableVersionEntity version;
        var wasNew = false;
        if (latest is not null && latest.ContentHash == hash)
        {
            version = latest;
        }
        else
        {
            version = new PromotableVersionEntity
            {
                LogicalId = logicalId,
                TenantId = tenantId,
                Version = (latest?.Version ?? 0) + 1,
                ContentHash = hash,
                SnapshotJson = snapshotJson,
                Source = source,
                PromotedFromVersionId = promotedFromVersionId,
                CreatedBy = createdBy,
                ChangeNote = changeNote,
                CreatedAt = DateTime.UtcNow,
            };
            db.PromotableVersions.Add(version);
            wasNew = true;
        }

        // Flush now so `version.Id` is populated for the deployment FK below.
        await db.SaveChangesAsync(ct);

        var deployment = await db.EnvironmentDeployments
            .FirstOrDefaultAsync(d => d.LogicalId == logicalId && d.TenantId == tenantId && d.EnvironmentId == environmentId, ct);
        if (deployment is null)
        {
            deployment = new EnvironmentDeploymentEntity
            {
                LogicalId = logicalId,
                TenantId = tenantId,
                EnvironmentId = environmentId,
                LiveVersionId = version.Id,
                PublishedAt = DateTime.UtcNow,
            };
            db.EnvironmentDeployments.Add(deployment);
        }
        else
        {
            deployment.LiveVersionId = version.Id;
            deployment.PublishedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Promotion ledger: {ObjectType} '{Name}' ({LogicalId}) recorded version {Version} (new={WasNew}, source={Source}) in environment {EnvironmentId}",
            objectType, name, logicalId, version.Version, wasNew, source, environmentId);

        return new RecordVersionResult { Version = ToDto(version), WasNew = wasNew };
    }

    public async Task<IReadOnlyList<PromotableVersionDto>> GetHistoryAsync(int tenantId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var versions = await db.PromotableVersions
            .Where(v => v.LogicalId == logicalId && v.TenantId == tenantId)
            .OrderByDescending(v => v.Version)
            .AsNoTracking()
            .ToListAsync(ct);
        return versions.Select(ToDto).ToList();
    }

    public async Task<PromotableVersionDto?> GetVersionAsync(int tenantId, int versionId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var version = await db.PromotableVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct);
        return version is null ? null : ToDto(version);
    }

    public async Task<IReadOnlyList<SnapshotFieldDiff>> DiffVersionsAsync(int tenantId, int fromVersionId, int toVersionId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var from = await db.PromotableVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == fromVersionId && v.TenantId == tenantId, ct);
        var to = await db.PromotableVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == toVersionId && v.TenantId == tenantId, ct);
        if (from is null || to is null)
        {
            return [];
        }

        return SnapshotJsonDiffer.Diff(from.SnapshotJson, to.SnapshotJson);
    }

    /// <summary>SHA-256, lowercase hex — matches PlatformApiKeyService's durable-hash convention (not the shorter, cache-key-style truncated hashes used elsewhere).</summary>
    private static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static PromotableVersionDto ToDto(PromotableVersionEntity v) => new()
    {
        Id = v.Id,
        LogicalId = v.LogicalId,
        Version = v.Version,
        ContentHash = v.ContentHash,
        SnapshotJson = v.SnapshotJson,
        Source = v.Source,
        PromotedFromVersionId = v.PromotedFromVersionId,
        CreatedBy = v.CreatedBy,
        ChangeNote = v.ChangeNote,
        CreatedAt = v.CreatedAt,
    };
}
