namespace Diva.Core.Models;

/// <summary>
/// Encapsulates all headers to propagate to MCP tool calls.
/// Built from TenantContext by McpHeaderPropagator.
/// </summary>
public sealed class McpRequestContext
{
    public string? BearerToken { get; init; }
    public int TenantId { get; init; }
    public int SiteId { get; init; }
    public string CorrelationId { get; init; } = string.Empty;
    public Dictionary<string, string> CustomHeaders { get; init; } = [];
    /// <summary>Injected as X-Agent-Id — required by agent memory MCP tools.</summary>
    public string? AgentId { get; init; }
    /// <summary>Injected as X-Session-Id — scopes working memory to the current session.</summary>
    public string? SessionId { get; init; }

    public Dictionary<string, string> ToHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(BearerToken))
        {
            headers["Authorization"]          = $"Bearer {BearerToken}";
            headers["X-Forwarded-Authorization"] = $"Bearer {BearerToken}";
        }

        headers["X-Tenant-ID"]       = TenantId.ToString();
        headers["X-Site-ID"]         = SiteId.ToString();
        headers["X-Correlation-ID"]  = CorrelationId;

        if (!string.IsNullOrEmpty(AgentId))   headers["X-Agent-Id"]   = AgentId;
        if (!string.IsNullOrEmpty(SessionId)) headers["X-Session-Id"] = SessionId;

        foreach (var (key, value) in CustomHeaders)
            headers[$"X-Tenant-{key}"] = value;

        return headers;
    }

    public static McpRequestContext FromTenant(TenantContext tenant, string? agentId = null, string? sessionId = null) => new()
    {
        BearerToken     = tenant.AccessToken,
        TenantId        = tenant.TenantId,
        SiteId          = tenant.CurrentSiteId,
        CorrelationId   = tenant.CorrelationId,
        CustomHeaders   = tenant.CustomHeaders,
        AgentId         = agentId,
        SessionId       = sessionId,
    };
}
