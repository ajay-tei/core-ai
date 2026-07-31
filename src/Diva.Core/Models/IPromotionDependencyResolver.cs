namespace Diva.Core.Models;

/// <summary>A dependency this object needs, identified for promotion-closure purposes.</summary>
public sealed record PromotableDependency(string ObjectType, Guid LogicalId, string DisplayName);

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
}
