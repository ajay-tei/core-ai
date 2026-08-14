using Diva.Core.Models;
using Diva.Host.Controllers;
using Diva.Infrastructure.Data.Entities;

namespace Diva.Agents.Tests;

/// <summary>
/// Pure-function unit tests for AgentsController's environment-scoping and sub-agent-hiding
/// helpers (internal static, exposed to this assembly via InternalsVisibleTo) — no DB/HTTP
/// harness needed since none of these three methods touch the database.
/// </summary>
public class AgentsControllerScopingTests
{
    private static TenantContext Ctx(bool isAdmin = false, bool isMasterAdmin = false, int environmentId = 0) => new()
    {
        TenantId = isMasterAdmin ? 0 : 1,
        UserRoles = isMasterAdmin ? ["master_admin"] : isAdmin ? ["admin"] : ["viewer"],
        EnvironmentId = environmentId,
    };

    // ── EffectiveEnvironmentIdForList ────────────────────────────────────────

    [Fact]
    public void EffectiveEnvironmentIdForList_Admin_RequestedId_PassesThroughUnchanged()
    {
        var tenant = Ctx(isAdmin: true, environmentId: 5);
        Assert.Equal(7, AgentsController.EffectiveEnvironmentIdForList(tenant, requestedEnvironmentId: 7));
    }

    [Fact]
    public void EffectiveEnvironmentIdForList_Admin_NullRequested_StaysNull()
    {
        var tenant = Ctx(isAdmin: true, environmentId: 5);
        Assert.Null(AgentsController.EffectiveEnvironmentIdForList(tenant, requestedEnvironmentId: null));
    }

    [Fact]
    public void EffectiveEnvironmentIdForList_MasterAdmin_RequestedId_PassesThroughUnchanged()
    {
        var tenant = Ctx(isMasterAdmin: true, environmentId: 5);
        Assert.Equal(7, AgentsController.EffectiveEnvironmentIdForList(tenant, requestedEnvironmentId: 7));
    }

    [Fact]
    public void EffectiveEnvironmentIdForList_NonAdmin_IgnoresRequestedId_AlwaysUsesTenantEnvironmentId()
    {
        var tenant = Ctx(isAdmin: false, environmentId: 5);
        // 999 simulates a spoofed/crafted query param — must never win over the resolved value.
        Assert.Equal(5, AgentsController.EffectiveEnvironmentIdForList(tenant, requestedEnvironmentId: 999));
    }

    // ── IsOutsideCallersEnvironment ───────────────────────────────────────────

    [Fact]
    public void IsOutsideCallersEnvironment_Admin_AlwaysFalse_RegardlessOfMismatch()
    {
        var tenant = Ctx(isAdmin: true, environmentId: 1);
        var agent = new AgentDefinitionEntity { EnvironmentId = 99 };
        Assert.False(AgentsController.IsOutsideCallersEnvironment(agent, tenant));
    }

    [Fact]
    public void IsOutsideCallersEnvironment_NonAdmin_UntaggedAgent_False()
    {
        var tenant = Ctx(isAdmin: false, environmentId: 1);
        var agent = new AgentDefinitionEntity { EnvironmentId = null };
        Assert.False(AgentsController.IsOutsideCallersEnvironment(agent, tenant));
    }

    [Fact]
    public void IsOutsideCallersEnvironment_NonAdmin_MatchingEnvironment_False()
    {
        var tenant = Ctx(isAdmin: false, environmentId: 1);
        var agent = new AgentDefinitionEntity { EnvironmentId = 1 };
        Assert.False(AgentsController.IsOutsideCallersEnvironment(agent, tenant));
    }

    [Fact]
    public void IsOutsideCallersEnvironment_NonAdmin_MismatchedEnvironment_True()
    {
        var tenant = Ctx(isAdmin: false, environmentId: 1);
        var agent = new AgentDefinitionEntity { EnvironmentId = 2 };
        Assert.True(AgentsController.IsOutsideCallersEnvironment(agent, tenant));
    }

    // ── ComputeSubAgentIds ────────────────────────────────────────────────────

    [Fact]
    public void ComputeSubAgentIds_EmptyInput_ReturnsEmptySet()
    {
        Assert.Empty(AgentsController.ComputeSubAgentIds([]));
    }

    [Fact]
    public void ComputeSubAgentIds_OneBlob_ReturnsItsIds()
    {
        var result = AgentsController.ComputeSubAgentIds(["[\"b\",\"c\"]"]);
        Assert.Equal(new HashSet<string> { "b", "c" }, result);
    }

    [Fact]
    public void ComputeSubAgentIds_MultipleBlobs_UnionsAll()
    {
        var result = AgentsController.ComputeSubAgentIds(["[\"a\"]", "[\"b\",\"c\"]"]);
        Assert.Equal(new HashSet<string> { "a", "b", "c" }, result);
    }

    [Fact]
    public void ComputeSubAgentIds_MalformedBlobMixedWithValid_SkipsMalformedOnly()
    {
        var result = AgentsController.ComputeSubAgentIds(["not valid json", "[\"a\"]"]);
        Assert.Equal(new HashSet<string> { "a" }, result);
    }

    [Fact]
    public void ComputeSubAgentIds_EmptyArrayBlob_ReturnsEmptySet_NoThrow()
    {
        Assert.Empty(AgentsController.ComputeSubAgentIds(["[]"]));
    }
}
