namespace Diva.Core.Models;

/// <summary>A dependency this object needs, identified for promotion-closure purposes.
/// <paramref name="CurrentVersion"/> is the version currently live in the target environment
/// (null = not live there yet); <paramref name="PromotingVersion"/> is the source environment's
/// live version about to be promoted (null = not yet recorded in the ledger). Both populated only
/// by <see cref="IPromotionOrchestrationService.PreviewAsync"/> for the admin UI's confirmation
/// dialog — not meaningful outside that call. <paramref name="IsOptional"/> marks an optional
/// dependent (see <see cref="IPromotionDependencyResolver.GetOptionalDependentsAsync"/>) that the
/// caller may exclude from promotion; false for ordinary cascade/root items.</summary>
public sealed record PromotableDependency(string ObjectType, Guid LogicalId, string DisplayName, int? CurrentVersion = null, int? PromotingVersion = null, bool IsOptional = false);

/// <summary>A named external secret/config this object references that is never copied/promoted —
/// it must already exist, independently configured, in the target environment (Phase G/I's "keys
/// never travel with promotion" rule). Missing = hard block.</summary>
public sealed record BlockingSecretDependency(string Kind, string Name);

/// <summary>
/// Resolves, for one promotable object type, what it depends on. Two distinct kinds of
/// dependency, per the plan's "auto-cascade only flows toward dependencies, never dependents" rule:
/// - Cascade dependencies: things this object needs to function that get promoted ALONGSIDE it
///   automatically (e.g. an Agent's referenced MCP servers/delegate agents).
/// - Forward dependencies: things this object references that must ALREADY exist in the target
///   environment — validated, never auto-created (e.g. a ScheduledTask's Agent, an AgentGroup's
///   member agents). Promoting the dependent never silently promotes what it depends on backwards.
/// </summary>
public interface IPromotionDependencyResolver
{
    string ObjectType { get; }

    Task<IReadOnlyList<PromotableDependency>> GetCascadeDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct);

    Task<IReadOnlyList<PromotableDependency>> GetForwardDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct);

    /// <summary>Named external secrets/configs (e.g. an Agent's LlmConfigId, resolved to its Name)
    /// that must already have their own row in the target environment — never copied/promoted.
    /// Default-empty for types with no such dependency.</summary>
    Task<IReadOnlyList<BlockingSecretDependency>> GetBlockingSecretDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<BlockingSecretDependency>>([]);

    /// <summary>Objects that reference this one but are NOT required for it to function (e.g. a
    /// ScheduledTask that invokes this Agent) — offered as optional, excludable promotion items.
    /// Unlike <see cref="GetCascadeDependenciesAsync"/> results, these must be materialized AFTER
    /// the object they depend on, never before (the reverse dependency direction). Default-empty
    /// for types with no such optional dependents.</summary>
    Task<IReadOnlyList<PromotableDependency>> GetOptionalDependentsAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);
}
