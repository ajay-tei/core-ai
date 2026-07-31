using System.Collections.Concurrent;
using Diva.Agents.Workers;
using Diva.Core.Configuration;
using Diva.Infrastructure.A2A;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Groups;
using Diva.Infrastructure.LiteLLM;
using Diva.TenantAdmin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Agents.Registry;

/// <summary>
/// Singleton registry that combines statically registered agents with agents
/// hot-loaded from the database (AgentDefinitionEntity, status=Published, isEnabled=true).
///
/// Uses IDatabaseProviderFactory (not DivaDbContext directly) so it is safe as a Singleton.
/// </summary>
public sealed class DynamicAgentRegistry : IAgentRegistry
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IAgentRunner _runner;
    private readonly IA2AAgentClient _a2aClient;
    private readonly ICredentialResolver? _credentialResolver;
    private readonly IGroupAgentOverlayService _overlayService;
    private readonly ITenantGroupService _groupService;
    private readonly ICapabilityScoringService _scorer;
    private readonly ILogger<DynamicAgentRegistry> _logger;

    // Statically registered agents (code-defined, registered at startup)
    private readonly ConcurrentDictionary<string, IWorkerAgent> _static = new();

    public DynamicAgentRegistry(
        IDatabaseProviderFactory db,
        IAgentRunner runner,
        IA2AAgentClient a2aClient,
        IGroupAgentOverlayService overlayService,
        ITenantGroupService groupService,
        ILogger<DynamicAgentRegistry> logger,
        ICapabilityScoringService? scorer = null,
        ICredentialResolver? credentialResolver = null)
    {
        _db = db;
        _runner = runner;
        _a2aClient = a2aClient;
        _credentialResolver = credentialResolver;
        _overlayService = overlayService;
        _groupService = groupService;
        _scorer = scorer ?? new CapabilityScoringService();
        _logger = logger;
    }

    public void Register(IWorkerAgent agent)
        => _static[agent.GetCapability().AgentId] = agent;

    public async Task<List<IWorkerAgent>> GetAgentsForTenantAsync(int tenantId, CancellationToken ct, int environmentId = 0)
    {
        var agents = new List<IWorkerAgent>(_static.Values);

        using var db = _db.CreateDbContext();
        var definitions = await db.AgentDefinitions
            .Where(d => d.TenantId == tenantId && d.IsEnabled && d.Status == "Published"
                && (environmentId == 0 || d.EnvironmentId == null || d.EnvironmentId == environmentId))
            .ToListAsync(ct);

        foreach (var def in definitions)
        {
            if (!string.IsNullOrEmpty(def.A2AEndpoint))
                agents.Add(new RemoteA2AAgent(def, _a2aClient, _credentialResolver));
            else
                agents.Add(new DynamicReActAgent(def, _runner));
        }

        // ── Group template overlays ────────────────────────────────────────────
        var templates = await _groupService.GetAgentTemplatesForTenantAsync(tenantId, ct);
        if (templates.Count > 0)
        {
            var overlays = (await _overlayService.GetOverlaysAsync(tenantId, ct))
                .ToDictionary(o => o.GroupTemplateId);

            foreach (var template in templates)
            {
                // Own tenant agent takes precedence over a same-ID group template
                if (definitions.Any(d => d.Id == template.Id)) continue;

                if (!overlays.TryGetValue(template.Id, out var overlay) || !overlay.IsEnabled)
                    continue;

                var merged = GroupAgentOverlayMerger.Merge(template, overlay, tenantId, _logger);
                agents.Add(string.IsNullOrEmpty(merged.A2AEndpoint)
                    ? new DynamicReActAgent(merged, _runner)
                    : new RemoteA2AAgent(merged, _a2aClient, _credentialResolver));
            }
        }

        _logger.LogDebug("Registry: {Static} static + {Dynamic} dynamic agents for tenant {TenantId}",
            _static.Count, definitions.Count, tenantId);

        return agents;
    }

    public async Task<IWorkerAgent?> GetByIdAsync(string agentId, int tenantId, CancellationToken ct, int environmentId = 0)
    {
        // Check static agents first
        if (_static.TryGetValue(agentId, out var staticAgent))
            return staticAgent;

        using var db = _db.CreateDbContext();
        var def = await db.AgentDefinitions.FirstOrDefaultAsync(
            d => d.Id == agentId && d.TenantId == tenantId
                && (environmentId == 0 || d.EnvironmentId == null || d.EnvironmentId == environmentId), ct);

        if (def is not null)
        {
            if (!string.IsNullOrEmpty(def.A2AEndpoint))
                return new RemoteA2AAgent(def, _a2aClient, _credentialResolver);
            return new DynamicReActAgent(def, _runner);
        }

        // ── Fall back to group template + overlay ──────────────────────────────
        var overlay = await _overlayService.GetOverlayAsync(tenantId, agentId, ct);
        if (overlay is null || !overlay.IsEnabled) return null;

        var templates = await _groupService.GetAgentTemplatesForTenantAsync(tenantId, ct);
        var template = templates.FirstOrDefault(t => t.Id == agentId);
        if (template is null) return null;

        var merged = GroupAgentOverlayMerger.Merge(template, overlay, tenantId, _logger);
        if (!string.IsNullOrEmpty(merged.A2AEndpoint))
            return new RemoteA2AAgent(merged, _a2aClient, _credentialResolver);
        return new DynamicReActAgent(merged, _runner);
    }

    public async Task<IWorkerAgent?> FindBestMatchAsync(
        string[] requiredCapabilities,
        int tenantId,
        CancellationToken ct,
        int environmentId = 0)
    {
        var agents = await GetAgentsForTenantAsync(tenantId, ct, environmentId);

        if (agents.Count == 0)
        {
            _logger.LogWarning("No agents available for tenant {TenantId}", tenantId);
            return null;
        }

        return _scorer.FindBestMatch(agents, requiredCapabilities);
    }
}
