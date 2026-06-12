namespace Diva.Core.Configuration;

/// <summary>
/// Result of creating a platform API key. Contains the raw key (shown once).
/// </summary>
public sealed record ApiKeyCreatedResult(
    int Id,
    string Name,
    string KeyPrefix,
    string RawKey,
    string Scope,
    DateTime? ExpiresAt);

/// <summary>
/// Validated platform API key info (never contains the raw key).
/// </summary>
public sealed record ValidatedApiKey(
    int Id,
    int TenantId,
    string Name,
    string KeyPrefix,
    string Scope,
    string[]? AllowedAgentIds,
    string[]? AllowedGroupIds = null);

/// <summary>
/// Request to create a new platform API key.
/// </summary>
public sealed record CreateApiKeyRequest(
    string Name,
    string Scope,
    string[]? AllowedAgentIds,
    DateTime? ExpiresAt,
    string[]? AllowedGroupIds = null);

/// <summary>
/// Manages platform API keys for non-SSO authentication.
/// </summary>
public interface IPlatformApiKeyService
{
    /// <summary>Creates a new API key. Returns the result containing the raw key (shown once).</summary>
    Task<ApiKeyCreatedResult> CreateAsync(int tenantId, string userId, CreateApiKeyRequest request, CancellationToken ct);

    /// <summary>Validates a raw API key. Returns key info if valid, null if invalid/expired/revoked.</summary>
    Task<ValidatedApiKey?> ValidateAsync(string rawKey, CancellationToken ct);

    /// <summary>Lists all API keys for a tenant (never returns raw keys).</summary>
    Task<List<PlatformApiKeyInfo>> ListAsync(int tenantId, CancellationToken ct);

    /// <summary>Revokes (deactivates) an API key.</summary>
    Task RevokeAsync(int tenantId, int keyId, CancellationToken ct);

    /// <summary>Revokes the old key and creates a new one. Returns the new raw key (shown once).</summary>
    Task<ApiKeyCreatedResult> RotateAsync(int tenantId, int keyId, string userId, CancellationToken ct);
}

/// <summary>API key info for list views (no secrets).</summary>
public sealed record PlatformApiKeyInfo(
    int Id,
    string Name,
    string KeyPrefix,
    string Scope,
    string[]? AllowedAgentIds,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    bool IsActive,
    DateTime? LastUsedAt,
    string? CreatedByUserId,
    string[]? AllowedGroupIds = null);
