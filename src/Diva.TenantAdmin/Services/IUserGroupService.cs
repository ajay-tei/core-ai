using Diva.Infrastructure.Data.Entities;

namespace Diva.TenantAdmin.Services;

/// <summary>Explicit user member of a user group (id and/or email).</summary>
public sealed record UserGroupMemberDto(string UserId, string? Email);

/// <summary>DTO for creating/updating a user group.</summary>
public sealed record UserGroupDto(
    string Name,
    string? Description,
    IReadOnlyList<UserGroupMemberDto> Members,
    IReadOnlyList<string> Roles);

/// <summary>
/// Manages tenant-scoped user groups (a user may belong to many groups). Membership is
/// stored relationally as member rows (explicit user id / email) and role rows
/// (SSO role / group auto-include).
///
/// Distinct from <see cref="IAgentGroupService"/> (grants agents to user groups) and
/// <see cref="ITenantGroupService"/> (cross-tenant template sharing).
/// </summary>
public interface IUserGroupService
{
    Task<List<UserGroupEntity>> ListAsync(int tenantId, CancellationToken ct);
    Task<UserGroupEntity?> GetAsync(int tenantId, int id, CancellationToken ct);
    Task<UserGroupEntity> CreateAsync(int tenantId, UserGroupDto dto, string? createdByUserId, CancellationToken ct);
    Task<UserGroupEntity?> UpdateAsync(int tenantId, int id, UserGroupDto dto, CancellationToken ct);
    Task<bool> DeleteAsync(int tenantId, int id, CancellationToken ct);
}
