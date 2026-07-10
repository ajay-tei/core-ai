using Diva.Core.Models;

namespace Diva.Core.Configuration;

/// <summary>
/// Resolves which tenant-scoped user groups a caller belongs to, based on their
/// identity in <see cref="TenantContext"/> (user id / email / roles / SSO groups).
///
/// Used on the hot invoke path to (a) evaluate agent-access-group grants and
/// (b) select shared MCP server credentials for non-API-key callers, so
/// implementations are expected to cache the per-tenant membership rules.
/// </summary>
public interface IUserGroupResolver
{
    /// <summary>
    /// Returns the ids of every user group the caller matches, in ascending id order
    /// (so the oldest group wins any downstream tie-break). Empty when the caller
    /// matches no group or has no identity.
    /// </summary>
    Task<IReadOnlyCollection<int>> GetGroupIdsForUserAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>Evicts the cached membership rules for a tenant after a write.</summary>
    void Invalidate(int tenantId);
}
