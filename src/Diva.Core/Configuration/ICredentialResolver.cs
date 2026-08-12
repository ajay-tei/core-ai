namespace Diva.Core.Configuration;

/// <summary>
/// Resolved credential ready for injection into HTTP headers.
/// </summary>
public sealed record ResolvedCredential(
    string ApiKey,
    string AuthScheme,
    string? CustomHeaderName);

/// <summary>
/// Resolves tenant-scoped credential references (by name) into decrypted API keys.
/// </summary>
public interface ICredentialResolver
{
    /// <summary>
    /// Looks up a named credential for the given tenant, decrypts the key,
    /// and validates it is active and not expired.
    /// Returns null if the credential does not exist or is invalid.
    /// </summary>
    /// <param name="environmentId">The requesting TenantContext's environment (Phase E). Required —
    /// deliberately not optional, mirroring Phase G's LLM-config resolver, so a missed call site is
    /// a compile error rather than a silent wrong-environment secret. 0 = wildcard/no scoping.
    /// Resolution prefers a row tagged to this exact environment, falling back to an untagged
    /// (EnvironmentId == null) row — never a different, specifically-tagged environment's row.</param>
    Task<ResolvedCredential?> ResolveAsync(int tenantId, string credentialName, int environmentId, CancellationToken ct);

    /// <summary>
    /// Evicts every cached resolution of this credential (across all of the tenant's environments,
    /// plus the unscoped/wildcard slot) so an updated value, rotated key, deactivation, or deletion
    /// takes effect immediately instead of silently continuing to serve the old value for up to the
    /// cache TTL. Call after any write to a credential's EncryptedApiKey, IsActive, ExpiresAt, or
    /// EnvironmentId.
    /// </summary>
    Task InvalidateAsync(int tenantId, string credentialName, CancellationToken ct);
}
