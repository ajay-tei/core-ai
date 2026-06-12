using Diva.Core.Configuration;
using Diva.Infrastructure.Auth;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Verifies SSO role normalization, access-group resolution, and the default-user
/// fallback applied during the SSO callback (claim-mapping feature).
/// </summary>
public class SsoClaimMapperTests
{
    [Fact]
    public void Map_NoRoles_DefaultsToUser()
    {
        var result = SsoClaimMapper.Map(rawRoles: null, rawGroups: null, rawGroupAccess: null,
            mappings: new ClaimMappingsOptions(), useRoleMappings: false);

        Assert.Equal(["user"], result.Roles);
        Assert.Empty(result.GroupAccess);
    }

    [Fact]
    public void Map_EmptyRolesAndGroups_DefaultsToUser()
    {
        var result = SsoClaimMapper.Map(rawRoles: [], rawGroups: [], rawGroupAccess: [],
            mappings: new ClaimMappingsOptions(), useRoleMappings: true);

        Assert.Equal(["user"], result.Roles);
        Assert.Empty(result.GroupAccess);
    }

    [Fact]
    public void Map_RolesPassThrough_WhenRoleMappingsDisabled()
    {
        var mappings = new ClaimMappingsOptions
        {
            RoleMap = new(StringComparer.OrdinalIgnoreCase) { ["Diva-Admins"] = "admin" }
        };

        var result = SsoClaimMapper.Map(rawRoles: ["Diva-Admins"], rawGroups: null, rawGroupAccess: null,
            mappings: mappings, useRoleMappings: false);

        // Mapping disabled → raw value retained, NOT normalized to "admin".
        Assert.Equal(["Diva-Admins"], result.Roles);
    }

    [Fact]
    public void Map_RoleMap_NormalizesRoles_WhenEnabled()
    {
        var mappings = new ClaimMappingsOptions
        {
            RoleMap = new(StringComparer.OrdinalIgnoreCase) { ["Diva-Admins"] = "admin" }
        };

        var result = SsoClaimMapper.Map(rawRoles: ["Diva-Admins"], rawGroups: null, rawGroupAccess: null,
            mappings: mappings, useRoleMappings: true);

        Assert.Equal(["admin"], result.Roles);
    }

    [Fact]
    public void Map_RoleMap_NormalizesGroups_WhenEnabled()
    {
        var mappings = new ClaimMappingsOptions
        {
            RoleMap = new(StringComparer.OrdinalIgnoreCase) { ["FinanceDept"] = "viewer" }
        };

        var result = SsoClaimMapper.Map(rawRoles: null, rawGroups: ["FinanceDept"], rawGroupAccess: null,
            mappings: mappings, useRoleMappings: true);

        Assert.Equal(["viewer"], result.Roles);
    }

    [Fact]
    public void Map_RoleMap_IsCaseInsensitive()
    {
        var mappings = new ClaimMappingsOptions
        {
            // Force ordinal (case-sensitive) comparer to prove the mapper rebuilds it.
            RoleMap = new(StringComparer.Ordinal) { ["diva-admins"] = "admin" }
        };

        var result = SsoClaimMapper.Map(rawRoles: ["DIVA-ADMINS"], rawGroups: null, rawGroupAccess: null,
            mappings: mappings, useRoleMappings: true);

        Assert.Equal(["admin"], result.Roles);
    }

    [Fact]
    public void Map_UnmappedRoles_PassThrough_WhenEnabled()
    {
        var mappings = new ClaimMappingsOptions
        {
            RoleMap = new(StringComparer.OrdinalIgnoreCase) { ["Diva-Admins"] = "admin" }
        };

        var result = SsoClaimMapper.Map(rawRoles: ["analyst"], rawGroups: null, rawGroupAccess: null,
            mappings: mappings, useRoleMappings: true);

        Assert.Equal(["analyst"], result.Roles);
    }

    [Fact]
    public void Map_AccessGroupMap_ResolvesGroupIds_FromGroups()
    {
        var mappings = new ClaimMappingsOptions
        {
            AccessGroupMap = new(StringComparer.OrdinalIgnoreCase) { ["Sales-Team"] = ["sales-agents"] }
        };

        var result = SsoClaimMapper.Map(rawRoles: null, rawGroups: ["Sales-Team"], rawGroupAccess: null,
            mappings: mappings, useRoleMappings: false);

        Assert.Equal(["sales-agents"], result.GroupAccess);
    }

    [Fact]
    public void Map_AccessGroupMap_MergesWithExplicitGroupAccess_Deduplicated()
    {
        var mappings = new ClaimMappingsOptions
        {
            AccessGroupMap = new(StringComparer.OrdinalIgnoreCase) { ["Sales-Team"] = ["sales-agents", "shared"] }
        };

        var result = SsoClaimMapper.Map(
            rawRoles: null, rawGroups: ["Sales-Team"], rawGroupAccess: ["shared"],
            mappings: mappings, useRoleMappings: false);

        Assert.Equal(["shared", "sales-agents"], result.GroupAccess);
    }

    [Fact]
    public void Map_NoAccessGroupMatch_EmptyGroupAccess()
    {
        var mappings = new ClaimMappingsOptions
        {
            AccessGroupMap = new(StringComparer.OrdinalIgnoreCase) { ["Sales-Team"] = ["sales-agents"] }
        };

        var result = SsoClaimMapper.Map(rawRoles: ["user"], rawGroups: ["OtherGroup"], rawGroupAccess: null,
            mappings: mappings, useRoleMappings: false);

        Assert.Empty(result.GroupAccess);
    }

    [Fact]
    public void Map_DistinctRoles_RemovesDuplicates()
    {
        var mappings = new ClaimMappingsOptions
        {
            RoleMap = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Diva-Admins"] = "admin",
                ["Admin-Group"] = "admin"
            }
        };

        var result = SsoClaimMapper.Map(
            rawRoles: ["Diva-Admins"], rawGroups: ["Admin-Group"], rawGroupAccess: null,
            mappings: mappings, useRoleMappings: true);

        Assert.Equal(["admin"], result.Roles);
    }
}
