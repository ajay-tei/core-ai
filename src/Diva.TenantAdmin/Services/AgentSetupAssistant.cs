using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.LiteLLM;
using Diva.TenantAdmin.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// AI-assisted agent setup: suggests system prompts, rule packs, and regex patterns.
/// Also maintains append-only prompt and rule-pack version history.
///
/// LLM calling pattern mirrors LlmRuleExtractor:
///   "Anthropic" provider → native Anthropic.SDK (avoids ME.AI version conflict)
///   everything else      → OpenAI-compatible ChatClient
/// </summary>
public sealed class AgentSetupAssistant : IAgentSetupAssistant
{
    private static readonly string[] InjectionPrefixes =
    [
        "ignore all previous instructions",
        "###",
        "</system>",
        "<|im_start|>",
        "<|im_end|>",
        "system prompt:",
    ];

    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly LlmOptions _llm;
    private readonly int _maxSuggestionTokens;
    private readonly int _maxRulePackTokens;
    private readonly IEnumerable<ISetupAssistantContextEnricher> _enrichers;
    private readonly IArchetypeRegistry _archetypes;
    private readonly PromptTemplateStore _promptStore;
    private readonly IDatabaseProviderFactory _db;
    private readonly ILlmConfigResolver _resolver;
    private readonly ILogger<AgentSetupAssistant> _logger;

    public AgentSetupAssistant(
        IOptions<LlmOptions> llm,
        IOptions<AgentOptions> agentOptions,
        IEnumerable<ISetupAssistantContextEnricher> enrichers,
        IArchetypeRegistry archetypes,
        PromptTemplateStore promptStore,
        IDatabaseProviderFactory db,
        ILlmConfigResolver resolver,
        ILogger<AgentSetupAssistant> logger)
    {
        _llm = llm.Value;
        _maxSuggestionTokens = agentOptions.Value.MaxSuggestionTokens;
        _maxRulePackTokens = agentOptions.Value.MaxRulePackSuggestionTokens;
        _enrichers = enrichers;
        _archetypes = archetypes;
        _promptStore = promptStore;
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    // ── System Prompt Suggestion ──────────────────────────────────────────────

    public async Task<PromptSuggestionDto> SuggestSystemPromptAsync(AgentSetupContext ctx, CancellationToken ct)
    {
        await RunEnrichersAsync(ctx, ct);
        SanitizeContext(ctx);

        var template = await _promptStore.GetAsync("agent-setup", "system-prompt-generator", ct);
        if (string.IsNullOrWhiteSpace(template))
        {
            _logger.LogWarning("system-prompt-generator.txt not found; returning empty suggestion");
            return new PromptSuggestionDto();
        }

        var archetypeList = string.Join("\n", _archetypes.GetAll()
            .Select(a => $"- {a.Id}: {a.Description}"));

        var configList = ctx.AvailableLlmConfigs.Count > 0
            ? string.Join("\n", ctx.AvailableLlmConfigs.Select(c => $"- ID={c.Id} ({c.Provider}/{c.Model}) Label={c.Label}"))
            : "(none configured)";

        var userPrompt = template
            .Replace("{{agent_name}}", ctx.AgentName)
            .Replace("{{agent_description}}", ctx.AgentDescription)
            .Replace("{{archetype_id}}", ctx.ArchetypeId ?? "general")
            .Replace("{{archetype_list}}", archetypeList)
            .Replace("{{tool_names}}", string.Join(", ", ctx.ToolNames))
            .Replace("{{mcp_tools_section}}", BuildMcpToolsSection(ctx))
            .Replace("{{delegate_agents_section}}", BuildDelegateAgentsSection(ctx))
            .Replace("{{available_llm_configs}}", configList)
            .Replace("{{additional_context}}", ctx.AdditionalContext ?? "")
            .Replace("{{mode}}", ctx.Mode)
            .Replace("{{current_system_prompt}}", ctx.CurrentSystemPrompt ?? "");

        var systemInstruction = "You are a JSON-only AI agent configuration expert. Respond ONLY with valid JSON. No markdown fences.";
        var raw = await TryCallLlmAsync(systemInstruction, userPrompt, _maxSuggestionTokens,
            nameof(SuggestSystemPromptAsync), ctx.TenantId, ctx.AgentId, ct);
        if (raw is null) return new PromptSuggestionDto();

        return ParsePromptSuggestion(raw);
    }

    // ── Rule Pack Suggestion ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<SuggestedRulePackDto>> SuggestRulePacksAsync(AgentSetupContext ctx, CancellationToken ct)
    {
        await RunEnrichersAsync(ctx, ct);
        SanitizeContext(ctx);

        var template = await _promptStore.GetAsync("agent-setup", "rule-pack-generator", ct);
        if (string.IsNullOrWhiteSpace(template))
        {
            _logger.LogWarning("rule-pack-generator.txt not found; returning empty suggestions");
            return [];
        }

        // Three sources of truth — injected live, never hardcoded
        var matrix = RulePackRuleCompatibility.AsMarkdownTable();
        var archetypeList = string.Join("\n", _archetypes.GetAll()
            .Select(a => $"- {a.Id}: {a.Description}"));

        var configList = ctx.AvailableLlmConfigs.Count > 0
            ? string.Join("\n", ctx.AvailableLlmConfigs.Select(c => $"- ID={c.Id} ({c.Provider}/{c.Model}) Label={c.Label}"))
            : "(none configured)";

        var userPrompt = template
            .Replace("{{agent_name}}", ctx.AgentName)
            .Replace("{{agent_description}}", ctx.AgentDescription)
            .Replace("{{archetype_id}}", ctx.ArchetypeId ?? "general")
            .Replace("{{archetype_list}}", archetypeList)
            .Replace("{{tool_names}}", string.Join(", ", ctx.ToolNames))
            .Replace("{{mcp_tools_section}}", BuildMcpToolsSection(ctx))
            .Replace("{{delegate_agents_section}}", BuildDelegateAgentsSection(ctx))
            .Replace("{{hook_point_matrix}}", matrix)
            .Replace("{{available_llm_configs}}", configList)
            .Replace("{{additional_context}}", ctx.AdditionalContext ?? "")
            .Replace("{{mode}}", ctx.Mode)
            .Replace("{{current_rule_packs}}", ctx.CurrentRulePacksJson ?? "[]");

        var systemInstruction = "You are a JSON-only AI agent rule pack designer. Respond ONLY with a valid JSON array. No markdown fences.";
        var raw = await TryCallLlmAsync(systemInstruction, userPrompt, _maxRulePackTokens,
            nameof(SuggestRulePacksAsync), ctx.TenantId, ctx.AgentId, ct);
        if (raw is null) return [];

        return ParseAndValidateRulePacks(raw);
    }

    // ── Regex Suggestion ──────────────────────────────────────────────────────

    public async Task<RegexSuggestionDto> SuggestRegexAsync(RegexSuggestionRequestDto request, int tenantId, CancellationToken ct)
    {
        var template = await _promptStore.GetAsync("agent-setup", "regex-generator", ct);
        if (string.IsNullOrWhiteSpace(template))
        {
            _logger.LogWarning("regex-generator.txt not found; returning empty suggestion");
            return new RegexSuggestionDto();
        }

        var positives = string.Join("\n", request.SampleMatches.Take(10).Select(s => $"  - {s}"));
        var negatives = string.Join("\n", request.SampleNonMatches.Take(10).Select(s => $"  - {s}"));

        var userPrompt = template
            .Replace("{{intent_description}}", SanitizeString(request.IntentDescription))
            .Replace("{{sample_matches}}", positives)
            .Replace("{{sample_non_matches}}", negatives)
            .Replace("{{rule_type}}", request.RuleType ?? "")
            .Replace("{{hook_point}}", request.HookPoint ?? "");

        var systemInstruction = "You are a JSON-only regex expert. Respond ONLY with valid JSON. No markdown fences.";
        var raw = await TryCallLlmAsync(systemInstruction, userPrompt, _maxSuggestionTokens,
            nameof(SuggestRegexAsync), tenantId, null, ct);
        if (raw is null) return new RegexSuggestionDto();

        return ParseAndValidateRegex(raw, request);
    }

    // ── History: Agent Prompt ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<AgentPromptHistoryEntryDto>> GetAgentPromptHistoryAsync(
        string agentId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.AgentPromptHistory
            .Where(h => h.AgentId == agentId)
            .OrderByDescending(h => h.Version)
            .Select(h => new AgentPromptHistoryEntryDto
            {
                Version = h.Version,
                SystemPrompt = h.SystemPrompt,
                CreatedAtUtc = h.CreatedAtUtc,
                CreatedBy = h.CreatedBy,
                Source = h.Source,
                Reason = h.Reason,
            })
            .ToListAsync(ct);
    }

    public async Task SavePromptVersionAsync(
        string agentId, int tenantId, string systemPrompt,
        string source, string? reason, string? createdBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var maxVersion = await db.AgentPromptHistory
            .Where(h => h.AgentId == agentId && h.TenantId == tenantId)
            .MaxAsync(h => (int?)h.Version, ct) ?? 0;

        db.AgentPromptHistory.Add(new Infrastructure.Data.Entities.AgentPromptHistoryEntity
        {
            TenantId = tenantId,
            AgentId = agentId,
            Version = maxVersion + 1,
            SystemPrompt = systemPrompt,
            Source = source,
            Reason = reason,
            CreatedBy = createdBy ?? "system",
            CreatedAtUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<AgentPromptHistoryEntryDto?> RestorePromptVersionAsync(
        string agentId, int tenantId, int version, string? reason, string? restoredBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entry = await db.AgentPromptHistory
            .FirstOrDefaultAsync(h => h.AgentId == agentId && h.Version == version, ct);

        if (entry is null) return null;

        // Restore = append a new version (no destructive overwrite)
        var maxVersion = await db.AgentPromptHistory
            .Where(h => h.AgentId == agentId && h.TenantId == tenantId)
            .MaxAsync(h => (int?)h.Version, ct) ?? 0;

        var restored = new Infrastructure.Data.Entities.AgentPromptHistoryEntity
        {
            TenantId = tenantId,
            AgentId = agentId,
            Version = maxVersion + 1,
            SystemPrompt = entry.SystemPrompt,
            Source = "restore",
            Reason = reason ?? $"Restored from version {version}",
            CreatedBy = restoredBy ?? "system",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.AgentPromptHistory.Add(restored);

        // Also update the agent's live system prompt
        var agent = await db.AgentDefinitions.FindAsync([agentId], ct);
        if (agent is not null)
        {
            agent.SystemPrompt = entry.SystemPrompt;
        }

        await db.SaveChangesAsync(ct);

        return new AgentPromptHistoryEntryDto
        {
            Version = restored.Version,
            SystemPrompt = restored.SystemPrompt,
            CreatedAtUtc = restored.CreatedAtUtc,
            CreatedBy = restored.CreatedBy,
            Source = restored.Source,
            Reason = restored.Reason,
        };
    }

    // ── History: Rule Pack ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RulePackHistoryEntryDto>> GetRulePackHistoryAsync(
        int packId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.RulePackHistory
            .Where(h => h.PackId == packId)
            .OrderByDescending(h => h.Version)
            .Select(h => new RulePackHistoryEntryDto
            {
                Version = h.Version,
                RulesJson = h.RulesJson,
                CreatedAtUtc = h.CreatedAtUtc,
                CreatedBy = h.CreatedBy,
                Source = h.Source,
                Reason = h.Reason,
            })
            .ToListAsync(ct);
    }

    public async Task SaveRulePackVersionAsync(
        int packId, int tenantId, string rulesJson,
        string source, string? reason, string? createdBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var maxVersion = await db.RulePackHistory
            .Where(h => h.PackId == packId && h.TenantId == tenantId)
            .MaxAsync(h => (int?)h.Version, ct) ?? 0;

        db.RulePackHistory.Add(new Infrastructure.Data.Entities.RulePackHistoryEntity
        {
            TenantId = tenantId,
            PackId = packId,
            Version = maxVersion + 1,
            RulesJson = rulesJson,
            Source = source,
            Reason = reason,
            CreatedBy = createdBy ?? "system",
            CreatedAtUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<RulePackHistoryEntryDto?> RestoreRulePackVersionAsync(
        int packId, int tenantId, int version, string? reason, string? restoredBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entry = await db.RulePackHistory
            .FirstOrDefaultAsync(h => h.PackId == packId && h.Version == version, ct);

        if (entry is null) return null;

        var maxVersion = await db.RulePackHistory
            .Where(h => h.PackId == packId && h.TenantId == tenantId)
            .MaxAsync(h => (int?)h.Version, ct) ?? 0;

        var restored = new Infrastructure.Data.Entities.RulePackHistoryEntity
        {
            TenantId = tenantId,
            PackId = packId,
            Version = maxVersion + 1,
            RulesJson = entry.RulesJson,
            Source = "restore",
            Reason = reason ?? $"Restored from version {version}",
            CreatedBy = restoredBy ?? "system",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.RulePackHistory.Add(restored);
        await db.SaveChangesAsync(ct);

        return new RulePackHistoryEntryDto
        {
            Version = restored.Version,
            RulesJson = restored.RulesJson,
            CreatedAtUtc = restored.CreatedAtUtc,
            CreatedBy = restored.CreatedBy,
            Source = restored.Source,
            Reason = restored.Reason,
        };
    }

    // ── LLM Calls ────────────────────────────────────────────────────────────

    private async Task<(string provider, string apiKey, string model, string? endpoint)>
        ResolveProviderAsync(int tenantId, string? agentId, CancellationToken ct)
    {
        if (tenantId > 0)
        {
            try
            {
                int? agentLlmConfigId = null;
                string? agentModelId = null;
                if (!string.IsNullOrEmpty(agentId))
                {
                    using var db = _db.CreateDbContext(TenantContext.System(tenantId));
                    var agent = await db.AgentDefinitions
                        .FirstOrDefaultAsync(a => a.Id == agentId, ct);
                    if (agent is not null)
                    {
                        agentLlmConfigId = agent.LlmConfigId;
                        agentModelId = agent.ModelId;
                    }
                }
                // Wildcard (0): this method only receives a raw tenantId, not a TenantContext/
                // environmentId — the Agent Setup Assistant runs during agent authoring, before
                // Phase F's environment switcher exists to say which environment is being edited.
                var resolved = await _resolver.ResolveAsync(tenantId, agentLlmConfigId, agentModelId, 0, ct);
                var provider = resolved.Provider ?? _llm.DirectProvider.Provider;
                var apiKey   = resolved.ApiKey   ?? _llm.DirectProvider.ApiKey;
                var model    = resolved.Model    ?? _llm.DirectProvider.Model;
                var endpoint = resolved.Endpoint;
                var isAnthropic = provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
                if (!isAnthropic && endpoint is null)
                    endpoint = _llm.DirectProvider.Endpoint;
                _logger.LogDebug(
                    "AgentSetupAssistant resolved: tenant={TenantId} agent={AgentId} llmConfigId={LlmConfigId} provider={Provider} model={Model}",
                    tenantId, agentId, agentLlmConfigId, provider, model);
                return (provider, apiKey, model, endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LlmConfigResolver failed for tenant {TenantId} agent {AgentId} in AgentSetupAssistant — using appsettings fallback", tenantId, agentId);
            }
        }
        return (_llm.DirectProvider.Provider, _llm.DirectProvider.ApiKey, _llm.DirectProvider.Model, _llm.DirectProvider.Endpoint);
    }

    /// <summary>
    /// Calls the LLM using the LLM provider configured for the given agent (falls back to tenant → platform → appsettings).
    /// </summary>
    private async Task<string?> TryCallLlmAsync(
        string systemInstruction, string userPrompt, int maxTokens, string callSite, int tenantId, string? agentId, CancellationToken ct)
    {
        try
        {
            var (provider, apiKey, model, endpoint) = await ResolveProviderAsync(tenantId, agentId, ct);
            return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(systemInstruction, userPrompt, maxTokens, apiKey, model, ct)
                : await CallOpenAiCompatibleAsync(systemInstruction, userPrompt, maxTokens, apiKey, model, endpoint, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{CallSite} LLM call failed", callSite);
            return null;
        }
    }

    private async Task<string> CallAnthropicAsync(string systemPrompt, string userPrompt, int maxTokens, string apiKey, string model, CancellationToken ct)
    {
        var client = new AnthropicClient(new APIAuthentication(apiKey));
        var parameters = new MessageParameters
        {
            Model = model,
            MaxTokens = maxTokens,
            System = [new SystemMessage(systemPrompt)],
            Messages = [new Message
            {
                Role = RoleType.User,
                Content = [new Anthropic.SDK.Messaging.TextContent { Text = userPrompt }]
            }]
        };

        var msg = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        return msg.Content.OfType<Anthropic.SDK.Messaging.TextContent>().FirstOrDefault()?.Text ?? "{}";
    }

    private async Task<string> CallOpenAiCompatibleAsync(string systemPrompt, string userPrompt, int maxTokens, string apiKey, string model, string? endpoint, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey);
        var clientOpts = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
            clientOpts.Endpoint = new Uri(endpoint);

        var chatClient = new OpenAIClient(credential, clientOpts).GetChatClient(model);
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var result = await chatClient.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = maxTokens }, ct);
        return result.Value.Content.FirstOrDefault()?.Text ?? "{}";
    }

    // ── Parse Helpers ─────────────────────────────────────────────────────────

    private PromptSuggestionDto ParsePromptSuggestion(string raw)
    {
        try
        {
            var json = StripMarkdownFences(raw);
            var dto = JsonSerializer.Deserialize<LlmPromptSuggestion>(json, SnakeCaseOptions);
            return new PromptSuggestionDto
            {
                SystemPrompt = dto?.SystemPrompt ?? string.Empty,
                Rationale = dto?.Rationale ?? string.Empty,
            };
        }
        catch (JsonException ex)
        {
            // Truncated response (max_tokens hit): attempt to extract system_prompt value directly
            _logger.LogWarning(ex, "Failed to parse prompt suggestion JSON — attempting truncation recovery");
            var extracted = TryExtractTruncatedStringField(raw, "system_prompt");
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                _logger.LogInformation("Recovered truncated system_prompt ({Chars} chars)", extracted.Length);
                return new PromptSuggestionDto { SystemPrompt = extracted, Rationale = "(response was truncated — increase MaxSuggestionTokens)" };
            }
            return new PromptSuggestionDto();
        }
    }

    /// <summary>
    /// Best-effort extraction of a JSON string field value from a truncated response.
    /// Finds "field_name": "..." and returns whatever content was produced before truncation.
    /// </summary>
    private static string? TryExtractTruncatedStringField(string raw, string fieldName)
    {
        var json = StripMarkdownFences(raw);
        var marker = $"\"{fieldName}\"";
        var fieldIdx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (fieldIdx < 0) return null;

        var colonIdx = json.IndexOf(':', fieldIdx + marker.Length);
        if (colonIdx < 0) return null;

        var quoteIdx = json.IndexOf('"', colonIdx + 1);
        if (quoteIdx < 0) return null;

        // Read until closing unescaped quote or end of string
        var sb = new System.Text.StringBuilder();
        for (var i = quoteIdx + 1; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
            if (c == '"') break;
            sb.Append(c);
        }
        var value = sb.ToString().Trim();
        return value.Length > 0 ? value : null;
    }

    private IReadOnlyList<SuggestedRulePackDto> ParseAndValidateRulePacks(string raw)
    {
        List<LlmRulePackSuggestion>? llmPacks = null;
        try
        {
            var json = StripMarkdownFences(raw);
            llmPacks = JsonSerializer.Deserialize<List<LlmRulePackSuggestion>>(json, SnakeCaseOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse rule pack suggestions JSON; attempting partial recovery");
            // The LLM may have been cut off by the token limit, leaving a truncated array.
            // Walk the raw output depth-first to find the last fully-closed top-level object
            // and close the array there so we can return whatever completed packs we got.
            try
            {
                var repaired = TryRepairTruncatedJsonArray(StripMarkdownFences(raw));
                if (repaired != "[]")
                {
                    llmPacks = JsonSerializer.Deserialize<List<LlmRulePackSuggestion>>(repaired, SnakeCaseOptions);
                    _logger.LogInformation("Partial recovery succeeded: {Count} rule pack(s) recovered", llmPacks?.Count ?? 0);
                }
            }
            catch (Exception repairEx)
            {
                _logger.LogWarning(repairEx, "Partial recovery also failed; returning empty rule packs");
                return [];
            }
        }

        if (llmPacks is null or { Count: 0 }) return [];

        var result = new List<SuggestedRulePackDto>();
        foreach (var pack in llmPacks)
        {
            var validRules = new List<SuggestedHookRuleDto>();
            foreach (var rule in pack.Rules ?? [])
            {
                if (!RulePackRuleCompatibility.IsValid(rule.HookPoint ?? "", rule.RuleType ?? ""))
                {
                    _logger.LogDebug("Dropping invalid LLM suggestion: hookPoint={HookPoint} ruleType={RuleType}",
                        rule.HookPoint, rule.RuleType);
                    continue;
                }
                validRules.Add(new SuggestedHookRuleDto
                {
                    HookPoint = rule.HookPoint!,
                    RuleType = rule.RuleType!,
                    Pattern = rule.Pattern,
                    Instruction = rule.Instruction,
                    Replacement = rule.Replacement,
                    ToolName = rule.ToolName,
                    Order = rule.Order,
                    StopOnMatch = rule.StopOnMatch,
                    LlmConfigId = rule.LlmConfigId,
                    ModelOverride = rule.ModelOverride,
                });
            }

            result.Add(new SuggestedRulePackDto
            {
                Name = pack.Name ?? "Suggested Pack",
                Description = pack.Description ?? string.Empty,
                Rationale = pack.Rationale ?? string.Empty,
                Operation = pack.Operation ?? "add",
                ExistingPackId = pack.ExistingPackId,
                Rules = validRules,
            });
        }

        return result;
    }

    private RegexSuggestionDto ParseAndValidateRegex(string raw, RegexSuggestionRequestDto request)
    {
        LlmRegexSuggestion? llm = null;
        try
        {
            var json = StripMarkdownFences(raw);
            llm = JsonSerializer.Deserialize<LlmRegexSuggestion>(json, SnakeCaseOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse regex suggestion JSON");
            return new RegexSuggestionDto();
        }

        if (llm is null || string.IsNullOrWhiteSpace(llm.Pattern))
            return new RegexSuggestionDto();

        var warnings = new List<string>();

        // Server-side validation — same approach as RulePackEngine regex handling
        bool patternValid;
        Exception? regexEx = null;
        try
        {
            var r = new Regex(llm.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(500));
            r.IsMatch(""); // force compilation
            patternValid = true;
        }
        catch (RegexMatchTimeoutException)
        {
            patternValid = false;
            regexEx = null;
            warnings.Add("Pattern timed out during validation — may cause catastrophic backtracking.");
        }
        catch (ArgumentException ex)
        {
            patternValid = false;
            regexEx = ex;
            warnings.Add($"Invalid regex syntax: {ex.Message}");
        }

        if (regexEx is not null)
            _logger.LogDebug(regexEx, "Regex validation error for pattern '{Pattern}'", llm.Pattern);

        if (!patternValid)
            return new RegexSuggestionDto { Warnings = warnings };

        // Preview matches/non-matches
        var previewMatches = new List<string>();
        var previewNonMatches = new List<string>();

        Exception? previewEx = null;
        try
        {
            var compiled = new Regex(llm.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            foreach (var s in request.SampleMatches.Take(5))
            {
                if (compiled.IsMatch(s)) previewMatches.Add(s);
                else warnings.Add($"Expected match but didn't: '{s}'");
            }
            foreach (var s in request.SampleNonMatches.Take(5))
            {
                if (!compiled.IsMatch(s)) previewNonMatches.Add(s);
                else warnings.Add($"Expected non-match but matched: '{s}'");
            }
        }
        catch (Exception ex)
        {
            previewEx = ex;
        }

        if (previewEx is not null)
            _logger.LogDebug(previewEx, "Regex preview evaluation failed for '{Pattern}'", llm.Pattern);

        return new RegexSuggestionDto
        {
            Pattern = llm.Pattern,
            Explanation = llm.Explanation ?? string.Empty,
            Flags = llm.Flags,
            Warnings = warnings,
            PreviewMatches = previewMatches,
            PreviewNonMatches = previewNonMatches,
        };
    }

    // ── Pure Helpers ──────────────────────────────────────────────────────────

    private static string StripMarkdownFences(string raw) =>
        Regex.Replace(raw.Trim(), @"^```json?\s*|\s*```$", "", RegexOptions.Multiline).Trim();

    /// <summary>
    /// Attempts to recover a valid JSON array from a string that has been truncated (e.g. by
    /// a token limit). Walks character-by-character tracking brace depth and string-escape
    /// state to find the index of the last <c>}</c> that closes a top-level array element,
    /// then appends <c>]</c> to form a valid (shorter) array.
    /// </summary>
    private static string TryRepairTruncatedJsonArray(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "[]";
        s = s.Trim();
        if (!s.StartsWith('[')) return "[]";

        int depth = 0;
        bool inString = false;
        bool escape = false;
        int lastCompleteObjectEnd = -1;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) lastCompleteObjectEnd = i;
            }
        }

        if (lastCompleteObjectEnd < 0) return "[]";
        return "[" + s[1..(lastCompleteObjectEnd + 1)] + "]";
    }

    private async Task RunEnrichersAsync(AgentSetupContext ctx, CancellationToken ct)
    {
        foreach (var enricher in _enrichers)
        {
            Exception? ex = null;
            try
            {
                await enricher.EnrichAsync(ctx, ct);
            }
            catch (Exception e)
            {
                ex = e;
            }
            if (ex is not null)
                _logger.LogWarning(ex, "Enricher {Enricher} failed", enricher.GetType().Name);
        }
    }

    private static void SanitizeContext(AgentSetupContext ctx)
    {
        ctx.AgentDescription = SanitizeString(ctx.AgentDescription);
        ctx.AdditionalContext = SanitizeString(ctx.AdditionalContext);
    }

    private static string BuildMcpToolsSection(AgentSetupContext ctx)
    {
        if (ctx.McpTools.Count > 0)
            return string.Join("\n", ctx.McpTools.Select(t =>
                string.IsNullOrEmpty(t.Description) ? $"- {t.Name}" : $"- {t.Name}: {t.Description}"));

        if (ctx.ToolNames.Length > 0)
            return string.Join("\n", ctx.ToolNames.Select(n => $"- {n} (MCP server)"));

        return "(none configured)";
    }

    private static string BuildDelegateAgentsSection(AgentSetupContext ctx)
    {
        if (ctx.DelegateAgents.Count == 0) return "(none configured)";

        return string.Join("\n", ctx.DelegateAgents.Select(d =>
        {
            var toolName = "call_agent_" + Regex.Replace(d.Name, "[^a-zA-Z0-9]", "_").ToLower();
            var caps = d.Capabilities?.Length > 0
                ? $"\n  Capabilities: {string.Join(", ", d.Capabilities)}"
                : "";
            return $"- {d.Name}: {d.Description ?? "No description provided"}. Invoke via {toolName} tool.{caps}";
        }));
    }

    internal static string SanitizeString(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var result = input.Trim();
        foreach (var prefix in InjectionPrefixes)
            result = result.Replace(prefix, "[filtered]", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    // ── LLM response shapes (snake_case from LLM) ─────────────────────────────

    private sealed class LlmPromptSuggestion
    {
        [JsonPropertyName("system_prompt")]
        public string? SystemPrompt { get; init; }
        [JsonPropertyName("rationale")]
        public string? Rationale { get; init; }
    }

    private sealed class LlmRulePackSuggestion
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("description")]
        public string? Description { get; init; }
        [JsonPropertyName("rationale")]
        public string? Rationale { get; init; }
        [JsonPropertyName("operation")]
        public string? Operation { get; init; }
        [JsonPropertyName("existing_pack_id")]
        public int? ExistingPackId { get; init; }
        [JsonPropertyName("rules")]
        public List<LlmHookRuleSuggestion>? Rules { get; init; }
    }

    private sealed class LlmHookRuleSuggestion
    {
        [JsonPropertyName("hook_point")]
        public string? HookPoint { get; init; }
        [JsonPropertyName("rule_type")]
        public string? RuleType { get; init; }
        [JsonPropertyName("pattern")]
        public string? Pattern { get; init; }
        [JsonPropertyName("instruction")]
        public string? Instruction { get; init; }
        [JsonPropertyName("replacement")]
        public string? Replacement { get; init; }
        [JsonPropertyName("tool_name")]
        public string? ToolName { get; init; }
        [JsonPropertyName("order")]
        public int Order { get; init; }
        [JsonPropertyName("stop_on_match")]
        public bool StopOnMatch { get; init; }
        [JsonPropertyName("llm_config_id")]
        public int? LlmConfigId { get; init; }
        [JsonPropertyName("model_override")]
        public string? ModelOverride { get; init; }
    }

    private sealed class LlmRegexSuggestion
    {
        [JsonPropertyName("pattern")]
        public string? Pattern { get; init; }
        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }
        [JsonPropertyName("flags")]
        public string? Flags { get; init; }
        [JsonPropertyName("warnings")]
        public List<string>? Warnings { get; init; }
    }
}
