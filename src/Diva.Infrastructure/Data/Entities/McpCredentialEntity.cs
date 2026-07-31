namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-scoped credential vault entry. Stores an encrypted API key and its
/// authentication scheme for use with MCP tool bindings and A2A agent calls.
/// </summary>
public class McpCredentialEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Unique name within the tenant, referenced by McpToolBinding.CredentialRef.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>AES-256-GCM encrypted API key (base64: nonce + ciphertext + tag).</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>How the key is sent: "Bearer", "ApiKey" (X-API-Key header), or "Custom".</summary>
    public string AuthScheme { get; set; } = "Bearer";

    /// <summary>Header name when AuthScheme = "Custom", e.g. "X-My-Service-Key".</summary>
    public string? CustomHeaderName { get; set; }

    /// <summary>Human-readable description (e.g. "Weather API prod key").</summary>
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastUsedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>FK to TenantEnvironmentEntity — which environment this credential's value applies
    /// to (Phase I). Null = untagged (resolves for any environment until tagged). Values never
    /// travel with promotion — an agent/MCP server promoted to a new environment must have its own
    /// tagged credential configured there first.</summary>
    public int? EnvironmentId { get; set; }
}
