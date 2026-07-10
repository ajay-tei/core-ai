namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-scoped, reusable MCP tool-server definition shared across all of a tenant's agents.
/// Decouples the server connection (transport/command/endpoint) from individual agents:
/// agents reference a server by <see cref="Name"/> via their McpServerRefsJson, and the
/// effective credential is selected dynamically at runtime based on the platform API key
/// used to invoke the agent (see <see cref="ApiKeyCredentialMappingsJson"/>).
/// </summary>
public class TenantMcpServerEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Unique name within the tenant, referenced by an agent's McpServerRefsJson.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Transport: "stdio" (default), "http", or "sse".</summary>
    public string Transport { get; set; } = "stdio";

    // ── stdio transport ───────────────────────────────────────
    public string? Command { get; set; }

    /// <summary>JSON string[] of command arguments.</summary>
    public string? ArgsJson { get; set; }

    /// <summary>JSON Dictionary&lt;string,string&gt; of environment variables.</summary>
    public string? EnvJson { get; set; }

    // ── http/sse transport ────────────────────────────────────
    public string? Endpoint { get; set; }

    public bool PassSsoToken { get; set; }
    public bool PassTenantHeaders { get; set; }

    /// <summary>
    /// Credential name (references <see cref="McpCredentialEntity.Name"/>) used when no
    /// per-API-key mapping matches the caller — e.g. browser/JWT callers without SSO forwarding.
    /// Empty/null = no default credential (SSO passthrough or unauthenticated server).
    /// </summary>
    public string? DefaultCredentialRef { get; set; }

    /// <summary>
    /// JSON array of per-API-key credential routing rules:
    /// [{ "apiKeyId": 12, "credentialRef": "weather-key-acme" }, ...].
    /// At runtime the rule whose apiKeyId matches the invoking platform API key wins;
    /// otherwise <see cref="DefaultCredentialRef"/> is used.
    /// </summary>
    public string? ApiKeyCredentialMappingsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// Per-user-group credential mappings (relational). Applied after per-API-key mappings and
    /// before <see cref="DefaultCredentialRef"/>: a caller belonging to a mapped user group uses
    /// that group's credential. See <see cref="McpServerUserGroupCredentialEntity"/>.
    /// </summary>
    public ICollection<McpServerUserGroupCredentialEntity> UserGroupCredentials { get; set; }
        = new List<McpServerUserGroupCredentialEntity>();
}
