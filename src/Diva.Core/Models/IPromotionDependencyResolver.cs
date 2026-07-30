namespace Diva.Core.Models;

/// <summary>A dependency this object needs, identified for promotion-closure purposes.</summary>
public sealed record PromotableDependency(string ObjectType, Guid LogicalId, string DisplayName);

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
}
