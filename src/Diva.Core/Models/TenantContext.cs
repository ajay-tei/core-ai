namespace Diva.Core.Models;

/// <summary>
/// Rich tenant context built from JWT claims in TenantContextMiddleware.
/// Flows through the entire request pipeline. Never construct manually in business logic.
/// </summary>
public sealed class TenantContext
{
    // ── Identity ──────────────────────────────────────────────
    public int TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string UserEmail { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;

    // ── Authorization ─────────────────────────────────────────
    public string Role { get; init; } = string.Empty;
    public string[] UserRoles { get; init; } = [];
    public string[] UserGroups { get; init; } = [];      // SSO groups (separate from roles)
    public string[] AgentAccess { get; init; } = [];       // which agent types this user can invoke
    public string[] GroupAccess { get; init; } = [];       // agent-group IDs explicitly granted (API key / claim)

    // ── Site scoping ──────────────────────────────────────────
    public int[] SiteIds { get; init; } = [];              // all sites this user can access
    public int CurrentSiteId { get; init; }                // site for this request (from header or default)

    // ── Token propagation ─────────────────────────────────────
    public string? AccessToken { get; init; }
    public DateTimeOffset TokenExpiry { get; init; }
    public string? TeamApiKey { get; init; }               // LiteLLM team key for cost tracking
    public string? InboundApiKey { get; init; }            // Raw X-API-Key used to authenticate this request (for MCP forwarding)

    /// <summary>
    /// DB id of the platform API key used to authenticate this request, when authenticated via X-API-Key.
    /// null for JWT/SSO callers. Drives per-API-key MCP credential selection for shared tool servers.
    /// </summary>
    public int? PlatformApiKeyId { get; init; }

    /// <summary>
    /// User-group id the caller explicitly chose for this chat/invocation, used to resolve shared
    /// MCP server credentials when the user belongs to multiple credential-mapped groups. null =
    /// no explicit choice (falls back to the default lowest-user-group-id selection). Only takes
    /// effect when the caller actually belongs to the group and it maps a credential for the server.
    /// </summary>
    public int? PreferredUserGroupId { get; init; }

    // ── Tracing ───────────────────────────────────────────────
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString();
    public string? SessionId { get; init; }

    // ── Custom headers (propagated to MCP tools) ──────────────
    public Dictionary<string, string> CustomHeaders { get; init; } = [];

    /// <summary>
    /// SSO-configured forward headers to inject into MCP HTTP calls when the SSO token
    /// is actively being forwarded (PassSsoToken=true AND user has an SSO session).
    /// Populated from the "sso_fwd_headers" JWT claim; empty for local-auth users.
    /// </summary>
    public Dictionary<string, string> SsoForwardHeaders { get; init; } = [];

    // ── Helpers ───────────────────────────────────────────────
    public bool CanAccessSite(int siteId) =>
        SiteIds.Length == 0 || SiteIds.Contains(siteId);

    public bool CanInvokeAgent(string agentType) =>
        AgentAccess.Length == 0 ||
        AgentAccess.Contains("*") ||
        AgentAccess.Contains(agentType, StringComparer.OrdinalIgnoreCase);

    public bool IsAdmin => UserRoles.Contains("admin", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this user is the platform-level master admin (TenantId=0, role=master_admin).
    /// Master admins bypass tenant isolation and can manage all tenants.
    /// </summary>
    public bool IsMasterAdmin => TenantId == 0 &&
        UserRoles.Contains("master_admin", StringComparer.OrdinalIgnoreCase);

    public bool IsTokenExpired => DateTimeOffset.UtcNow >= TokenExpiry;

    /// <summary>Fallback for unauthenticated/internal contexts (e.g., scheduled jobs).</summary>
    public static TenantContext System(int tenantId, int siteId = 0) => new()
    {
        TenantId = tenantId,
        TenantName = "System",
        UserId = "system",
        SiteIds = siteId > 0 ? [siteId] : [],
        CurrentSiteId = siteId,
        UserRoles = ["system"],
        AgentAccess = ["*"]
    };

    /// <summary>
    /// System context that carries a specific user's identity so downstream user-group resolution
    /// (e.g. MCP credential selection) matches that user. Used by scheduled tasks configured to
    /// "run as" a user profile. Retains full agent access (<c>["*"]</c>) — this is an internal,
    /// admin-configured run, not an interactive user request.
    /// </summary>
    public static TenantContext RunAsUser(
        int tenantId, string userId, string? email = null, string? displayName = null, int siteId = 0) => new()
        {
            TenantId = tenantId,
            TenantName = "System",
            UserId = userId,
            UserEmail = email ?? string.Empty,
            UserName = displayName ?? string.Empty,
            SiteIds = siteId > 0 ? [siteId] : [],
            CurrentSiteId = siteId,
            UserRoles = ["system"],
            AgentAccess = ["*"]
        };

    /// <summary>Returns a copy of this context with <paramref name="sessionId"/> set.</summary>
    public TenantContext WithSession(string? sessionId) => new()
    {
        TenantId = TenantId,
        TenantName = TenantName,
        UserId = UserId,
        UserEmail = UserEmail,
        UserName = UserName,
        Role = Role,
        UserRoles = UserRoles,
        UserGroups = UserGroups,
        AgentAccess = AgentAccess,
        GroupAccess = GroupAccess,
        SiteIds = SiteIds,
        CurrentSiteId = CurrentSiteId,
        AccessToken = AccessToken,
        TokenExpiry = TokenExpiry,
        TeamApiKey = TeamApiKey,
        InboundApiKey = InboundApiKey,
        PlatformApiKeyId = PlatformApiKeyId,
        PreferredUserGroupId = PreferredUserGroupId,
        CorrelationId = CorrelationId,
        SessionId = sessionId,
        CustomHeaders = CustomHeaders,
        SsoForwardHeaders = SsoForwardHeaders,
    };

    /// <summary>Returns a copy of this context with <paramref name="preferredUserGroupId"/> set.</summary>
    public TenantContext WithPreferredUserGroup(int? preferredUserGroupId) => new()
    {
        TenantId = TenantId,
        TenantName = TenantName,
        UserId = UserId,
        UserEmail = UserEmail,
        UserName = UserName,
        Role = Role,
        UserRoles = UserRoles,
        UserGroups = UserGroups,
        AgentAccess = AgentAccess,
        GroupAccess = GroupAccess,
        SiteIds = SiteIds,
        CurrentSiteId = CurrentSiteId,
        AccessToken = AccessToken,
        TokenExpiry = TokenExpiry,
        TeamApiKey = TeamApiKey,
        InboundApiKey = InboundApiKey,
        PlatformApiKeyId = PlatformApiKeyId,
        PreferredUserGroupId = preferredUserGroupId,
        CorrelationId = CorrelationId,
        SessionId = SessionId,
        CustomHeaders = CustomHeaders,
        SsoForwardHeaders = SsoForwardHeaders,
    };

    /// <summary>
    /// Dev-mode master admin context — used when OAuth is disabled.
    /// Has TenantId=0 and role=master_admin so all platform-level endpoints are accessible.
    /// </summary>
    public static TenantContext DevMasterAdmin() => new()
    {
        TenantId = 0,
        TenantName = "Dev Admin",
        UserId = "dev-admin",
        UserEmail = "dev@localhost",
        UserName = "Dev Admin",
        UserRoles = ["master_admin", "admin", "system"],
        AgentAccess = ["*"]
    };
}
