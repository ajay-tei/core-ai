namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// A tenant-scoped collection of agents with an optional access control list.
/// When a group has a non-empty allow-list (users or roles), its member agents
/// become "restricted": only granted users/roles (or API keys with an explicit
/// group grant) may invoke them. A group with empty allow-lists imposes no
/// restriction (agents remain open to all tenant users).
///
/// Distinct from <see cref="TenantGroupEntity"/> (which groups *tenants* to share
/// agent templates). This groups *agents* within a single tenant for authorization.
/// </summary>
public class AgentGroupEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }

    /// <summary>Human-readable display name.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>JSON array of member agent IDs (AgentDefinition.Id).</summary>
    public string? AgentIdsJson { get; set; }

    /// <summary>JSON array of user identifiers (UserId and/or email) granted access.</summary>
    public string? AllowedUserIdsJson { get; set; }

    /// <summary>JSON array of roles / SSO groups granted access.</summary>
    public string? AllowedRolesJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
