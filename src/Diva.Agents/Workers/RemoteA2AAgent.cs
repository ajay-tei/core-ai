namespace Diva.Agents.Workers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.A2A;
using Diva.Infrastructure.Data.Entities;

/// <summary>
/// Worker agent that delegates execution to a remote agent via A2A protocol.
/// Implements IStreamableWorkerAgent so the streaming endpoint can delegate directly.
/// Resolves A2ASecretRef via the tenant credential vault when available.
/// </summary>
public sealed class RemoteA2AAgent : IStreamableWorkerAgent
{
    private readonly AgentDefinitionEntity _definition;
    private readonly IA2AAgentClient _a2aClient;
    private readonly ICredentialResolver? _credentialResolver;

    public RemoteA2AAgent(AgentDefinitionEntity definition, IA2AAgentClient a2aClient, ICredentialResolver? credentialResolver = null)
    {
        _definition = definition;
        _a2aClient = a2aClient;
        _credentialResolver = credentialResolver;
    }

    public AgentCapability GetCapability() => new()
    {
        AgentId = _definition.Id,
        AgentType = _definition.AgentType,
        Description = _definition.Description,
        Capabilities = JsonSerializer.Deserialize<string[]>(_definition.Capabilities ?? "[]") ?? [],
        SupportedTools = string.IsNullOrEmpty(_definition.ToolBindings) ? [] :
            (JsonNode.Parse(_definition.ToolBindings)?.AsArray()
                ?.Select(n => n is JsonObject obj && obj["name"] is JsonNode nameNode
                    ? nameNode.GetValue<string>()
                    : n?.GetValue<string>() ?? "")
                .Where(s => s.Length > 0)
                .ToArray() ?? []),
        DelegateAgentIds = JsonSerializer.Deserialize<string[]>(_definition.DelegateAgentIdsJson ?? "[]") ?? [],
        Priority = 3,
    };

    public async Task<AgentResponse> ExecuteAsync(
        AgentRequest request, TenantContext tenant, CancellationToken ct)
    {
        var chunks = new List<AgentStreamChunk>();
        await foreach (var chunk in InvokeStreamAsync(request, tenant, ct))
            chunks.Add(chunk);

        var final = chunks.LastOrDefault(c => c.Type == "final_response");
        return new AgentResponse
        {
            Success = final is not null,
            Content = final?.Content ?? "Remote agent returned no response",
            AgentName = _definition.DisplayName,
        };
    }

    public async IAsyncEnumerable<AgentStreamChunk> InvokeStreamAsync(
        AgentRequest request, TenantContext tenant,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Resolve A2ASecretRef through the credential vault if available
        string? authToken = null;
        string authScheme = _definition.A2AAuthScheme ?? "Bearer";
        string? customHeader = null;

        if (!string.IsNullOrEmpty(_definition.A2ASecretRef))
        {
            if (_credentialResolver is not null)
            {
                var resolved = await _credentialResolver.ResolveAsync(tenant.TenantId, _definition.A2ASecretRef, ct);
                if (resolved is not null)
                {
                    authToken = resolved.ApiKey;
                    authScheme = resolved.AuthScheme;
                    customHeader = resolved.CustomHeaderName;
                }
                else
                {
                    // Credential name not found in vault — fail fast rather than forwarding
                    // the ref name as a token (which always causes 401 on the remote).
                    yield return new AgentStreamChunk
                    {
                        Type = "error",
                        Content = $"A2A credential '{_definition.A2ASecretRef}' not found in the credential vault for this tenant. " +
                                  "Add it in Settings → MCP Credentials, then retry.",
                    };
                    yield return new AgentStreamChunk { Type = "done" };
                    yield break;
                }
            }
            else
            {
                // No vault configured — treat secretRef as the literal token value.
                // Useful for dev/test environments and simple direct-token deployments.
                authToken = _definition.A2ASecretRef;
            }
        }

        await foreach (var chunk in _a2aClient.SendTaskAsync(
            _definition.A2AEndpoint!, authToken, request, ct, authScheme, customHeader,
            remoteAgentId: _definition.A2ARemoteAgentId))
        {
            yield return chunk;
        }
    }
}
