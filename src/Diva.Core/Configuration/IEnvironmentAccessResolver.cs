using Diva.Core.Models;

namespace Diva.Core.Configuration;

/// <summary>
/// Resolves which of a tenant's environments a caller may access, based on each
/// TenantEnvironmentEntity's optional AllowedRolesJson / linked user groups. An
/// environment with neither configured is open to every tenant user (backward
/// compatible). Admins/master-admins always have access to everything.
///
/// Used by TenantContextMiddleware to validate a non-admin's X-Environment header and
/// by EnvironmentAccessController to compute a caller's accessible environment list.
/// </summary>
public interface IEnvironmentAccessResolver
{
    Task<bool> CanAccessEnvironmentAsync(int environmentId, TenantContext tenant, CancellationToken ct);

    /// <summary>Ids of every one of the tenant's environments the caller may access.</summary>
    Task<IReadOnlyCollection<int>> GetAccessibleEnvironmentIdsAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>Resolves the best environment id for a caller with no explicit environment request:
    /// the tenant's default if the caller is granted access to it (or unconditionally for admins),
    /// else the caller's lowest-Rank accessible environment, else 0 (a non-admin with no accessible
    /// environment at all).</summary>
    Task<int> ResolveEffectiveEnvironmentIdAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>Evicts the cached access rules for a tenant after a write.</summary>
    void InvalidateForTenant(int tenantId);
}
