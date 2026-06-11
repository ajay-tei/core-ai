using System.Text;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Context;

/// <summary>
/// Manages context window budget across all agent paths.
///
/// Point B (cross-run): sliding window over DB-loaded history; older turns → LLM or
///   rule-based summary injected into the system prompt.
/// Point A (in-run): cheap rule-based compaction before every LLM call; older in-run
///   tool-result pairs are replaced with a single summary message.
///
/// Per-agent overrides are accepted as an optional ContextWindowOverrideOptions that
/// is merged over the global ContextWindowOptions (null fields fall through to global).
/// </summary>
public sealed class ContextWindowManager : IContextWindowManager
{
    private readonly ContextWindowOptions _opts;
    private readonly LlmOptions _llm;
    private readonly IAnthropicProvider _anthropic;
    private readonly IOpenAiProvider _openAi;
    private readonly ISummarizationStrategy _summarizer;
    private readonly ILogger<ContextWindowManager> _logger;

    public ContextWindowManager(
        IOptions<AgentOptions> agentOpts,
        IOptions<LlmOptions> llm,
        IAnthropicProvider anthropic,
        IOpenAiProvider openAi,
        ILogger<ContextWindowManager> logger)
    {
        _opts = agentOpts.Value.ContextWindow;
        _llm = llm.Value;
        _anthropic = anthropic;
        _openAi = openAi;
        _summarizer = _llm.DirectProvider.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicSummarizationStrategy(anthropic, _opts.SummarizerMaxTokens)
            : new OpenAiSummarizationStrategy(openAi, _opts.SummarizerMaxTokens);
        _logger = logger;
    }

    // ── Per-agent merge ───────────────────────────────────────────────────────

    private ContextWindowOptions Effective(ContextWindowOverrideOptions? perAgent) =>
        perAgent is null ? _opts : new ContextWindowOptions
        {
            BudgetTokens = perAgent.BudgetTokens ?? _opts.BudgetTokens,
            CompactionThreshold = perAgent.CompactionThreshold ?? _opts.CompactionThreshold,
            KeepLastRawMessages = perAgent.KeepLastRawMessages ?? _opts.KeepLastRawMessages,
            MaxHistoryTurns = perAgent.MaxHistoryTurns ?? _opts.MaxHistoryTurns,
            SummarizerModel = perAgent.SummarizerModel ?? _opts.SummarizerModel,
        };

    // ── Point B ───────────────────────────────────────────────────────────────

    public async Task<(List<ConversationTurn> Turns, string? Summary)> CompactHistoryAsync(
        List<ConversationTurn> history,
        string? sessionModel = null,
        ContextWindowOverrideOptions? agentOverride = null,
        CancellationToken ct = default)
    {
        var opts = Effective(agentOverride);
        if (history.Count <= opts.MaxHistoryTurns)
            return (history, null);

        var offloaded = history[..^opts.MaxHistoryTurns];
        var recent = history[^opts.MaxHistoryTurns..];

        // Model priority: explicit config > session model > rule-based fallback
        var summarizerModel = !string.IsNullOrWhiteSpace(opts.SummarizerModel) ? opts.SummarizerModel
                            : !string.IsNullOrWhiteSpace(sessionModel) ? sessionModel
                            : null;

        string summary;
        if (!string.IsNullOrWhiteSpace(summarizerModel))
        {
            _logger.LogDebug(
                "LLM cross-run compaction with model {Model}: offloading {Count} turns",
                summarizerModel, offloaded.Count);
            try
            {
                summary = await SummarizeWithLlmAsync(offloaded, summarizerModel, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM summarisation failed — falling back to rule-based");
                summary = BuildRuleBasedSummary(offloaded);
            }
        }
        else
        {
            summary = BuildRuleBasedSummary(offloaded);
        }

        _logger.LogDebug("Cross-run compaction: kept {Kept}/{Total} turns verbatim",
            recent.Count, history.Count);
        return ([.. recent], summary);
    }

    // ── Point B — LLM summarisation ───────────────────────────────────────────

    private async Task<string> SummarizeWithLlmAsync(
        IReadOnlyList<ConversationTurn> turns, string model, CancellationToken ct)
    {
        var sb = new StringBuilder(
            "Summarise the following conversation turns into a concise bullet-point context " +
            "summary (max 200 words). Focus on key facts, decisions, and outcomes. Omit small talk.\n\n");
        foreach (var t in turns)
        {
            var snippet = t.Content.Replace('\n', ' ');
            sb.AppendLine($"{t.Role}: {snippet[..Math.Min(400, snippet.Length)]}");
        }

        return await _summarizer.SummarizeAsync(sb.ToString(), model, ct);
    }

    // ── Point B — rule-based fallback ─────────────────────────────────────────

    private static string BuildRuleBasedSummary(IReadOnlyList<ConversationTurn> offloaded)
    {
        var sb = new StringBuilder(
            $"Earlier session context ({offloaded.Count} turns, oldest first):\n");
        foreach (var turn in offloaded.Where(t => t.Role == "user").Take(6))
        {
            var snippet = turn.Content.Replace('\n', ' ');
            sb.AppendLine($"• {snippet[..Math.Min(140, snippet.Length)]}");
        }
        return sb.ToString().TrimEnd();
    }

    // ── Point A — shared generic core ────────────────────────────────────────

    /// <summary>
    /// Provider-agnostic: given flat text per message, returns whether to compact,
    /// which tail to keep verbatim, and the summary text for the middle.
    /// New providers call this and apply the result to their concrete message type.
    /// </summary>
    internal (bool ShouldCompact, int KeepLast, string SummaryText)
        ComputeCompactionPlan(
            IReadOnlyList<string> messageTexts,
            string systemText,
            ContextWindowOverrideOptions? agentOverride = null)
    {
        var opts = Effective(agentOverride);
        var totalEst = EstimateTokens(systemText) + messageTexts.Sum(EstimateTokens);
        var threshold = (int)(opts.BudgetTokens * opts.CompactionThreshold);

        if (totalEst <= threshold || messageTexts.Count <= opts.KeepLastRawMessages + 1)
            return (false, 0, string.Empty);

        var keep = opts.KeepLastRawMessages;
        var compactable = messageTexts.Skip(1).Take(messageTexts.Count - 1 - keep).ToList();
        var summaryText = "[Prior context in this run — compacted]\n" +
                          string.Join("\n", compactable.Select(t =>
                              $"• {t[..Math.Min(120, t.Length)]}"));

        _logger.LogWarning(
            "In-run context nearing limit (~{Est:N0} tokens). Compacting {Count} messages.",
            totalEst, messageTexts.Count);
        return (true, keep, summaryText);
    }

    // ── Point A — Anthropic adapter ───────────────────────────────────────────

    public List<Message> MaybeCompactAnthropicMessages(
        List<Message> messages,
        string systemPrompt,
        ContextWindowOverrideOptions? agentOverride = null)
    {
        if (messages.Count == 0) return messages;

        var texts = messages.Select(AnthropicMessageText).ToList();
        var (should, keepLast, summaryText) =
            ComputeCompactionPlan(texts, systemPrompt, agentOverride);
        if (!should) return messages;

        // Walk the tail start back until it lands on a clean boundary:
        // never start with a user message whose content is only ToolResultContent blocks —
        // that would leave tool_use blocks without a corresponding tool_result in the prior turn.
        var tailStart = messages.Count - keepLast;
        while (tailStart > 1 &&
               messages[tailStart].Role == RoleType.User &&
               messages[tailStart].Content.Count > 0 &&
               messages[tailStart].Content.All(c => c is Anthropic.SDK.Messaging.ToolResultContent))
        {
            tailStart--;
        }
        keepLast = messages.Count - tailStart;

        var summaryMsg = new Message
        {
            Role = RoleType.User,
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = summaryText }]
        };
        return [messages[0], summaryMsg, .. messages[^keepLast..]];
    }

    // ── Point A — OpenAI adapter ──────────────────────────────────────────────

    public List<ChatMessage> MaybeCompactChatMessages(
        List<ChatMessage> messages,
        ContextWindowOverrideOptions? agentOverride = null)
    {
        if (messages.Count < 3) return messages;

        var systemText = messages[0].Text ?? "";
        var bodyTexts = messages.Skip(1).Select(ChatMessageText).ToList();
        var (should, keepLast, summaryText) =
            ComputeCompactionPlan(bodyTexts, systemText, agentOverride);
        if (!should) return messages;

        // Walk the tail start back until it lands on a clean boundary:
        // never start with a ChatRole.Tool message — that would leave the preceding
        // assistant tool_use block without a corresponding tool_result in the kept tail.
        var tailStart = messages.Count - keepLast;
        while (tailStart > 1 && messages[tailStart].Role == ChatRole.Tool)
            tailStart--;
        keepLast = messages.Count - tailStart;

        var summaryMsg = new ChatMessage(ChatRole.User, summaryText);
        return [messages[0], messages[1], summaryMsg, .. messages[^keepLast..]];
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    internal static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    internal static string AnthropicMessageText(Message msg) =>
        string.Join(" ", msg.Content.Select(c => c switch
        {
            Anthropic.SDK.Messaging.TextContent tc => tc.Text ?? "",
            Anthropic.SDK.Messaging.ToolResultContent tr => string.Join(" ",
                tr.Content?.OfType<Anthropic.SDK.Messaging.TextContent>()
                           .Select(t => t.Text) ?? []),
            ToolUseContent tu => tu.Input?.ToString() ?? "",
            _ => ""
        }));

    /// <summary>
    /// Extracts a text representation of a <see cref="ChatMessage"/> for token estimation,
    /// including text, function-call arguments, and function-result content.
    /// </summary>
    internal static string ChatMessageText(ChatMessage msg)
    {
        var parts = new List<string>();
        foreach (var c in msg.Contents)
        {
            switch (c)
            {
                case Microsoft.Extensions.AI.TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    parts.Add(tc.Text!);
                    break;
                case FunctionResultContent fr when fr.Result is not null:
                    parts.Add(fr.Result.ToString() ?? "");
                    break;
                case FunctionCallContent fc when fc.Arguments is not null:
                    parts.Add(System.Text.Json.JsonSerializer.Serialize(fc.Arguments));
                    break;
            }
        }
        return string.Join(" ", parts);
    }
}

// ── Summarization strategies ──────────────────────────────────────────────────

internal interface ISummarizationStrategy
{
    Task<string> SummarizeAsync(string prompt, string model, CancellationToken ct);
}

internal sealed class AnthropicSummarizationStrategy(IAnthropicProvider anthropic, int maxTokens) : ISummarizationStrategy
{
    public async Task<string> SummarizeAsync(string prompt, string model, CancellationToken ct)
    {
        var parameters = new MessageParameters
        {
            Model = model,
            MaxTokens = maxTokens,
            System = [new SystemMessage("You are a concise summariser. Reply only with bullet points.")],
            Messages =
            [
                new Message
                {
                    Role    = RoleType.User,
                    Content = [new Anthropic.SDK.Messaging.TextContent { Text = prompt }]
                }
            ]
        };
        var msg = await anthropic.GetClaudeMessageAsync(parameters, ct);
        return "Earlier session context (LLM summary):\n" +
               (msg.Content.OfType<Anthropic.SDK.Messaging.TextContent>()
                           .FirstOrDefault()?.Text ?? "(no summary)");
    }
}

internal sealed class OpenAiSummarizationStrategy(IOpenAiProvider openAi, int maxTokens) : ISummarizationStrategy
{
    public async Task<string> SummarizeAsync(string prompt, string model, CancellationToken ct)
    {
        var client = openAi.CreateChatClient(model);
        var oaiMessages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a concise summariser. Reply only with bullet points."),
            new(ChatRole.User, prompt)
        };
        var result = await client.GetResponseAsync(
            oaiMessages, new ChatOptions { MaxOutputTokens = maxTokens }, ct);
        return "Earlier session context (LLM summary):\n" + (result.Text ?? "(no summary)");
    }
}
