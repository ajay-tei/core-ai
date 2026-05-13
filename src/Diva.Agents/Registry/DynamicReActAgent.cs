using System.Text.Json;
using System.Text.Json.Nodes;
using Diva.Agents.Workers;
using Diva.Core.Models;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;

namespace Diva.Agents.Registry;

/// <summary>
/// A worker agent created at runtime from an AgentDefinitionEntity stored in the database.
/// Delegates execution to IAgentRunner (which handles both Anthropic and OpenAI-compatible paths).
/// </summary>
public sealed class DynamicReActAgent : IWorkerAgent, IStreamableWorkerAgent
{
    private readonly AgentDefinitionEntity _definition;
    private readonly IAgentRunner _runner;

    public DynamicReActAgent(AgentDefinitionEntity definition, IAgentRunner runner)
    {
        _definition = definition;
        _runner = runner;
    }

    public AgentCapability GetCapability()
    {
        var caps = string.IsNullOrEmpty(_definition.Capabilities)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(_definition.Capabilities) ?? [];

        var tools = string.IsNullOrEmpty(_definition.ToolBindings)
            ? Array.Empty<string>()
            : (JsonNode.Parse(_definition.ToolBindings)?.AsArray()
                ?.Select(n => n is JsonObject obj && obj["name"] is JsonNode nameNode
                    ? nameNode.GetValue<string>()
                    : n?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToArray() ?? []);

        var delegates = string.IsNullOrEmpty(_definition.DelegateAgentIdsJson)
            ? Array.Empty<string>()
            : JsonSerializer.Deserialize<string[]>(_definition.DelegateAgentIdsJson) ?? [];

        return new AgentCapability
        {
            AgentId = _definition.Id,
            AgentType = _definition.AgentType,
            Description = _definition.Description,
            Capabilities = caps,
            SupportedTools = tools,
            DelegateAgentIds = delegates,
            // Dynamic agents have lower priority than statically registered agents
            Priority = 5
        };
    }

    public Task<AgentResponse> ExecuteAsync(
        AgentRequest request,
        TenantContext tenant,
        CancellationToken ct)
    {
        return _runner.RunAsync(_definition, request, tenant, ct);
    }

    public IAsyncEnumerable<AgentStreamChunk> InvokeStreamAsync(
        AgentRequest request,
        TenantContext tenant,
        CancellationToken ct)
    {
        return _runner.InvokeStreamAsync(_definition, request, tenant, ct);
    }
}
