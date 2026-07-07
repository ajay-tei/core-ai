using Diva.Core.Models;
using Diva.Infrastructure.Data.Entities;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Manages MCP client lifecycle: connecting to configured tool servers and
/// enumerating the tools they expose.  Abstracted so tests can substitute
/// a fake implementation without spawning real child processes.
/// </summary>
public interface IMcpConnectionManager
{
    /// <summary>
    /// Connects to all valid MCP server bindings declared on <paramref name="definition"/>
    /// in parallel. Failed connections are logged and excluded from the returned map.
    /// Pass <paramref name="fallbackTenant"/> for background/scheduled runs where HttpContext is unavailable.
    /// Pass <paramref name="forcePassSsoToken"/> = true to forward the caller's Bearer token to all
    /// HTTP/SSE bindings regardless of their individual PassSsoToken setting (used by test chat).
    /// Pass <paramref name="effectiveBindingsJson"/> to connect using a pre-merged binding set
    /// (inline + resolved shared-server bindings) instead of <c>definition.ToolBindings</c>.
    /// </summary>
    Task<Dictionary<string, McpClient>> ConnectAsync(
        AgentDefinitionEntity definition, CancellationToken ct,
        TenantContext? fallbackTenant = null, bool forcePassSsoToken = false,
        string? effectiveBindingsJson = null);

    /// <summary>
    /// Lists tools from all connected <paramref name="clients"/> in parallel and returns
    /// both the routing map (tool-name → McpClient) and the flat tool list.
    /// </summary>
    Task<(Dictionary<string, McpClient> Map, List<McpClientTool> Tools)> BuildToolDataAsync(
        Dictionary<string, McpClient> clients, CancellationToken ct);
}
