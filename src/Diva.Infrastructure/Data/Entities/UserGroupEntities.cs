namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-scoped group of users. A single user may belong to multiple groups.
/// Membership is defined relationally by <see cref="UserGroupMemberEntity"/> rows
/// (explicit user id / email) and/or <see cref="UserGroupRoleEntity"/> rows
/// (SSO role / group auto-include). Used to grant access to restricted
/// <see cref="AgentGroupEntity"/> agents and to select shared MCP server credentials
/// for non-API-key callers.
///
/// Distinct from <see cref="AgentGroupEntity"/> (groups AGENTS) and
/// <see cref="TenantGroupEntity"/> (groups TENANTS).
/// </summary>
public class UserGroupEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>Human-readable display name, unique within the tenant.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedByUserId { get; set; }

    /// <summary>Explicit user membership rows.</summary>
    public ICollection<UserGroupMemberEntity> Members { get; set; } = new List<UserGroupMemberEntity>();

    /// <summary>Role / SSO-group auto-include rows.</summary>
    public ICollection<UserGroupRoleEntity> Roles { get; set; } = new List<UserGroupRoleEntity>();
}

/// <summary>
/// Explicit user membership row for a <see cref="UserGroupEntity"/>.
/// A user matches when their JWT <c>sub</c> equals <see cref="UserId"/> OR their
/// email equals <see cref="Email"/> (case-insensitive).
/// </summary>
public class UserGroupMemberEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int UserGroupId { get; set; }
    public UserGroupEntity? Group { get; set; }

    /// <summary>JWT sub / user id. May be empty when only <see cref="Email"/> is known.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>User email. May be empty when only <see cref="UserId"/> is known.</summary>
    public string? Email { get; set; }
}

/// <summary>
/// Role / SSO-group auto-include rule for a <see cref="UserGroupEntity"/>.
/// Any user whose roles ∪ SSO groups contain <see cref="Role"/> is a member.
/// </summary>
public class UserGroupRoleEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int UserGroupId { get; set; }
    public UserGroupEntity? Group { get; set; }

    /// <summary>Role name or SSO group name; matched against the user's roles ∪ groups.</summary>
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// Junction granting an <see cref="AgentGroupEntity"/> (access-restricted set of agents)
/// to a <see cref="UserGroupEntity"/>. A user in the referenced user group may invoke the
/// agent group's member agents.
/// </summary>
public class AgentGroupUserGroupEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public string AgentGroupId { get; set; } = string.Empty;
    public AgentGroupEntity? AgentGroup { get; set; }

    public int UserGroupId { get; set; }
    public UserGroupEntity? UserGroup { get; set; }
}

/// <summary>
/// Per-user-group credential mapping for a shared <see cref="TenantMcpServerEntity"/>.
/// When a non-API-key caller invokes an agent using the referenced server, the effective
/// credential is chosen from the user's matching group. On a multi-group conflict the row
/// with the lowest <see cref="UserGroupId"/> (oldest group) wins.
/// </summary>
public class McpServerUserGroupCredentialEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    public int McpServerId { get; set; }
    public TenantMcpServerEntity? McpServer { get; set; }

    public int UserGroupId { get; set; }
    public UserGroupEntity? UserGroup { get; set; }

    /// <summary>References <see cref="McpCredentialEntity.Name"/> within the same tenant.</summary>
    public string CredentialRef { get; set; } = string.Empty;
}
