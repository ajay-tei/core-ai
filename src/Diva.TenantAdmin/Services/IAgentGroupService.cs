using Diva.Core.Models;
using Diva.Infrastructure.Data.Entities;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// DTO for creating/updating an agent access group.
/// </summary>
public sealed record AgentGroupDto(
    string Name,
    string? Description,
    string[] AgentIds,
    string[] AllowedUserIds,
    string[] AllowedRoles,
    int[] AllowedUserGroupIds);

/// <summary>
/// Manages tenant-scoped agent access groups and evaluates per-user / per-role
/// authorization for invoking grouped (access-restricted) agents.
///
/// Distinct from <see cref="ITenantGroupService"/> (cross-tenant template sharing).
/// </summary>
public interface IAgentGroupService
{
    Task<List<AgentGroupEntity>> ListAsync(int tenantId, CancellationToken ct);
    Task<AgentGroupEntity?> GetAsync(int tenantId, string id, CancellationToken ct);
    Task<AgentGroupEntity> CreateAsync(int tenantId, AgentGroupDto dto, int? environmentId, CancellationToken ct);
    Task<AgentGroupEntity?> UpdateAsync(int tenantId, string id, AgentGroupDto dto, CancellationToken ct);
    Task<bool> DeleteAsync(int tenantId, string id, CancellationToken ct);

    /// <summary>
    /// Returns true if the given tenant context is allowed to invoke the agent. Allow-list only:
    /// an agent that belongs to no access group at all, or whose group(s) grant nobody (empty
    /// allow-lists), is invisible to everyone except admins/master-admins (who always bypass this).
    /// </summary>
    Task<bool> CanInvokeAgentAsync(string agentId, TenantContext tenant, CancellationToken ct);

    /// <summary>
    /// Returns the subset of <paramref name="candidateAgentIds"/> the tenant context is NOT allowed
    /// to invoke — allow-list only, so this includes agents with no access group at all, not just
    /// ones whose group(s) explicitly deny them. Used to filter listings. Always empty for admins.
    /// </summary>
    Task<HashSet<string>> GetDeniedAgentIdsAsync(IEnumerable<string> candidateAgentIds, TenantContext tenant, CancellationToken ct);

    /// <summary>
    /// Returns the set of user-group ids referenced by the agent's restricted access groups
    /// (union across all groups containing the agent). Empty when the agent has no user-group-based
    /// restriction. Used to constrain the shared-MCP credential group picker.
    /// </summary>
    Task<HashSet<int>> GetAllowedUserGroupIdsForAgentAsync(string agentId, int tenantId, CancellationToken ct);

    /// <summary>Drops the cached restricted-group map for a tenant after writes.</summary>
    void InvalidateForTenant(int tenantId);
}
