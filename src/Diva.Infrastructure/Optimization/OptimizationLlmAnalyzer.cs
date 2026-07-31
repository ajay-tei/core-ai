using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Core.Optimization;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Diva.Infrastructure.Optimization;

public sealed class OptimizationLlmAnalyzer : IOptimizationLlmAnalyzer
{
    private readonly ILlmConfigResolver _resolver;
    private readonly IOptimizationRulePackAccessor _rulePackAccessor;
    private readonly LlmOptions _llm;
    private readonly AgentOptions _opts;
    private readonly ILogger<OptimizationLlmAnalyzer> _logger;

    public OptimizationLlmAnalyzer(
        ILlmConfigResolver resolver,
        IOptimizationRulePackAccessor rulePackAccessor,
        IOptions<LlmOptions> llm,
        IOptions<AgentOptions> opts,
        ILogger<OptimizationLlmAnalyzer> logger)
    {
        _resolver          = resolver;
        _rulePackAccessor  = rulePackAccessor;
        _llm               = llm.Value;
        _opts              = opts.Value;
        _logger            = logger;
    }

    private const string AnalyzeSystemMessage      = "You are an AI performance optimizer. Return ONLY valid JSON arrays.";
    private const string MergeSystemMessage        = "You are a system prompt editor. Output only the final merged system prompt text — no JSON, no markdown, no preamble, no explanation.";
    private const string QuickImproveSystemMessage = "You are a system prompt editor. Apply the admin's instruction to the system prompt. Output ONLY the final improved prompt — no preamble, no commentary, no JSON.";

    public async Task<List<OptimizationSuggestionDto>> AnalyzeAsync(
        SessionAnalysisReport report,
        AgentDefinitionEntity agentDef,
        string? userContext,
        CancellationToken ct)
    {
        try
        {
            var (provider, apiKey, model, endpoint) = await ResolveProviderAsync(agentDef, ct);

            var packs = new List<Diva.Core.Optimization.RulePackSummary>();
            try { packs = await _rulePackAccessor.GetPackSummariesAsync(agentDef.TenantId, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to load rule packs for optimizer prompt"); }

            var prompt = BuildPrompt(report, agentDef, userContext, packs);
            var raw = provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(prompt, apiKey, model, _opts.Optimization.AnalyzerMaxTokens, AnalyzeSystemMessage, ct)
                : await CallOpenAiCompatibleAsync(prompt, apiKey, model, endpoint, _opts.Optimization.AnalyzerMaxTokens, AnalyzeSystemMessage, ct);

            return ParseSuggestions(raw, agentDef.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optimization analysis LLM call failed for agent {AgentId}", agentDef.Id);
            return [];
        }
    }

    public async Task<string> MergePromptAsync(
        string currentPrompt,
        IReadOnlyList<string> suggestedChanges,
        AgentDefinitionEntity agentDef,
        CancellationToken ct)
    {
        if (suggestedChanges.Count == 0) return currentPrompt;

        try
        {
            var (provider, apiKey, model, endpoint) = await ResolveProviderAsync(agentDef, ct);
            var mergeTokens = ResolveMergeMaxTokens(agentDef);
            var prompt = BuildMergePrompt(currentPrompt, suggestedChanges);
            _logger.LogDebug("Prompt merge: agent={AgentId} maxTokens={Max}", agentDef.Id, mergeTokens);
            return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(prompt, apiKey, model, mergeTokens, MergeSystemMessage, ct)
                : await CallOpenAiCompatibleAsync(prompt, apiKey, model, endpoint, mergeTokens, MergeSystemMessage, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt merge LLM call failed for agent {AgentId} — returning current prompt unchanged", agentDef.Id);
            return currentPrompt;
        }
    }

    public async Task<string> QuickImprovePromptAsync(
        string currentPrompt,
        string instruction,
        AgentDefinitionEntity agentDef,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return currentPrompt;

        try
        {
            var (provider, apiKey, model, endpoint) = await ResolveProviderAsync(agentDef, ct);
            var maxTokens = ResolveMergeMaxTokens(agentDef);
            var prompt    = BuildQuickImprovePrompt(currentPrompt, instruction);
            _logger.LogDebug("Quick prompt improve: agent={AgentId} maxTokens={Max}", agentDef.Id, maxTokens);
            return provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(prompt, apiKey, model, maxTokens, QuickImproveSystemMessage, ct)
                : await CallOpenAiCompatibleAsync(prompt, apiKey, model, endpoint, maxTokens, QuickImproveSystemMessage, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick prompt improve LLM call failed for agent {AgentId}", agentDef.Id);
            throw;
        }
    }

    private static string BuildQuickImprovePrompt(string currentPrompt, string instruction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Apply the instruction below to the system prompt.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Preserve the original tone, structure, and intent unless the instruction explicitly says otherwise.");
        sb.AppendLine("- Only change what the instruction requires — do not add or remove unrelated content.");
        sb.AppendLine("- If the current prompt is empty, write a new prompt that fulfils the instruction.");
        sb.AppendLine("- Output ONLY the final system prompt. No preamble, no labels, no commentary.");
        sb.AppendLine();
        sb.AppendLine("Current system prompt:");
        sb.AppendLine("---");
        sb.AppendLine(string.IsNullOrWhiteSpace(currentPrompt) ? "(empty)" : currentPrompt);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"Instruction: {instruction}");
        sb.AppendLine();
        sb.AppendLine("Improved system prompt:");
        return sb.ToString();
    }

    private int ResolveMergeMaxTokens(AgentDefinitionEntity agentDef)
    {
        if (!string.IsNullOrWhiteSpace(agentDef.OptimizationOverrideJson))
        {
            try
            {
                var o = JsonSerializer.Deserialize<OptimizationOverrideOptions>(
                    agentDef.OptimizationOverrideJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (o?.MergeMaxTokens is > 0)
                    return o.MergeMaxTokens.Value;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse OptimizationOverrideJson for agent {AgentId}", agentDef.Id);
            }
        }
        return _opts.Optimization.MergeMaxTokens;
    }

    // ── Prompt builders ────────────────────────────────────────────────────────

    private string BuildPrompt(
        SessionAnalysisReport r,
        AgentDefinitionEntity agentDef,
        string? userContext,
        List<Diva.Core.Optimization.RulePackSummary> packs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are an AI agent optimizer. Analyze the performance metrics below and suggest specific, actionable improvements.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(userContext))
        {
            sb.AppendLine("Admin-identified problem (HIGHEST PRIORITY — generate suggestions that address this specifically):");
            sb.AppendLine(userContext);
            sb.AppendLine();
        }

        sb.AppendLine($"Agent: {agentDef.Name} (type: {agentDef.AgentType})");
        sb.AppendLine($"Period: {r.TotalSessions} sessions, {r.TotalTurns} turns");
        sb.AppendLine();

        if (r.ScoredTurns > 0)
        {
            sb.AppendLine($"Dimensional Quality Scores (avg across {r.ScoredTurns} scored turns):");
            sb.AppendLine($"  Faithfulness:    {r.AvgFaithfulness:F2}  (high=no hallucination, low=makes things up)");
            sb.AppendLine($"  Completeness:    {r.AvgCompleteness:F2}  (high=fully answers, low=partial answers)");
            sb.AppendLine($"  Tool Efficiency: {r.AvgToolEfficiency:F2}  (high=optimal tool use, low=wrong/missing tools)");
            sb.AppendLine($"  Coherence:       {r.AvgCoherence:F2}  (high=clear/structured, low=disorganized)");
            sb.AppendLine();
        }

        sb.AppendLine("Additional signals:");
        sb.AppendLine($"  Verification failure rate: {r.VerificationFailureRate:P1}");
        sb.AppendLine($"  Correction retry rate:     {r.CorrectionRetryRate:P1}");
        sb.AppendLine($"  Max-iterations hit rate:   {r.MaxIterationsHitRate:P1}");
        sb.AppendLine($"  Tool error rate:           {r.ToolErrorRate:P1}");
        sb.AppendLine($"  Avg iterations/turn:       {r.AverageIterationsPerTurn:F1}");

        if (r.FrequentToolErrors.Count > 0)
            sb.AppendLine($"  Frequent tool errors: {string.Join(", ", r.FrequentToolErrors)}");

        if (r.SampleTurnContent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Sample worst-performing turns:");
            foreach (var sample in r.SampleTurnContent.Take(3))
                sb.AppendLine(sample);
        }

        sb.AppendLine();
        sb.AppendLine("Current agent configuration:");
        sb.AppendLine($"  System Prompt (first 2000 chars): {Truncate(agentDef.SystemPrompt ?? "", 2000)}");
        sb.AppendLine($"  Temperature: {agentDef.Temperature}");
        sb.AppendLine($"  MaxIterations: {agentDef.MaxIterations}");
        sb.AppendLine($"  MaxContinuations: {agentDef.MaxContinuations?.ToString() ?? "global default"}");
        sb.AppendLine($"  VerificationMode: {agentDef.VerificationMode ?? "global default"}");

        if (packs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available Rule Packs (tenant-wide behavior rules):");
            foreach (var p in packs.Take(10))
            {
                var status = p.IsEnabled ? "enabled" : "disabled";
                sb.AppendLine($"  - [{p.Id}] \"{p.Name}\" ({status}): {p.Description ?? "(no description)"}");
                if (p.Rules.Count > 0)
                {
                    var ruleDesc = string.Join(", ", p.Rules.Take(5)
                        .Select(r => $"{r.RuleType}{(r.Instruction is not null ? $":{Truncate(r.Instruction, 40)}" : "")}"));
                    sb.AppendLine($"    Rules: {ruleDesc}");
                }
            }
            sb.AppendLine("  When tool behavior or response structure issues are detected, suggest type \"RulePackSuggestion\"");
            sb.AppendLine("  with suggestedValue = pack ID (as string) and fieldName = pack name.");
            sb.AppendLine("  Prefer RulePackSuggestion over ToolStrategyHint when a matching disabled pack exists.");
        }

        sb.AppendLine();
        sb.AppendLine($"Suggest up to {_opts.Optimization.MaxSuggestionsPerRun} specific improvements as a JSON array. Each suggestion:");
        sb.AppendLine(@"[{
  ""type"": ""SystemPromptImprovement|TemperatureAdjustment|VerificationModeUpgrade|MaxIterationsAdjustment|MaxContinuationsAdjustment|ToolStrategyHint|ModelSwitch|ContextWindowAdjustment|RulePackSuggestion"",
  ""field_name"": ""systemPrompt|temperature|verificationMode|maxIterations|maxContinuations|toolStrategy|modelId|contextWindow|<packName>"",
  ""current_value"": ""current value as string"",
  ""suggested_value"": ""exact new value, improvement text, or pack ID for RulePackSuggestion"",
  ""confidence"": 0.0-1.0,
  ""reasoning"": ""specific explanation tied to the metrics above""
}]");
        sb.AppendLine("IMPORTANT: For VerificationModeUpgrade, ONLY use these exact values: Off | ToolGrounded | LlmVerifier | Strict | Auto");
        sb.AppendLine("Do NOT invent verification mode values like \"hybrid\", \"advanced\", or any other string.");
        sb.AppendLine("Return ONLY valid JSON array. No markdown, no explanation.");

        return sb.ToString();
    }

    private static string BuildMergePrompt(string currentPrompt, IReadOnlyList<string> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a system prompt editor. Integrate the suggested improvements into the existing system prompt.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- Preserve the original tone, structure, and intent of the current prompt");
        sb.AppendLine("- Integrate suggestions naturally — do not mechanically append them");
        sb.AppendLine("- Remove contradictions between current text and suggested improvements");
        sb.AppendLine("- Output ONLY the final merged prompt — no preamble, no meta-commentary, no explanation");
        sb.AppendLine("- Keep it concise — merge intelligently, don't expand unnecessarily");
        sb.AppendLine();
        sb.AppendLine("Current system prompt:");
        sb.AppendLine("---");
        sb.AppendLine(currentPrompt);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"Suggested improvements to integrate (apply ALL of them):");
        for (var i = 0; i < changes.Count; i++)
            sb.AppendLine($"{i + 1}. {changes[i]}");
        sb.AppendLine();
        sb.AppendLine("Merged system prompt:");
        return sb.ToString();
    }

    // ── LLM calls ─────────────────────────────────────────────────────────────

    private async Task<(string provider, string apiKey, string model, string? endpoint)>
        ResolveProviderAsync(AgentDefinitionEntity agentDef, CancellationToken ct)
    {
        ResolvedLlmConfig? resolved = null;
        if (agentDef.TenantId > 0)
        {
            try { resolved = await _resolver.ResolveAsync(agentDef.TenantId, agentDef.LlmConfigId, agentDef.ModelId, agentDef.EnvironmentId ?? 0, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "LlmConfigResolver failed for optimizer agent {AgentId} — using global fallback", agentDef.Id); }
        }

        var provider = resolved?.Provider ?? _llm.DirectProvider.Provider;
        var apiKey   = resolved?.ApiKey   ?? _llm.DirectProvider.ApiKey;
        var model    = resolved?.Model    ?? _llm.DirectProvider.Model;
        var endpoint = resolved is not null ? resolved.Endpoint : _llm.DirectProvider.Endpoint;

        // Non-Anthropic providers (Ollama, OpenAI-compatible) require an explicit endpoint.
        // The resolver clears the inherited endpoint when the provider changes, so if the
        // agent's LLM config doesn't have an explicit endpoint set, it will be null here.
        var isAnthropic = provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
        if (!isAnthropic && endpoint is null)
        {
            _logger.LogWarning(
                "Optimizer: agent {AgentId} uses provider '{Provider}' but no endpoint was resolved " +
                "(the LLM config is missing an Endpoint value). Falling back to global DirectProvider endpoint.",
                agentDef.Id, provider);
            endpoint = _llm.DirectProvider.Endpoint;
        }

        _logger.LogDebug(
            "Optimizer resolved: agent={AgentId} provider={Provider} model={Model} endpoint={Endpoint} llmConfigId={ConfigId}",
            agentDef.Id, provider, model, endpoint ?? "(none)", agentDef.LlmConfigId?.ToString() ?? "none");

        return (provider, apiKey, model, endpoint);
    }

    private Task<(string provider, string apiKey, string model, string? endpoint)>
        ResolveFallbackProviderAsync(CancellationToken ct) =>
        Task.FromResult((
            _llm.DirectProvider.Provider,
            _llm.DirectProvider.ApiKey,
            _llm.DirectProvider.Model,
            (string?)_llm.DirectProvider.Endpoint));

    private async Task<string> CallAnthropicAsync(
        string prompt, string apiKey, string model, int maxTokens, string systemMessage, CancellationToken ct)
    {
        using var httpClient = new System.Net.Http.HttpClient
            { Timeout = TimeSpan.FromSeconds(_llm.HttpTimeoutSeconds) };
        var client = new AnthropicClient(new APIAuthentication(apiKey), httpClient);
        var parameters = new MessageParameters
        {
            Model     = model,
            MaxTokens = maxTokens,
            System    = [new SystemMessage(systemMessage)],
            Messages  = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }]
        };
        var msg = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        return msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
    }

    private async Task<string> CallOpenAiCompatibleAsync(
        string prompt, string apiKey, string model, string? endpoint, int maxTokens, string systemMessage, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "no-key" : apiKey);
        var clientOpts = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(_llm.HttpTimeoutSeconds),
        };
        if (!string.IsNullOrEmpty(endpoint))
            clientOpts.Endpoint = new Uri(endpoint);
        var chatClient = new OpenAIClient(credential, clientOpts).GetChatClient(model);
        var messages = new ChatMessage[]
        {
            new SystemChatMessage(systemMessage),
            new UserChatMessage(prompt)
        };
        var result = await chatClient.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = maxTokens }, ct);
        return result.Value.Content.FirstOrDefault()?.Text ?? "";
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    private List<OptimizationSuggestionDto> ParseSuggestions(string raw, string agentId)
    {
        var json = Regex.Replace(raw.Trim(), @"^```json?\s*|\s*```$", "", RegexOptions.Multiline).Trim();
        var first = json.IndexOf('[');
        var last  = json.LastIndexOf(']');
        if (first < 0 || last < first) return [];
        json = json[first..(last + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var results   = new List<OptimizationSuggestionDto>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var type       = GetString(el, "type");
                var field      = GetString(el, "field_name");
                var suggested  = GetString(el, "suggested_value");
                var reasoning  = GetString(el, "reasoning");
                var confidence = el.TryGetProperty("confidence", out var c) ? (float)c.GetDouble() : 0f;

                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(suggested)) continue;
                if (confidence < _opts.Optimization.ConfidenceThreshold) continue;

                results.Add(new OptimizationSuggestionDto
                {
                    AgentId        = agentId,
                    Type           = type,
                    FieldName      = field ?? type,
                    CurrentValue   = GetString(el, "current_value"),
                    SuggestedValue = suggested,
                    Confidence     = confidence,
                    Reasoning      = reasoning ?? "",
                    Status         = "Pending",
                    CreatedAt      = DateTime.UtcNow
                });

                if (results.Count >= _opts.Optimization.MaxSuggestionsPerRun) break;
            }

            return results;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse optimization suggestions JSON");
            return [];
        }
    }

    private static string? GetString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
