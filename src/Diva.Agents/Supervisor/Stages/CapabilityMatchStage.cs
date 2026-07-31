using Diva.Agents.Registry;
using Microsoft.Extensions.Logging;

namespace Diva.Agents.Supervisor.Stages;

/// <summary>
/// Matches each sub-task to the best available worker agent using capability scoring.
/// Uses state.AvailableAgents pre-fetched by AgentContextStage (no extra DB call).
/// Falls back to IReadableAgentRegistry.GetAgentsForTenantAsync when AvailableAgents
/// is empty (e.g. unit tests running this stage in isolation).
/// </summary>
public sealed class CapabilityMatchStage : ISupervisorPipelineStage
{
    private readonly IReadableAgentRegistry _registry;
    private readonly ICapabilityScoringService _scorer;
    private readonly ILogger<CapabilityMatchStage> _logger;

    public CapabilityMatchStage(
        IReadableAgentRegistry registry,
        ICapabilityScoringService scorer,
        ILogger<CapabilityMatchStage> logger)
    {
        _registry = registry;
        _scorer = scorer;
        _logger = logger;
    }

    public async Task<SupervisorState> ExecuteAsync(SupervisorState state, CancellationToken ct)
    {
        // Reuse agents pre-fetched by AgentContextStage; fall back to direct fetch when absent.
        var agents = state.AvailableAgents.Count > 0
            ? state.AvailableAgents
            : await _registry.GetAgentsForTenantAsync(state.TenantContext.TenantId, ct, state.TenantContext.EnvironmentId);

        var plan = new List<(SubTask, Diva.Agents.Workers.IWorkerAgent)>();

        foreach (var task in state.SubTasks)
        {
            var agent = _scorer.FindBestMatch(agents, task.RequiredCapabilities);

            if (agent is null)
            {
                _logger.LogWarning("No agent found for capabilities [{Caps}] — skipping sub-task",
                    string.Join(", ", task.RequiredCapabilities));
                continue;
            }

            var cap = agent.GetCapability();
            _logger.LogInformation(
                "CapabilityMatch: sub-task matched agent={AgentId} type={AgentType} caps=[{Caps}] task={Desc}",
                cap.AgentId, cap.AgentType,
                string.Join(", ", cap.Capabilities),
                task.Description);

            plan.Add((task, agent));
        }

        if (plan.Count == 0)
        {
            state.Status = SupervisorStatus.Failed;
            state.ErrorMessage = "No capable agents found for this request. Create or publish an agent first.";
            return state;
        }

        state.DispatchPlan = plan;
        return state;
    }
}
