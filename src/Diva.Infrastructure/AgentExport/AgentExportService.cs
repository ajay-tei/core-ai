namespace Diva.Infrastructure.AgentExport;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <see cref="IAgentExportService"/>: exports a full agent configuration bundle
/// (definition + linked business rules) and imports it back into any tenant.
/// </summary>
public sealed class AgentExportService : IAgentExportService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<AgentExportService> _logger;

    public AgentExportService(IDatabaseProviderFactory db, ILogger<AgentExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Export ────────────────────────────────────────────────────────────────

    public async Task<AgentExportBundle> ExportAsync(
        string agentId,
        TenantContext tenant,
        CancellationToken ct)
    {
        using var db = _db.CreateDbContext(tenant);

        var agent = await db.AgentDefinitions.FindAsync([agentId], ct)
            ?? throw new InvalidOperationException($"Agent '{agentId}' not found.");

        var rules = await db.BusinessRules
            .Where(r => r.AgentId == agentId)
            .OrderBy(r => r.Priority)
            .ToListAsync(ct);

        // Resolve delegate agent names so they can be matched by name on import
        var delegateNames = await ResolveDelegateNamesAsync(db, agent.DelegateAgentIdsJson, ct);

        return new AgentExportBundle
        {
            SchemaVersion = "1.0",
            ExportedAt = DateTime.UtcNow,
            SourceTenantId = tenant.TenantId,
            Agent = MapAgent(agent, delegateNames),
            Rules = rules.Select(MapRule).ToList().AsReadOnly(),
        };
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<AgentImportResult> ImportAsync(
        AgentExportBundle bundle,
        TenantContext tenant,
        AgentImportOptions options,
        CancellationToken ct)
    {
        if (bundle.Agent is null) throw new ArgumentException("Bundle contains no agent definition.");

        using var db = _db.CreateDbContext(tenant);

        var warnings = new List<string>();
        var effectiveName = options.NewAgentName?.Trim() is { Length: > 0 } n
            ? n
            : bundle.Agent.Name;

        // Resolve delegate agent IDs from names in the target tenant — scoped to the target
        // environment when known (promotion), so a delegate whose Name exists in multiple
        // environments resolves to THIS agent's own environment's copy, not an arbitrary one.
        var delegateIdsJson = await ResolveDelegateIdsJsonAsync(
            db, bundle.Agent.DelegateAgentNames, options.DelegateEnvironmentId, warnings, ct);

        // Overwrite or create
        AgentDefinitionEntity existing = options.TargetAgentId is { Length: > 0 } targetId
            ? await db.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == targetId, ct) ?? new AgentDefinitionEntity()
            : options.OverwriteExisting
                ? await db.AgentDefinitions.FirstOrDefaultAsync(a => a.Name == effectiveName, ct) ?? new AgentDefinitionEntity()
                : new AgentDefinitionEntity();

        var isNew = string.IsNullOrEmpty(existing.Id) || existing.Id == existing.Id && existing.TenantId == 0;

        ApplyAgentFields(existing, bundle.Agent, effectiveName, tenant.TenantId, delegateIdsJson, isNew);

        if (isNew)
        {
            existing.Id = Guid.NewGuid().ToString();
            existing.TenantId = tenant.TenantId;
            existing.CreatedAt = DateTime.UtcNow;
            existing.Version = 1;
            existing.LogicalId = Guid.NewGuid();
            // Imported agents always land in the tenant's DEFAULT environment, never whichever
            // environment the importing admin currently has selected — an imported bundle has no
            // environment context of its own, so landing it somewhere predictable (for review/
            // promotion afterward) is safer than silently inheriting the caller's current tab.
            // Promotion (AgentSnapshotSerializer.MaterializeAsync) overwrites this immediately
            // after the call with the actual target environment, so this default is only ever
            // "final" for a direct admin-portal import.
            existing.EnvironmentId = await db.TenantEnvironments
                .Where(e => e.TenantId == tenant.TenantId && e.IsDefault)
                .Select(e => (int?)e.Id)
                .FirstOrDefaultAsync(ct);
            db.AgentDefinitions.Add(existing);
        }
        else
        {
            existing.Version++;
        }

        await db.SaveChangesAsync(ct);

        // Import rules
        var rulesImported = 0;
        if (options.ImportRules && bundle.Rules.Count > 0)
        {
            rulesImported = await ImportRulesAsync(db, bundle.Rules, existing.Id, tenant.TenantId, existing.AgentType, ct);
        }

        _logger.LogInformation(
            "Imported agent '{Name}' (Id={Id}) for tenant {TenantId}. Rules={Rules}, Warnings={Warnings}",
            existing.Name, existing.Id, tenant.TenantId, rulesImported, warnings.Count);

        return new AgentImportResult
        {
            AgentId = existing.Id,
            AgentName = existing.Name,
            RulesImported = rulesImported,
            Warnings = warnings.AsReadOnly(),
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static AgentExportDefinition MapAgent(
        AgentDefinitionEntity a,
        IReadOnlyList<string> delegateNames) => new()
        {
            Name = a.Name,
            DisplayName = a.DisplayName,
            Description = a.Description,
            AgentType = a.AgentType,
            SystemPrompt = a.SystemPrompt,
            ModelId = a.ModelId,
            Temperature = a.Temperature,
            MaxIterations = a.MaxIterations,
            Capabilities = a.Capabilities,
            ToolBindings = a.ToolBindings,
            McpServerRefsJson = a.McpServerRefsJson,
            ConversationStartersJson = a.ConversationStartersJson,
            VerificationMode = a.VerificationMode,
            ContextWindowJson = a.ContextWindowJson,
            OptimizationOverrideJson = a.OptimizationOverrideJson,
            CustomVariablesJson = a.CustomVariablesJson,
            MaxContinuations = a.MaxContinuations,
            MaxToolResultChars = a.MaxToolResultChars,
            MaxOutputTokens = a.MaxOutputTokens,
            EnableHistoryCaching = a.EnableHistoryCaching,
            EnableExtendedThinking = a.EnableExtendedThinking,
            ThinkingBudgetTokens = a.ThinkingBudgetTokens,
            PipelineStagesJson = a.PipelineStagesJson,
            ToolFilterJson = a.ToolFilterJson,
            StageInstructionsJson = a.StageInstructionsJson,
            ArchetypeId = a.ArchetypeId,
            HooksJson = a.HooksJson,
            A2AEndpoint = a.A2AEndpoint,
            A2AAuthScheme = a.A2AAuthScheme,
            A2ASecretRef = a.A2ASecretRef,
            A2ARemoteAgentId = a.A2ARemoteAgentId,
            ExecutionMode = a.ExecutionMode,
            ModelSwitchingJson = a.ModelSwitchingJson,
            IsEnabled = a.IsEnabled,
            Status = a.Status,
            DelegateAgentNames = delegateNames,
        };

    private static AgentExportRule MapRule(TenantBusinessRuleEntity r) => new()
    {
        AgentType = r.AgentType,
        RuleCategory = r.RuleCategory,
        RuleKey = r.RuleKey,
        RuleValueJson = r.RuleValueJson,
        PromptInjection = r.PromptInjection,
        IsActive = r.IsActive,
        Priority = r.Priority,
        HookPoint = r.HookPoint,
        HookRuleType = r.HookRuleType,
        Pattern = r.Pattern,
        Replacement = r.Replacement,
        ToolName = r.ToolName,
        OrderInPack = r.OrderInPack,
        StopOnMatch = r.StopOnMatch,
        MaxEvaluationMs = r.MaxEvaluationMs,
    };

    private static void ApplyAgentFields(
        AgentDefinitionEntity target,
        AgentExportDefinition src,
        string effectiveName,
        int tenantId,
        string? delegateIdsJson,
        bool isNewRow)
    {
        target.Name = effectiveName;
        target.DisplayName = src.DisplayName;
        target.Description = src.Description;
        target.AgentType = src.AgentType;
        target.SystemPrompt = src.SystemPrompt;
        target.ModelId = src.ModelId;
        target.Temperature = src.Temperature;
        target.MaxIterations = src.MaxIterations;
        target.Capabilities = src.Capabilities;
        target.ToolBindings = src.ToolBindings;
        target.McpServerRefsJson = src.McpServerRefsJson;
        target.ConversationStartersJson = src.ConversationStartersJson;
        target.VerificationMode = src.VerificationMode;
        target.ContextWindowJson = src.ContextWindowJson;
        target.OptimizationOverrideJson = src.OptimizationOverrideJson;
        // Custom variables are environment-specific customization (e.g. company_name/disclaimer
        // text may legitimately differ per environment) — like LlmConfigId, they're editable
        // directly on an already-promoted agent (PUT /api/agents/{id}/custom-variables) and must
        // not be silently reset by a later re-promotion. Only a brand-new row (first promotion)
        // inherits the source's starting value.
        if (isNewRow)
        {
            target.CustomVariablesJson = src.CustomVariablesJson;
        }
        target.MaxContinuations = src.MaxContinuations;
        target.MaxToolResultChars = src.MaxToolResultChars;
        target.MaxOutputTokens = src.MaxOutputTokens;
        target.EnableHistoryCaching = src.EnableHistoryCaching;
        target.EnableExtendedThinking = src.EnableExtendedThinking;
        target.ThinkingBudgetTokens = src.ThinkingBudgetTokens;
        target.PipelineStagesJson = src.PipelineStagesJson;
        target.ToolFilterJson = src.ToolFilterJson;
        target.StageInstructionsJson = src.StageInstructionsJson;
        target.ArchetypeId = src.ArchetypeId;
        target.HooksJson = src.HooksJson;
        target.A2AEndpoint = src.A2AEndpoint;
        target.A2AAuthScheme = src.A2AAuthScheme;
        target.A2ASecretRef = src.A2ASecretRef;
        target.A2ARemoteAgentId = src.A2ARemoteAgentId;
        target.ExecutionMode = src.ExecutionMode;
        target.ModelSwitchingJson = src.ModelSwitchingJson;
        target.IsEnabled = src.IsEnabled;
        target.Status = src.Status;
        target.DelegateAgentIdsJson = delegateIdsJson;
        target.TenantId = tenantId;
    }

    private static async Task<int> ImportRulesAsync(
        DivaDbContext db,
        IReadOnlyList<AgentExportRule> rules,
        string agentId,
        int tenantId,
        string agentType,
        CancellationToken ct)
    {
        foreach (var r in rules)
        {
            db.BusinessRules.Add(new TenantBusinessRuleEntity
            {
                Guid = Guid.NewGuid().ToString(),
                TenantId = tenantId,
                AgentId = agentId,
                AgentType = r.AgentType != "*" ? r.AgentType : agentType,
                RuleCategory = r.RuleCategory,
                RuleKey = r.RuleKey,
                RuleValueJson = r.RuleValueJson,
                PromptInjection = r.PromptInjection,
                IsActive = r.IsActive,
                Priority = r.Priority,
                HookPoint = r.HookPoint,
                HookRuleType = r.HookRuleType,
                Pattern = r.Pattern,
                Replacement = r.Replacement,
                ToolName = r.ToolName,
                OrderInPack = r.OrderInPack,
                StopOnMatch = r.StopOnMatch,
                MaxEvaluationMs = r.MaxEvaluationMs,
                CreatedAt = DateTime.UtcNow,
                // RulePackId intentionally omitted — imported rules are standalone
            });
        }

        await db.SaveChangesAsync(ct);
        return rules.Count;
    }

    private static async Task<IReadOnlyList<string>> ResolveDelegateNamesAsync(
        DivaDbContext db,
        string? delegateIdsJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(delegateIdsJson) || delegateIdsJson.Trim() == "[]")
            return [];

        List<string>? ids;
        try { ids = JsonSerializer.Deserialize<List<string>>(delegateIdsJson); }
        catch { return []; }

        if (ids is not { Count: > 0 }) return [];

        var names = await db.AgentDefinitions
            .Where(a => ids.Contains(a.Id))
            .Select(a => a.Name)
            .ToListAsync(ct);

        return names.AsReadOnly();
    }

    private static async Task<string?> ResolveDelegateIdsJsonAsync(
        DivaDbContext db,
        IReadOnlyList<string> names,
        int? environmentId,
        List<string> warnings,
        CancellationToken ct)
    {
        if (names.Count == 0) return null;

        var nameList = names.ToList();
        var candidatesQuery = db.AgentDefinitions.Where(a => nameList.Contains(a.Name));
        if (environmentId is { } envId)
        {
            candidatesQuery = candidatesQuery.Where(a => a.EnvironmentId == envId || a.EnvironmentId == null);
        }
        var candidates = await candidatesQuery
            .Select(a => new { a.Id, a.Name, a.EnvironmentId })
            .ToListAsync(ct);

        // Even when scoped, prefer an exact environment match over an untagged (legacy) row if
        // both happen to exist for the same Name — avoids picking the wrong one arbitrarily.
        var found = candidates
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => environmentId is { } eid
                ? g.OrderByDescending(c => c.EnvironmentId == eid).First()
                : g.First())
            .ToList();

        var foundNames = found.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in nameList.Where(n => !foundNames.Contains(n)))
            warnings.Add($"Delegate agent '{name}' not found in this tenant — skipped.");

        if (found.Count == 0) return null;

        return JsonSerializer.Serialize(found.Select(f => f.Id).ToList());
    }
}
