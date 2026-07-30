namespace Diva.Core.Models;

/// <summary>
/// Portable snapshot of a single promotable object version, exposed at the Core layer so
/// callers never depend on the EF entity directly (mirrors the AgentExportBundle convention).
/// </summary>
public sealed record PromotableVersionDto
{
    public int Id { get; init; }
    public Guid LogicalId { get; init; }
    public int Version { get; init; }
    public string ContentHash { get; init; } = string.Empty;
    public string SnapshotJson { get; init; } = string.Empty;
    public string Source { get; init; } = "manual";
    public int? PromotedFromVersionId { get; init; }
    public string? CreatedBy { get; init; }
    public string? ChangeNote { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Result of recording a version — WasNew is false when the content hash matched the latest existing version (deduped, no new row written).</summary>
public sealed record RecordVersionResult
{
    public PromotableVersionDto Version { get; init; } = null!;
    public bool WasNew { get; init; }
}

/// <summary>One changed/added/removed field between two snapshot JSON documents (dot-path field name).</summary>
public sealed record SnapshotFieldDiff(string FieldPath, string? OldValue, string? NewValue);

/// <summary>
/// Records, retrieves, and diffs the append-only version history for promotable objects
/// (agents, MCP servers, scheduled tasks, agent groups). The ledger itself is content-agnostic —
/// SnapshotJson is an opaque string produced/consumed by the matching <see cref="IPromotableSnapshotSerializer"/>.
/// </summary>
public interface IPromotionLedgerService
{
    /// <summary>
    /// Upserts the PromotableObject row for <paramref name="logicalId"/>, then hashes
    /// <paramref name="snapshotJson"/> and appends a new version only if the hash differs from
    /// the most recent version on record (content-hash dedup — republishing unchanged content is
    /// a no-op). Always updates the environment's live-version pointer to the resulting version.
    /// </summary>
    Task<RecordVersionResult> RecordVersionAsync(
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
        CancellationToken ct);

    /// <summary>Full version history for a logical object, newest first.</summary>
    Task<IReadOnlyList<PromotableVersionDto>> GetHistoryAsync(int tenantId, Guid logicalId, CancellationToken ct);

    Task<PromotableVersionDto?> GetVersionAsync(int tenantId, int versionId, CancellationToken ct);

    /// <summary>Field-level diff between two recorded versions' SnapshotJson.</summary>
    Task<IReadOnlyList<SnapshotFieldDiff>> DiffVersionsAsync(int tenantId, int fromVersionId, int toVersionId, CancellationToken ct);
}

/// <summary>A canonical portable snapshot produced by an <see cref="IPromotableSnapshotSerializer"/>.</summary>
public sealed record SerializedSnapshot
{
    public string SnapshotJson { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Produces/consumes the canonical portable JSON snapshot for one promotable object type, and
/// knows how to materialize a snapshot back onto a live entity row for a target
/// (TenantId, EnvironmentId). One implementation per ObjectType ("Agent", "McpServer",
/// "ScheduledTask", "AgentGroup"); registered as an <c>IEnumerable&lt;IPromotableSnapshotSerializer&gt;</c>
/// so callers (the ledger, and later Phase D's promotion orchestrator) can dispatch by ObjectType
/// without a switch statement — new promotable types are added by registering a new implementation only.
/// </summary>
public interface IPromotableSnapshotSerializer
{
    string ObjectType { get; }

    /// <summary>Null if no live row exists for (tenantId, logicalId).</summary>
    Task<SerializedSnapshot?> SerializeAsync(int tenantId, Guid logicalId, CancellationToken ct);

    /// <summary>
    /// Creates or updates the live row for (tenantId, environmentId) from the snapshot, matched by
    /// Name within the tenant. Known limitation: matching is not yet environment-scoped (a tenant
    /// with more than one environment could match the wrong environment's row by name) — safe today
    /// because every tenant still has exactly one (backfilled) environment; Phase D/E's promotion
    /// orchestration is expected to refine this to match by (EnvironmentId, LogicalId) once
    /// environment-scoped routing ships.
    /// </summary>
    Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct);
}
