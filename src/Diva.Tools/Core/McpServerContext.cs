using Diva.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Diva.Tools.Core;

public sealed class McpServerContext
{
    public TenantContext Tenant { get; init; } = null!;
    public bool IsAuthenticated { get; init; }
    public int TenantId => Tenant.TenantId;
    public string CorrelationId => Tenant.CorrelationId;
    public string? UserId => string.IsNullOrEmpty(Tenant?.UserId) ? null : Tenant.UserId;
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }

    public static readonly McpServerContext Anonymous =
        new() { Tenant = TenantContext.System(0), IsAuthenticated = false };

    /// <summary>
    /// Call inside each tool method (not constructor) to avoid MCP SDK session-init timing issues.
    /// Returns Anonymous when: HttpContext is null (stdio transport), or TenantContextMiddleware did not run.
    /// </summary>
    public static McpServerContext FromHttpContext(IHttpContextAccessor accessor)
    {
        var httpCtx = accessor.HttpContext;
        if (httpCtx is null) return Anonymous;

        // Inline equivalent of TenantContextMiddleware.TryGetTenantContext() —
        // avoids pulling Diva.Infrastructure into this library.
        var tenant = httpCtx.Items.TryGetValue("TenantContext", out var obj)
            ? obj as TenantContext
            : null;

        return new McpServerContext
        {
            Tenant = tenant ?? TenantContext.System(0),
            IsAuthenticated = tenant is not null,
            AgentId = httpCtx.Request.Headers.TryGetValue("X-Agent-Id", out var agentVal)
                ? agentVal.ToString() : null,
            SessionId = httpCtx.Request.Headers.TryGetValue("X-Session-Id", out var sessVal)
                ? sessVal.ToString() : null,
        };
    }
}
