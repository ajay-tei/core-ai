using System.Security.Claims;
using Diva.Core.Configuration;
using Diva.Infrastructure.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Verifies that the SSO claim mapping correctly extracts both roles and groups,
/// supporting JSON-array and CSV claim formats, and that roles and groups are
/// retained independently (Phase 28).
/// </summary>
public class TenantClaimsExtractorGroupTests
{
    private static TenantClaimsExtractor Build(ClaimMappingsOptions? mappings = null)
    {
        var options = new OAuthOptions { ClaimMappings = mappings ?? new ClaimMappingsOptions() };
        return new TenantClaimsExtractor(Options.Create(options), NullLogger<TenantClaimsExtractor>.Instance);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value))));

    [Fact]
    public void Extract_GroupsClaim_JsonArray_Parsed()
    {
        var principal = Principal(("groups", "[\"FinanceDept\",\"Admins\"]"));
        var ctx = Build().Extract(principal, null, null);

        Assert.Equal(["FinanceDept", "Admins"], ctx.UserGroups);
    }

    [Fact]
    public void Extract_GroupsClaim_Csv_Parsed()
    {
        var principal = Principal(("groups", "FinanceDept, Admins"));
        var ctx = Build().Extract(principal, null, null);

        Assert.Equal(["FinanceDept", "Admins"], ctx.UserGroups);
    }

    [Fact]
    public void Extract_RolesAndGroups_RetainedIndependently()
    {
        var principal = Principal(
            ("roles", "[\"admin\",\"finance\"]"),
            ("groups", "[\"FinanceDept\"]"));
        var ctx = Build().Extract(principal, null, null);

        Assert.Equal(["admin", "finance"], ctx.UserRoles);
        Assert.Equal(["FinanceDept"], ctx.UserGroups);
    }

    [Fact]
    public void Extract_NoGroupsClaim_EmptyGroups_BackCompat()
    {
        var principal = Principal(("roles", "admin"));
        var ctx = Build().Extract(principal, null, null);

        Assert.Empty(ctx.UserGroups);
        Assert.Equal(["admin"], ctx.UserRoles);
    }

    [Fact]
    public void Extract_CustomGroupsClaimName_Honored()
    {
        var mappings = new ClaimMappingsOptions { Groups = "wids" };
        var principal = Principal(("wids", "[\"G1\"]"));
        var ctx = Build(mappings).Extract(principal, null, null);

        Assert.Equal(["G1"], ctx.UserGroups);
    }
}
