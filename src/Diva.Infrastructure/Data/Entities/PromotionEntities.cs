namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// One row per <see cref="LogicalId"/> — a promotable object's stable cross-environment identity,
/// regardless of how many per-environment physical rows (copies) exist for it. See
/// <see cref="PromotableVersionEntity"/> for its append-only version history and
/// <see cref="EnvironmentDeploymentEntity"/> for which version is currently live in each environment.
/// Generalizes the (unimplemented) Phase 27 marketplace design's CatalogItemEntity to serve both
/// marketplace distribution and environment promotion with one engine.
/// </summary>
public class PromotableObjectEntity : ITenantEntity
{
    public Guid LogicalId { get; set; } = Guid.NewGuid();
    public int TenantId { get; set; }

    /// <summary>"Agent" | "McpServer" | "ScheduledTask" | "AgentGroup".</summary>
    public string ObjectType { get; set; } = string.Empty;

    /// <summary>Current display name — kept in sync with the live row's Name on each recorded version.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>FK to TenantEnvironmentEntity — the environment this object was first created in.</summary>
    public int OriginEnvironmentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Append-only, immutable version history for a <see cref="PromotableObjectEntity"/>. A row is
/// never updated or deleted after creation — a new publish/promotion/rollback always appends a new
/// row with a monotonically-increasing <see cref="Version"/> (never re-uses/decrements one), fixing
/// the "Version never bumps" gap flagged in the (unimplemented) Phase 27 marketplace design for
/// <c>AgentDefinitionEntity.Version</c> — generically, for all 4 promotable entity types at once.
/// </summary>
public class PromotableVersionEntity : ITenantEntity
{
    public int Id { get; set; }
    public Guid LogicalId { get; set; }
    public int TenantId { get; set; }

    /// <summary>Monotonic per LogicalId, starting at 1.</summary>
    public int Version { get; set; }

    /// <summary>SHA-256 (lowercase hex) of <see cref="SnapshotJson"/> — used to dedup identical republishes.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Full portable entity snapshot (canonical JSON) — shape depends on ObjectType; see the
    /// per-type <c>IPromotableSnapshotSerializer</c> implementation for what it contains.</summary>
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>"manual" | "publish" | "promotion" | "rollback".</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Which version (in the source environment) this was promoted/rolled-back from. Null for in-environment publishes.</summary>
    public int? PromotedFromVersionId { get; set; }

    public string? CreatedBy { get; set; }
    public string? ChangeNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks which version of a <see cref="PromotableObjectEntity"/> is currently live in a given
/// environment — one row per (LogicalId, EnvironmentId). Draft (unpublished, in-progress) state is
/// a separate, not-yet-built concept (Phase C's EntityDraftEntity) — this table only ever reflects
/// what's actually live/published.
/// </summary>
public class EnvironmentDeploymentEntity : ITenantEntity
{
    public int Id { get; set; }
    public Guid LogicalId { get; set; }
    public int TenantId { get; set; }

    /// <summary>FK to TenantEnvironmentEntity.</summary>
    public int EnvironmentId { get; set; }

    /// <summary>FK to PromotableVersionEntity — the version currently serving live traffic in this environment.</summary>
    public int? LiveVersionId { get; set; }

    public DateTime? PublishedAt { get; set; }
}
