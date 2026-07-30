namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Cascade dependencies for an Agent: its referenced MCP servers (McpServerRefsJson, name-based)
/// and delegate agents (DelegateAgentIdsJson, id-based) — both promoted alongside it automatically.
/// No forward (validation-only) dependencies for Agents.
/// </summary>
public sealed class AgentPromotionDependencyResolver : IPromotionDependencyResolver
{
    private readonly IDatabaseProviderFactory _db;

    public string ObjectType => "Agent";

    public AgentPromotionDependencyResolver(IDatabaseProviderFactory db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<PromotableDependency>> GetCascadeDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var agent = await db.AgentDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.LogicalId == logicalId, ct);
        if (agent is null)
        {
            return [];
        }

        var deps = new List<PromotableDependency>();

        // ── MCP server refs (name-based) ───────────────────────────────────
        var mcpNames = ParseStringArray(agent.McpServerRefsJson);
        if (mcpNames.Count > 0)
        {
            var servers = await db.TenantMcpServers.AsNoTracking()
                .Where(s => s.TenantId == tenantId && mcpNames.Contains(s.Name) && s.LogicalId != null)
                .Select(s => new { s.Name, s.LogicalId })
                .ToListAsync(ct);
            deps.AddRange(servers.Select(s => new PromotableDependency("McpServer", s.LogicalId!.Value, s.Name)));
        }

        // ── Delegate agents (id-based) ─────────────────────────────────────
        var delegateIds = ParseStringArray(agent.DelegateAgentIdsJson);
        if (delegateIds.Count > 0)
        {
            var delegates = await db.AgentDefinitions.AsNoTracking()
                .Where(a => a.TenantId == tenantId && delegateIds.Contains(a.Id) && a.LogicalId != null)
                .Select(a => new { a.Name, a.LogicalId })
                .ToListAsync(ct);
            deps.AddRange(delegates.Select(a => new PromotableDependency("Agent", a.LogicalId!.Value, a.Name)));
        }

        return deps;
    }

    public Task<IReadOnlyList<PromotableDependency>> GetForwardDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);

    private static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>MCP servers are a leaf node in the dependency graph — no cascade or forward dependencies.</summary>
public sealed class McpServerPromotionDependencyResolver : IPromotionDependencyResolver
{
    public string ObjectType => "McpServer";

    public Task<IReadOnlyList<PromotableDependency>> GetCascadeDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);

    public Task<IReadOnlyList<PromotableDependency>> GetForwardDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);
}

/// <summary>
/// Forward (validation-only) dependency for a ScheduledTask: its Agent must already exist in the
/// target environment — never auto-cascaded (promoting a schedule doesn't drag its agent along;
/// the agent must be promoted first, on its own).
/// </summary>
public sealed class ScheduledTaskPromotionDependencyResolver : IPromotionDependencyResolver
{
    private readonly IDatabaseProviderFactory _db;

    public string ObjectType => "ScheduledTask";

    public ScheduledTaskPromotionDependencyResolver(IDatabaseProviderFactory db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<PromotableDependency>> GetCascadeDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);

    public async Task<IReadOnlyList<PromotableDependency>> GetForwardDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var task = await db.ScheduledTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.LogicalId == logicalId, ct);
        if (task is null)
        {
            return [];
        }

        var agent = await db.AgentDefinitions.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Id == task.AgentId && a.LogicalId != null)
            .Select(a => new { a.Name, a.LogicalId })
            .FirstOrDefaultAsync(ct);

        return agent is null ? [] : [new PromotableDependency("Agent", agent.LogicalId!.Value, agent.Name)];
    }
}

/// <summary>
/// Forward (validation-only) dependency for an AgentGroup: each member agent must already exist in
/// the target environment — never auto-cascaded.
/// </summary>
public sealed class AgentGroupPromotionDependencyResolver : IPromotionDependencyResolver
{
    private readonly IDatabaseProviderFactory _db;

    public string ObjectType => "AgentGroup";

    public AgentGroupPromotionDependencyResolver(IDatabaseProviderFactory db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<PromotableDependency>> GetCascadeDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PromotableDependency>>([]);

    public async Task<IReadOnlyList<PromotableDependency>> GetForwardDependenciesAsync(int tenantId, Guid logicalId, int environmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var group = await db.AgentGroups.AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.LogicalId == logicalId, ct);
        if (group is null || string.IsNullOrWhiteSpace(group.AgentIdsJson) || group.AgentIdsJson.Trim() == "[]")
        {
            return [];
        }

        List<string>? memberIds;
        try
        {
            memberIds = JsonSerializer.Deserialize<List<string>>(group.AgentIdsJson);
        }
        catch
        {
            return [];
        }

        if (memberIds is not { Count: > 0 })
        {
            return [];
        }

        var agents = await db.AgentDefinitions.AsNoTracking()
            .Where(a => a.TenantId == tenantId && memberIds.Contains(a.Id) && a.LogicalId != null)
            .Select(a => new { a.Name, a.LogicalId })
            .ToListAsync(ct);

        return agents.Select(a => new PromotableDependency("Agent", a.LogicalId!.Value, a.Name)).ToList();
    }
}
