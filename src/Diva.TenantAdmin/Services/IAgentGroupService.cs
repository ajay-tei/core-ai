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
    Task<AgentGroupEntity> CreateAsync(int tenantId, AgentGroupDto dto, CancellationToken ct);
    Task<AgentGroupEntity?> UpdateAsync(int tenantId, string id, AgentGroupDto dto, CancellationToken ct);
    Task<bool> DeleteAsync(int tenantId, string id, CancellationToken ct);

    /// <summary>
    /// Returns true if the given tenant context is allowed to invoke the agent.
    /// Agents not in any *restricted* group are always allowed (backward compatible).
    /// </summary>
    Task<bool> CanInvokeAgentAsync(string agentId, TenantContext tenant, CancellationToken ct);

    /// <summary>
    /// Returns the set of agent IDs the tenant context is NOT allowed to invoke
    /// (i.e. restricted agents the user/key has no grant for). Used to filter listings.
    /// </summary>
    Task<HashSet<string>> GetDeniedAgentIdsAsync(TenantContext tenant, CancellationToken ct);

    /// <summary>
    /// Returns the set of user-group ids referenced by the agent's restricted access groups
    /// (union across all groups containing the agent). Empty when the agent has no user-group-based
    /// restriction. Used to constrain the shared-MCP credential group picker.
    /// </summary>
    Task<HashSet<int>> GetAllowedUserGroupIdsForAgentAsync(string agentId, int tenantId, CancellationToken ct);

    /// <summary>Drops the cached restricted-group map for a tenant after writes.</summary>
    void InvalidateForTenant(int tenantId);
}
