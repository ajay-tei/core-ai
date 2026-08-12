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
    /// <summary><paramref name="targetLlmConfigId"/> (Agent promotions only) is the override the caller is about to apply via <see cref="PromoteAsync"/> — when set, the root agent's own pinned LlmConfigId is not checked for a missing target-environment key, since the override replaces it.</summary>
    Task<PromotionPreview> PreviewAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, int? targetLlmConfigId, CancellationToken ct);

    /// <summary><paramref name="changeNote"/> is recorded on the resulting ledger version(s) in <paramref name="toEnvironmentId"/> (e.g. a user-supplied summary of what this promotion changes).
    /// <paramref name="targetLlmConfigId"/> (Agent promotions only) explicitly sets the promoted row's LlmConfigId in the target environment; when null, the target's own existing LlmConfigId is left untouched (LlmConfigId is deliberately excluded from the portable snapshot, so this is already the default "keep existing" behavior on re-promotion).</summary>
    Task<PromotionResult> PromoteAsync(int tenantId, string objectType, Guid logicalId, int fromEnvironmentId, int toEnvironmentId, string? createdBy, string? changeNote, int? targetLlmConfigId, CancellationToken ct);

    /// <summary>Restores <paramref name="logicalId"/> in <paramref name="environmentId"/> to an older recorded version (Source="rollback").</summary>
    Task<PromotionResult> RollbackAsync(int tenantId, string objectType, Guid logicalId, int environmentId, int toVersionId, string? createdBy, CancellationToken ct);
}
