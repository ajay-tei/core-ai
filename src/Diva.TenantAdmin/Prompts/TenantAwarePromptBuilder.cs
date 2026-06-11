using System.Text;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Core.Prompts;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Learning;
using Diva.Infrastructure.Optimization;
using Diva.TenantAdmin.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.TenantAdmin.Prompts;

/// <summary>
/// Builds system prompts augmented with tenant business rules, session rules, and prompt overrides.
/// Also merges shared group rules and overrides when the tenant belongs to one or more groups.
/// Injection order (lowest → highest priority):
///   1. Group prompt overrides applied to base prompt first
///   2. Tenant prompt overrides on top (tenant wins)
///   3. ## Group Rules block  (Priority ≈ 50)
///   4. ## Business Rules block (tenant, Priority ≈ 100)
///   5. ## Session Rules block
/// Implements IPromptBuilder (defined in Diva.Core) — injected into AnthropicAgentRunner.
/// Singleton-safe: all dependencies use IDatabaseProviderFactory or IMemoryCache.
/// </summary>
public sealed class TenantAwarePromptBuilder : IPromptBuilder
{
    private readonly ITenantBusinessRulesService _rules;
    private readonly ISessionRuleManager _sessionRules;
    private readonly ITenantGroupService _groupService;
    private readonly IAgentOptimizationService _optimization;
    private readonly AgentOptions _opts;
    private readonly ILogger<TenantAwarePromptBuilder> _logger;

    public TenantAwarePromptBuilder(
        ITenantBusinessRulesService rules,
        ISessionRuleManager sessionRules,
        ITenantGroupService groupService,
        IAgentOptimizationService optimization,
        IOptions<AgentOptions> opts,
        ILogger<TenantAwarePromptBuilder> logger)
    {
        _rules = rules;
        _sessionRules = sessionRules;
        _groupService = groupService;
        _optimization = optimization;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<string> BuildAsync(
        string baseSystemPrompt,
        string agentType,
        TenantContext tenant,
        CancellationToken ct,
        string? customVariablesJson = null,
        string? agentId = null)
    {
        var parts = new List<string> { baseSystemPrompt };

        // Fetch all sources in parallel
        // NOTE: Tenant business rules are intentionally excluded here — they now flow through
        // TenantRulePackHook via RulePackEngine (BusinessRuleAdapter + virtual pack).
        var tenantOverridesTask = _rules.GetPromptOverridesAsync(tenant.TenantId, agentType, ct, agentId);
        var groupRulesTask = _groupService.GetActiveRulesForTenantAsync(tenant.TenantId, agentType, ct);
        var groupOverridesTask = _groupService.GetActiveOverridesForTenantAsync(tenant.TenantId, agentType, ct);
        var sessionRulesTask = tenant.SessionId is not null
            ? _sessionRules.GetSessionRulesAsync(tenant.SessionId, ct)
            : Task.FromResult(new List<SuggestedRule>());

        await Task.WhenAll(tenantOverridesTask, groupRulesTask, groupOverridesTask, sessionRulesTask);

        // 1. Apply group prompt overrides first (lower priority)
        var groupOverrides = groupOverridesTask.Result;
        if (groupOverrides.Count > 0)
            parts[0] = ApplyGroupOverrides(parts[0], groupOverrides);

        // 2. Apply tenant prompt overrides on top (tenant wins)
        var tenantOverrides = tenantOverridesTask.Result;
        if (tenantOverrides.Count > 0)
            parts[0] = ApplyOverrides(parts[0], tenantOverrides);

        // 3. ## Group Rules block (shared, lower priority — template rules excluded: they are opt-in at tenant level)
        var groupRules = groupRulesTask.Result.Where(r => !r.IsTemplate).ToList();
        if (groupRules.Count > 0)
        {
            var groupBlock = "## Group Rules\n\n" +
                string.Join("\n", groupRules.Select(r => $"- {r.PromptInjection}"));
            parts.Add(groupBlock);
        }

        // Tenant business rules are now injected via TenantRulePackHook / RulePackEngine.
        // Session rules remain here (ephemeral; not hook-level evaluated).

        // 4. ## Session Rules block
        var sessionRuleList = sessionRulesTask.Result;
        if (sessionRuleList.Count > 0)
        {
            var sessionBlock = "## Session Rules\n\n" +
                string.Join("\n", sessionRuleList.Select(r => $"- {r.PromptInjection}"));
            parts.Add(sessionBlock);
        }

        // 5. ## Response Examples (few-shot, Phase 24) — only when agentId is known
        if (agentId is not null)
        {
            try
            {
                var examples = await _optimization.GetFewShotExamplesAsync(agentId, tenant.TenantId, ct);
                var enabled = examples.Where(e => e.IsEnabled).OrderBy(e => e.SortOrder)
                                       .Take(_opts.Optimization.MaxFewShotExamplesPerAgent)
                                       .ToList();
                if (enabled.Count > 0)
                {
                    var sb = new StringBuilder("## Response Examples\n");
                    foreach (var ex in enabled)
                    {
                        sb.AppendLine($"User: {ex.UserMessage}");
                        sb.AppendLine($"Assistant: {ex.AssistantMessage}");
                        sb.AppendLine();
                    }
                    parts.Add(sb.ToString().TrimEnd());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load few-shot examples for agent {AgentId}", agentId);
            }
        }

        var result = string.Join("\n\n", parts);

        // Resolve {{variable}} placeholders in the fully-assembled prompt.
        // Precedence: customVars (admin-defined) > runtimeVars (per-request identity) > built-ins (date/time)
        var customVars = PromptVariableResolver.ParseJson(customVariablesJson, _logger);
        var runtimeVars = BuildRuntimeVariables(tenant);
        result = PromptVariableResolver.Resolve(result, customVars, runtimeVars, _logger);

        _logger.LogDebug(
            "Built prompt for agentType={AgentType} agentId={AgentId} tenant={TenantId}: {TotalLength} chars, " +
            "{TenantOverrides} tenant overrides, {GroupOverrides} group overrides, {GroupRules} group rules",
            agentType, agentId, tenant.TenantId, result.Length, tenantOverrides.Count, groupOverrides.Count, groupRules.Count);

        return result;
    }

    /// <inheritdoc/>
    public async Task<(string StaticPart, string DynamicPart)> BuildPartsAsync(
        string baseSystemPrompt,
        string agentType,
        TenantContext tenant,
        CancellationToken ct,
        string? customVariablesJson = null,
        string? agentId = null)
    {
        // Fetch all sources in parallel (same as BuildAsync)
        var tenantOverridesTask = _rules.GetPromptOverridesAsync(tenant.TenantId, agentType, ct, agentId);
        var groupRulesTask = _groupService.GetActiveRulesForTenantAsync(tenant.TenantId, agentType, ct);
        var groupOverridesTask = _groupService.GetActiveOverridesForTenantAsync(tenant.TenantId, agentType, ct);
        var sessionRulesTask = tenant.SessionId is not null
            ? _sessionRules.GetSessionRulesAsync(tenant.SessionId, ct)
            : Task.FromResult(new List<SuggestedRule>());

        await Task.WhenAll(tenantOverridesTask, groupRulesTask, groupOverridesTask, sessionRulesTask);

        // ── Static part (stable across sessions for same agent+tenant) ─────────
        var staticParts = new List<string> { baseSystemPrompt };

        var groupOverrides = groupOverridesTask.Result;
        if (groupOverrides.Count > 0)
            staticParts[0] = ApplyGroupOverrides(staticParts[0], groupOverrides);

        var tenantOverrides = tenantOverridesTask.Result;
        if (tenantOverrides.Count > 0)
            staticParts[0] = ApplyOverrides(staticParts[0], tenantOverrides);

        var groupRules = groupRulesTask.Result.Where(r => !r.IsTemplate).ToList();
        if (groupRules.Count > 0)
            staticParts.Add("## Group Rules\n\n" +
                string.Join("\n", groupRules.Select(r => $"- {r.PromptInjection}")));

        // Few-shot examples (Phase 24) — per-agent (not per-session), so they belong in the
        // STATIC (cached) block to maximise the Anthropic BP1 cache hit rate. They are stable
        // across turns for a given agent+tenant.
        if (agentId is not null)
        {
            try
            {
                var examples = await _optimization.GetFewShotExamplesAsync(agentId, tenant.TenantId, ct);
                var enabled = examples.Where(e => e.IsEnabled).OrderBy(e => e.SortOrder)
                                       .Take(_opts.Optimization.MaxFewShotExamplesPerAgent)
                                       .ToList();
                if (enabled.Count > 0)
                {
                    var sb = new StringBuilder("## Response Examples\n");
                    foreach (var ex in enabled)
                    {
                        sb.AppendLine($"User: {ex.UserMessage}");
                        sb.AppendLine($"Assistant: {ex.AssistantMessage}");
                        sb.AppendLine();
                    }
                    staticParts.Add(sb.ToString().TrimEnd());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load few-shot examples for agent {AgentId}", agentId);
            }
        }

        var staticResult = string.Join("\n\n", staticParts);
        var customVars = PromptVariableResolver.ParseJson(customVariablesJson, _logger);
        var runtimeVars = BuildRuntimeVariables(tenant);
        staticResult = PromptVariableResolver.Resolve(staticResult, customVars, runtimeVars, _logger);

        // ── Dynamic part (changes per session) ────────────────────────────────────────────
        var sessionRuleList = sessionRulesTask.Result;
        var dynamicParts = new List<string>();

        if (sessionRuleList.Count > 0)
            dynamicParts.Add(PromptVariableResolver.Resolve(
                "## Session Rules\n\n" +
                string.Join("\n", sessionRuleList.Select(r => $"- {r.PromptInjection}")),
                customVars, runtimeVars, _logger));

        var dynamicResult = dynamicParts.Count > 0 ? string.Join("\n\n", dynamicParts) : string.Empty;

        _logger.LogDebug(
            "BuildPartsAsync for agentType={AgentType} agentId={AgentId} tenant={TenantId}: " +
            "static={StaticLength} chars, dynamic={DynamicLength} chars",
            agentType, agentId, tenant.TenantId, staticResult.Length, dynamicResult.Length);

        return (staticResult, dynamicResult);
    }

    /// <summary>
    /// Builds per-request runtime variables from TenantContext for {{variable}} substitution.
    /// These are available in all agent system prompts without any admin configuration.
    /// Precedence: customVariables (agent-level admin config) wins over these at resolve time.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildRuntimeVariables(TenantContext tenant)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user_id"] = tenant.UserId,
            ["user_email"] = tenant.UserEmail,
            ["user_name"] = tenant.UserName,
            ["tenant_id"] = tenant.TenantId.ToString(),
            ["tenant_name"] = tenant.TenantName,
            ["session_id"] = tenant.SessionId ?? "",
        };

    private static string ApplyGroupOverrides(string prompt, List<GroupPromptOverrideEntity> overrides)
    {
        foreach (var o in overrides.OrderBy(x => x.Id))
        {
            prompt = o.MergeMode switch
            {
                "Replace" => o.CustomText,
                "Prepend" => o.CustomText + "\n\n" + prompt,
                _ => prompt + "\n\n" + o.CustomText,   // "Append" (default)
            };
        }
        return prompt;
    }

    private static string ApplyOverrides(string prompt, List<TenantPromptOverrideEntity> overrides)
    {
        foreach (var o in overrides.OrderBy(x => x.Version))
        {
            prompt = o.MergeMode switch
            {
                "Replace" => o.CustomText,
                "Prepend" => o.CustomText + "\n\n" + prompt,
                _ => prompt + "\n\n" + o.CustomText,   // "Append" (default)
            };
        }
        return prompt;
    }
}
