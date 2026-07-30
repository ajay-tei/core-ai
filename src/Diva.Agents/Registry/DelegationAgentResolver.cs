using System.Text.Json;
using Diva.Agents.Workers;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Diva.Agents.Registry;

/// <summary>
/// Bridges <see cref="IAgentDelegationResolver"/> (Diva.Core) to <see cref="IAgentRegistry"/> (Diva.Agents)
/// so that <c>AgentToolProvider</c> and <c>AgentToolExecutor</c> in Diva.Infrastructure can resolve agents
/// without a circular project reference.
///
/// Uses <see cref="IServiceProvider"/> to lazily resolve <see cref="IAgentRegistry"/> and break the
/// circular singleton dependency: DynamicAgentRegistry → AnthropicAgentRunner → AgentToolProvider →
/// DelegationAgentResolver → DynamicAgentRegistry.
/// </summary>
public sealed class DelegationAgentResolver(IServiceProvider sp) : IAgentDelegationResolver
{
    private IAgentRegistry? _registry;
    private IAgentRegistry Registry => _registry ??= sp.GetRequiredService<IAgentRegistry>();

    public async Task<DelegateAgentInfo?> GetAgentInfoAsync(string agentId, int tenantId, CancellationToken ct)
    {
        var agent = await Registry.GetByIdAsync(agentId, tenantId, ct);
        if (agent is null) return null;

        var cap = agent.GetCapability();
        return new DelegateAgentInfo(
            cap.AgentId,
            cap.AgentType,
            cap.Description,
            cap.Capabilities);
    }

    public async Task<AgentResponse> ExecuteAgentAsync(
        string agentId, AgentRequest request, TenantContext tenant, CancellationToken ct)
    {
        var agent = await Registry.GetByIdAsync(agentId, tenant.TenantId, ct, tenant.EnvironmentId);
        if (agent is null)
            return new AgentResponse
            {
                Success = false,
                Content = $"Agent '{agentId}' not found or not available.",
                ErrorMessage = $"Agent '{agentId}' not found.",
            };

        return await agent.ExecuteAsync(request, tenant, ct);
    }
}
