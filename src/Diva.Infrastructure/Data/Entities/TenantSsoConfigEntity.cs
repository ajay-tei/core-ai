using Diva.Sso;

namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Stores per-tenant SSO provider configuration.
/// Each tenant can have one or more SSO providers (e.g., primary + backup).
/// Implements ISsoProviderConfig so it can be used directly by Diva.Sso validators.
/// </summary>
public class TenantSsoConfigEntity : ITenantEntity, ISsoProviderConfig
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    // ── Provider identity ──────────────────────────────────────────────────
    public string ProviderName { get; set; } = "";     // "google" | "azure" | "okta" | "generic"

    /// <summary>
    /// JWT iss claim value — used as lookup key for multi-tenant validation.
    /// Must be unique across the platform.
    /// For opaque tokens, set to a descriptive tenant identifier string.
    /// </summary>
    public string Issuer { get; set; } = "";

    // ── OAuth2 client credentials ──────────────────────────────────────────
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";     // TODO: encrypt at rest via IDataProtector

    // ── Explicit OAuth2/OIDC endpoints (override OIDC discovery if set) ───
    public string? AuthorizationEndpoint { get; set; } // /authorize URL
    public string? TokenEndpoint { get; set; }         // /token URL
    public string? UserinfoEndpoint { get; set; }      // /userinfo URL

    /// <summary>
    /// OIDC discovery base URL. When set, endpoints are auto-populated from
    /// {Authority}/.well-known/openid-configuration. Can be omitted if all
    /// endpoints are provided explicitly (supports non-OIDC OAuth2 providers).
    /// </summary>
    public string? Authority { get; set; }

    // ── Redirect / proxy config ────────────────────────────────────────────
    public string ProxyBaseUrl { get; set; } = "";     // base URL for redirect_uri construction
    public string? ProxyAdminEmail { get; set; }       // admin/owner email for this SSO config

    // ── Role & team mapping ────────────────────────────────────────────────
    public bool UseRoleMappings { get; set; } = false;  // map provider roles → app roles
    public bool UseTeamMappings { get; set; } = false;  // map provider groups → tenant teams

    // ── Token type & validation ────────────────────────────────────────────
    /// <summary>"jwt" for standard JWTs; "opaque" for tokens requiring introspection.</summary>
    public string TokenType { get; set; } = "jwt";

    /// <summary>Required when TokenType = "opaque". OAuth2 token introspection endpoint URL.</summary>
    public string? IntrospectionEndpoint { get; set; }

    public string Audience { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional JSON override for claim name mappings.
    /// Format: {"TenantId":"tid","UserId":"sub","Roles":"groups"}
    /// Falls back to global OAuthOptions.ClaimMappings when null.
    /// </summary>
    public string? ClaimMappingsJson { get; set; }

    /// <summary>
    /// Provider logout endpoint. Used by the portal to redirect the user after sign-out.
    /// Not required for token validation — purely for session termination UX.
    /// </summary>
    public string? LogoutUrl { get; set; }

    /// <summary>
    /// Comma-separated email domains that belong to this tenant's SSO provider.
    /// Used by the tenant discovery endpoint to route users to the correct login.
    /// Example: "contoso.com,contoso.onmicrosoft.com"
    /// </summary>
    public string? EmailDomains { get; set; }

    /// <summary>
    /// Optional JSON object (Dictionary&lt;string, string&gt;) of additional HTTP headers to
    /// forward verbatim to MCP servers when the SSO token is being forwarded
    /// (i.e. the MCP binding has PassSsoToken=true AND the user has an active SSO session).
    /// Reserved headers such as "Authorization" are silently skipped.
    /// Example: {"X-Tenant-Domain":"contoso.com","X-App-Context":"diva"}
    /// </summary>
    public string? SsoForwardHeadersJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
