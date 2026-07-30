namespace Diva.Core.Models;

/// <summary>One object actually promoted (or skipped as a no-op) as part of a promotion run.</summary>
public sealed record PromotedObjectResult(string ObjectType, Guid LogicalId, string Name, int? VersionId, int? Version, bool WasSkipped);

public sealed record PromotionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int? RunId { get; init; }
    public IReadOnlyList<PromotedObjectResult> PromotedObjects { get; init; } = [];
}

public sealed record PromotionPreview
{
    public bool CanPromote { get; init; }
    public string? BlockingError { get; init; }
    /// <summary>Everything that would be promoted (dependency closure), for the admin UI's confirmation dialog.</summary>
    public IReadOnlyList<PromotableDependency> WillPromote { get; init; } = [];
}

/// <summary>
/// Promotes a promotable object (and its cascade-dependency closure) from one environment to a
/// strictly-higher-ranked environment within the SAME tenant. Reuses Phase B's ledger + snapshot
/// serializers directly — this is the orchestration layer on top of those primitives.
/// </summary>
public interface IPromotionOrchestrationService
{
    Task<PromotionPreview> PreviewAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, CancellationToken ct);

    Task<PromotionResult> PromoteAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, string? createdBy, CancellationToken ct);

    /// <summary>Restores <paramref name="logicalId"/> in <paramref name="environmentId"/> to an older recorded version (Source="rollback").</summary>
    Task<PromotionResult> RollbackAsync(int tenantId, string objectType, Guid logicalId, int environmentId, int toVersionId, string? createdBy, CancellationToken ct);
}
