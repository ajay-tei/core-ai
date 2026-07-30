using Diva.Agents.Workers;

namespace Diva.Agents.Registry;

/// <summary>
/// Read-only view of the agent registry. All pipeline stages and read-only consumers
/// depend on this interface. Implemented by both DynamicAgentRegistry (full registry)
/// and the planned ScopedAgentRegistry (Phase 19) which has no Register() operation.
/// </summary>
public interface IReadableAgentRegistry
{
    /// <summary>Get all enabled, published agents for a tenant (static + dynamic from DB).
    /// <paramref name="environmentId"/> = 0 (default) is a wildcard — no environment filter,
    /// matching today's behavior. A non-zero value scopes to that TenantEnvironmentEntity only
    /// (Track 2 Phase E); rows with a null EnvironmentId (not yet backfilled) still match.</summary>
    Task<List<IWorkerAgent>> GetAgentsForTenantAsync(int tenantId, CancellationToken ct, int environmentId = 0);

    /// <summary>Find the best-matching agent for a set of required capabilities.</summary>
    Task<IWorkerAgent?> FindBestMatchAsync(
        string[] requiredCapabilities,
        int tenantId,
        CancellationToken ct,
        int environmentId = 0);

    /// <summary>Look up a specific agent by ID. See <see cref="GetAgentsForTenantAsync"/> for the
    /// <paramref name="environmentId"/> wildcard/filter convention.</summary>
    Task<IWorkerAgent?> GetByIdAsync(string agentId, int tenantId, CancellationToken ct, int environmentId = 0);
}
