using Diva.Agents.Registry;
using Microsoft.Extensions.Logging;

namespace Diva.Agents.Supervisor.Stages;

/// <summary>
/// Pre-fetches the tenant's available agents once per pipeline run and caches them in
/// state.AvailableAgents. Placed before DecomposeStage so LlmDecompositionStrategy has
/// agent metadata for its prompt, and CapabilityMatchStage reuses the list without a
/// second DB round trip.
///
/// Respects state.ScopedRegistry when set (Phase 19 coordinator isolation) and falls
/// back to the global registry otherwise.
/// </summary>
public sealed class AgentContextStage : ISupervisorPipelineStage
{
    private readonly IReadableAgentRegistry _globalRegistry;
    private readonly ILogger<AgentContextStage> _logger;

    public AgentContextStage(IReadableAgentRegistry globalRegistry, ILogger<AgentContextStage> logger)
    {
        _globalRegistry = globalRegistry;
        _logger         = logger;
    }

    public async Task<SupervisorState> ExecuteAsync(SupervisorState state, CancellationToken ct)
    {
        var registry = state.ScopedRegistry ?? _globalRegistry;
        state.AvailableAgents = await registry.GetAgentsForTenantAsync(state.TenantContext.TenantId, ct, state.TenantContext.EnvironmentId);

        _logger.LogInformation("AgentContextStage: loaded {Count} agent(s) for tenant {TenantId} (scoped={Scoped})",
            state.AvailableAgents.Count, state.TenantContext.TenantId, state.ScopedRegistry is not null);

        return state;
    }
}
