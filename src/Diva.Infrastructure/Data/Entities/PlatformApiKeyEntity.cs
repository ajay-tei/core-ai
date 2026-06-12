namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-scoped platform API key. Enables non-SSO callers (external systems,
/// scheduled tasks, CI/CD) to authenticate against the Diva API.
/// The raw key is never stored — only a SHA-256 hash.
/// </summary>
public class PlatformApiKeyEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the full key (hex-encoded, lowercase).</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>First 8 chars of the key for identification in list views.</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Access scope: "admin", "invoke", or "readonly".</summary>
    public string Scope { get; set; } = "invoke";

    /// <summary>
    /// Optional JSON array of agent IDs this key can invoke.
    /// Null or empty = all agents accessible.
    /// </summary>
    public string? AllowedAgentIdsJson { get; set; }

    /// <summary>
    /// Optional JSON array of agent-group IDs this key is explicitly granted.
    /// Used to authorize access to access-restricted (grouped) agents, since an
    /// API key has no real user/role identity. Null or empty = no group grants.
    /// </summary>
    public string? AllowedGroupIdsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastUsedAt { get; set; }
    public string? CreatedByUserId { get; set; }
}
