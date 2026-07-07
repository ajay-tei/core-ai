using System.Text.Json;
using Diva.Infrastructure.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Groups;

/// <summary>
/// Merges a <see cref="GroupAgentTemplateEntity"/> with an optional
/// <see cref="TenantGroupAgentOverlayEntity"/> into a synthetic <see cref="AgentDefinitionEntity"/>
/// that can be executed by <see cref="DynamicAgentRegistry"/> and <see cref="AnthropicAgentRunner"/>.
///
/// Must be public — accessed from both Diva.Agents (separate assembly) and Diva.Host.
/// Pure static method; no DI dependencies.
/// </summary>
public static class GroupAgentOverlayMerger
{
    public static AgentDefinitionEntity Merge(
        GroupAgentTemplateEntity template,
        TenantGroupAgentOverlayEntity? overlay,
        int tenantId,
        ILogger? logger = null)
    {
        var def = new AgentDefinitionEntity
        {
            Id = template.Id,
            TenantId = tenantId,
            Name = template.Name,
            DisplayName = template.DisplayName,
            Description = template.Description,
            AgentType = template.AgentType,
            SystemPrompt = template.SystemPrompt,
            ModelId = template.ModelId,
            Temperature = template.Temperature,
            MaxIterations = template.MaxIterations,
            Capabilities = template.Capabilities,
            ToolBindings = template.ToolBindings,
            McpServerRefsJson = template.McpServerRefsJson,
            VerificationMode = template.VerificationMode,
            ContextWindowJson = template.ContextWindowJson,
            CustomVariablesJson = template.CustomVariablesJson,
            MaxContinuations = template.MaxContinuations,
            MaxToolResultChars = template.MaxToolResultChars,
            MaxOutputTokens = template.MaxOutputTokens,
            EnableHistoryCaching = template.EnableHistoryCaching,
            PipelineStagesJson = template.PipelineStagesJson,
            ToolFilterJson = template.ToolFilterJson,
            StageInstructionsJson = template.StageInstructionsJson,
            LlmConfigId = template.LlmConfigId,
            ArchetypeId = template.ArchetypeId,
            HooksJson = template.HooksJson,
            A2AEndpoint = template.A2AEndpoint,
            A2AAuthScheme = template.A2AAuthScheme,
            A2ASecretRef = template.A2ASecretRef,
            ExecutionMode = template.ExecutionMode,
            ModelSwitchingJson = template.ModelSwitchingJson,
            IsEnabled = template.IsEnabled,
            Status = template.Status,
            Version = template.Version,
        };

        if (overlay is null || !overlay.IsEnabled)
            return def;

        // ── Apply overlay fields (non-null values override template) ──────────

        if (overlay.ModelId is not null)
            def.ModelId = overlay.ModelId;

        if (overlay.Temperature.HasValue)
            def.Temperature = overlay.Temperature.Value;

        if (overlay.LlmConfigId.HasValue)
            def.LlmConfigId = overlay.LlmConfigId;

        if (overlay.MaxOutputTokens.HasValue)
            def.MaxOutputTokens = overlay.MaxOutputTokens;

        if (overlay.SystemPromptAddendum is not null)
            def.SystemPrompt = (def.SystemPrompt ?? string.Empty)
                + $"\n\n## Tenant Addendum\n{overlay.SystemPromptAddendum}";

        if (overlay.ExtraToolBindingsJson is not null)
            def.ToolBindings = MergeJsonArrays(def.ToolBindings, overlay.ExtraToolBindingsJson, logger);

        if (overlay.CustomVariablesJson is not null)
            def.CustomVariablesJson = MergeJsonDictionaries(def.CustomVariablesJson, overlay.CustomVariablesJson, logger);

        return def;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Unions two JSON string arrays. Overlay entries are appended; duplicates removed.
    /// Returns the base array unchanged if overlay is null/empty.
    /// </summary>
    private static string? MergeJsonArrays(string? baseJson, string overlayJson, ILogger? logger)
    {
        var baseList = ParseStringArray(baseJson, logger);
        var overlayList = ParseStringArray(overlayJson, logger);

        var merged = baseList
            .Concat(overlayList.Where(o => !baseList.Contains(o, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        return merged.Count == 0 ? null : JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// Merges two JSON Dictionary&lt;string,string&gt; objects. Overlay values win on key collision.
    /// Returns the base dictionary unchanged if overlay is null/empty.
    /// </summary>
    private static string? MergeJsonDictionaries(string? baseJson, string overlayJson, ILogger? logger)
    {
        var baseDict = ParseStringDictionary(baseJson, logger);
        var overlayDict = ParseStringDictionary(overlayJson, logger);

        foreach (var (k, v) in overlayDict)
            baseDict[k] = v;

        return baseDict.Count == 0 ? null : JsonSerializer.Serialize(baseDict);
    }

    private static List<string> ParseStringArray(string? json, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "GroupAgentOverlay: failed to parse JSON array — overlay field ignored");
            return [];
        }
    }

    private static Dictionary<string, string> ParseStringDictionary(string? json, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "GroupAgentOverlay: failed to parse JSON dictionary — overlay field ignored");
            return [];
        }
    }
}
