using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Context;
using Diva.Infrastructure.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// <see cref="ILlmProviderStrategy"/> implementation for the Anthropic native SDK.
/// Created per-execution — holds mutable <see cref="Message"/> list + provider refs.
///
/// Prompt caching strategy (Anthropic breakpoints managed manually via PromptCacheType.FineGrained):
///   BP1 — static system block (base prompt + overrides + group rules): cache_control set in BuildSystemBlocks()
///   BP2 — tool definitions:                                            NOT set manually (Anthropic.SDK.Common.Tool
///          has no CacheControl property in SDK 5.10.0; relies on Anthropic API auto-caching tools when present)
///   BP3 — last prior-session history message:                          cache_control set in Initialize()
///   BP4 — most-recent tool-result message (sliding):                   cache_control moved in AddToolResults()
/// </summary>
internal sealed class AnthropicProviderStrategy : ILlmProviderStrategy
{
    private readonly IAnthropicProvider _anthropic;
    private readonly IContextWindowManager _ctx;
    private string _model;
    private int _maxTokens;
    private readonly bool _enableHistoryCaching;
    private readonly bool _enableThinking;
    private readonly int _thinkingBudget;
    private string? _apiKeyOverride;
    private readonly Func<Func<Task<MessageResponse>>, CancellationToken, Task<MessageResponse>> _retry;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    // System prompt split: static (cacheable across sessions) + dynamic (volatile per session).
    private string _staticSystemPrompt;    // stable: base + group/tenant overrides + group rules
    private string _dynamicSystemPrompt;   // volatile: session rules, hook injections, caller instructions

    private List<Message> _messages = [];
    private IList<Anthropic.SDK.Common.Tool>? _tools;
    private readonly HashSet<string> _toolNames = new(StringComparer.Ordinal);   // guards against duplicate tool names
    private MessageResponse? _lastResponse;
    private List<ContentBase>? _lastStreamedContent;
    private TokenUsage _lastTokenUsage;

    // Tracks the tool-results message that currently holds the sliding BP4 cache breakpoint.
    private Message? _slidingCacheBoundary;

    // Newer Claude models (e.g. sonnet-5) reject "thinking.type.enabled"/budget_tokens and require
    // "thinking.type.adaptive" + output_config.effort. We default to the budget API (Claude 3.7/4.x)
    // and flip this flag on the first adaptive-required error, self-healing for the rest of the run.
    private bool _useAdaptiveThinking;

    // Set by SuppressThinkingForRun(): disables thinking for the rest of the run (and strips thinking
    // blocks from history so requests stay valid). Sticky — must NOT re-enable, or a tool call would
    // let the following turn go thinking-only again. Used to force a plain-text answer out of a model
    // that keeps ending its turn with thinking only.
    private bool _suppressThinkingSticky;

    // Extended-thinking text accumulated on the most recent streamed call (null when none), surfaced
    // as a last-resort answer when a turn ends with reasoning only and no visible text.
    private string? _lastThinkingText;

    public AnthropicProviderStrategy(
        IAnthropicProvider anthropic,
        IContextWindowManager ctx,
        string model,
        int maxTokens,
        string staticSystemPrompt,
        string dynamicSystemPrompt,
        Func<Func<Task<MessageResponse>>, CancellationToken, Task<MessageResponse>> retry,
        bool enableHistoryCaching = true,
        string? apiKeyOverride = null,
        bool enableThinking = false,
        int thinkingBudget = 0,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _anthropic = anthropic;
        _ctx = ctx;
        _model = model;
        _maxTokens = maxTokens;
        _staticSystemPrompt = staticSystemPrompt;
        _dynamicSystemPrompt = dynamicSystemPrompt;
        _enableHistoryCaching = enableHistoryCaching;
        _enableThinking = enableThinking;
        _thinkingBudget = thinkingBudget;
        _apiKeyOverride = apiKeyOverride;
        _retry = retry;
        _logger = logger;
    }

    /// <summary>
    /// Applies extended-thinking parameters to <paramref name="p"/>, or leaves them unset when
    /// thinking is disabled. Uses the budget-based API (Claude 3.7/4.x) by default and switches to
    /// the adaptive + effort API (newer models) once <see cref="_useAdaptiveThinking"/> is set.
    /// </summary>
    private void ApplyThinking(MessageParameters p)
    {
        if (_suppressThinkingSticky)
        {
            // Thinking disabled for the rest of the run — the API rejects thinking blocks in history
            // when thinking is off, so strip them. History always ends on a user message, so stripping
            // thinking blocks from prior assistant (tool_use) turns still yields a valid sequence.
            StripThinkingBlocks();
            return;
        }

        if (!_enableThinking || _thinkingBudget <= 0)
            return;

        if (_useAdaptiveThinking)
        {
            // Adaptive thinking carries no budget_tokens; effort lives on output_config.
            // UseInterleavedThinking makes the SDK send the interleaved-thinking-2025-05-14 beta header,
            // which lets the model emit plaintext thinking blocks *between* tool calls (streamed as
            // thinking_delta events) instead of a single opaque, signature-only block.
            p.Thinking = new ThinkingParameters { Type = ThinkingType.adaptive, UseInterleavedThinking = true };
            p.OutputConfig = new OutputConfig { Effort = MapBudgetToEffort() };
        }
        else
        {
            p.Thinking = new ThinkingParameters { Type = ThinkingType.enabled, BudgetTokens = _thinkingBudget, UseInterleavedThinking = true };
        }
    }

    /// <summary>Removes thinking / redacted-thinking blocks from every message in history.</summary>
    private void StripThinkingBlocks()
    {
        foreach (var m in _messages)
            m.Content?.RemoveAll(b => b is ThinkingContent or RedactedThinkingContent);
    }

    /// <summary>Maps the configured token budget onto the coarse adaptive effort levels.</summary>
    private ThinkingEffort MapBudgetToEffort() => _thinkingBudget switch
    {
        <= 4096 => ThinkingEffort.low,
        <= 12000 => ThinkingEffort.medium,
        <= 24000 => ThinkingEffort.high,
        _ => ThinkingEffort.max,
    };

    /// <summary>True when the API rejected budget-based thinking and asked for the adaptive + effort API.</summary>
    private static bool IsAdaptiveThinkingError(Exception ex) =>
        ex.Message.Contains("thinking.type.adaptive", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("output_config.effort", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void SuppressThinkingForRun() => _suppressThinkingSticky = true;

    /// <inheritdoc/>
    public string? LastThinkingText => _lastThinkingText;

    // ── System block builder ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the System parameter for Anthropic API calls.
    /// When caching is enabled: two blocks — static (BP1, ephemeral) + dynamic (no marker).
    /// When caching is disabled: one combined block.
    /// </summary>
    private List<SystemMessage> BuildSystemBlocks()
    {
        var blocks = new List<SystemMessage>();

        if (_enableHistoryCaching)
        {
            // BP1: static block — marked ephemeral; stable across sessions for same agent+tenant.
            var staticBlock = new SystemMessage(_staticSystemPrompt);
            staticBlock.CacheControl = new CacheControl { Type = CacheControlType.ephemeral };
            blocks.Add(staticBlock);

            // Dynamic block — no cache_control; changes per session.
            if (!string.IsNullOrEmpty(_dynamicSystemPrompt))
                blocks.Add(new SystemMessage(_dynamicSystemPrompt));
        }
        else
        {
            // Caching disabled: single combined block (matches pre-caching behaviour).
            var combined = string.IsNullOrEmpty(_dynamicSystemPrompt)
                ? _staticSystemPrompt
                : _staticSystemPrompt + "\n\n" + _dynamicSystemPrompt;
            blocks.Add(new SystemMessage(combined));
        }

        return blocks;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    public void Initialize(
        string systemPrompt,
        List<ConversationTurn> history,
        string userQuery,
        List<McpClientTool> mcpTools,
        IReadOnlyList<ContentPart>? attachments = null)
    {
        _messages = new List<Message>();
        _slidingCacheBoundary = null;

        foreach (var turn in history)
            _messages.Add(new Message
            {
                Role = turn.Role == "assistant" ? RoleType.Assistant : RoleType.User,
                Content = [new Anthropic.SDK.Messaging.TextContent { Text = turn.Content }]
            });

        // BP3: mark the last history message's last content block as cacheable.
        // Caches everything before the current user query (BP1+BP2+history → single cache hit).
        if (_enableHistoryCaching && _messages.Count > 0)
        {
            var lastHist = _messages[^1];
            if (lastHist.Content is { Count: > 0 })
                lastHist.Content[^1].CacheControl = new CacheControl { Type = CacheControlType.ephemeral };
        }

        // Attachments (images/docs) go BEFORE the text block per Anthropic best practice.
        var userContent = new List<ContentBase>();
        foreach (var part in attachments ?? [])
        {
            switch (part)
            {
                case ImageContentPart img:
                    userContent.Add(new ImageContent
                    {
                        Source = new ImageSource
                        {
                            Type = img.Data is not null ? SourceType.base64 : SourceType.url,
                            MediaType = img.MediaType,
                            Data = img.Data,
                            Url = img.Url
                        }
                    });
                    break;
                case DocumentContentPart doc:
                    userContent.Add(new DocumentContent
                    {
                        Title = doc.Title,
                        Source = new DocumentSource
                        {
                            Type = doc.Data is not null ? SourceType.base64 : SourceType.url,
                            MediaType = doc.MediaType,
                            Data = doc.Data,
                            Url = doc.Url
                        }
                    });
                    break;
            }
        }
        userContent.Add(new Anthropic.SDK.Messaging.TextContent { Text = userQuery });
        _messages.Add(new Message { Role = RoleType.User, Content = userContent });

        SetTools(mcpTools);
        // Note: Anthropic.SDK.Common.Tool has no CacheControl property; tool caching is
        // handled by PromptCacheType.AutomaticToolsAndSystem in the request parameters.
    }

    /// <summary>
    /// Builds <see cref="_tools"/> from the supplied functions, dropping any duplicate tool names.
    /// The Anthropic API (and the tool schema in general) requires globally-unique tool names, so a
    /// name collision from multiple MCP servers or delegate agents would otherwise fail the request.
    /// </summary>
    private void SetTools(IReadOnlyList<AIFunction> source)
    {
        _toolNames.Clear();
        if (source.Count == 0) { _tools = null; return; }
        var list = new List<Anthropic.SDK.Common.Tool>(source.Count);
        foreach (var t in source)
            if (_toolNames.Add(t.Name))
                list.Add(ToAnthropicTool(t));
        _tools = list.Count > 0 ? list : null;
    }

    /// <inheritdoc/>
    public void AddExtraTools(IReadOnlyList<AIFunction> tools)
    {
        if (tools.Count == 0) return;
        _tools ??= new List<Anthropic.SDK.Common.Tool>();
        foreach (var tool in tools)
            if (_toolNames.Add(tool.Name))   // skip names already present to keep tool names unique
                _tools.Add(ToAnthropicTool(tool));
    }

    // ── LLM calls ─────────────────────────────────────────────────────────────

    public async Task<UnifiedLlmResponse> CallLlmAsync(CancellationToken ct)
    {
        // Clear any stale streamed content so CommitAssistantResponse uses _lastResponse.
        _lastStreamedContent = null;

        var parameters = new MessageParameters
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = BuildSystemBlocks(),
            Messages = _messages,
            Tools = _tools,
            ToolChoice = _tools is { Count: > 0 }
                ? new ToolChoice { Type = ToolChoiceType.Auto, DisableParallelToolUse = false }
                : null,
            PromptCaching = PromptCacheType.FineGrained   // respects CacheControl on individual blocks
        };
        ApplyThinking(parameters);

        MessageResponse response;
        try
        {
            response = await _retry(() => _anthropic.GetClaudeMessageAsync(parameters, ct, _apiKeyOverride), ct);
        }
        catch (Exception ex) when (_enableThinking && !_useAdaptiveThinking && IsAdaptiveThinkingError(ex))
        {
            _logger?.LogInformation(
                "Model '{Model}' requires adaptive extended thinking — switching from budget to effort mode.", _model);
            _useAdaptiveThinking = true;
            ApplyThinking(parameters);   // rebuild thinking + output_config on the same params object
            response = await _retry(() => _anthropic.GetClaudeMessageAsync(parameters, ct, _apiKeyOverride), ct);
        }
        _lastResponse = response;
        _lastTokenUsage = new TokenUsage(
            response.Usage?.InputTokens ?? 0,
            response.Usage?.OutputTokens ?? 0,
            response.Usage?.CacheReadInputTokens ?? 0,
            response.Usage?.CacheCreationInputTokens ?? 0);

        var text = string.Join("\n", response.Content
            .OfType<Anthropic.SDK.Messaging.TextContent>()
            .Select(b => b.Text));

        var toolCalls = response.StopReason == "tool_use"
            ? response.Content.OfType<ToolUseContent>()
                .Select(tu => new UnifiedToolCall(tu.Id, tu.Name, tu.Input?.ToString() ?? "{}"))
                .ToList()
            : [];

        _logger?.LogInformation(
            "[PARALLEL-DIAG buffered] stopReason={Stop} rawToolUseBlocks={Raw} capturedToolCalls={Captured} names=[{Names}]",
            response.StopReason ?? "null",
            response.Content.OfType<ToolUseContent>().Count(),
            toolCalls.Count,
            string.Join(", ", toolCalls.Select(t => t.Name)));

        return new UnifiedLlmResponse(text, toolCalls, toolCalls.Count > 0, response.StopReason);
    }

    /// <inheritdoc/>
    public TokenUsage LastTokenUsage => _lastTokenUsage;

    public async IAsyncEnumerable<UnifiedStreamDelta> StreamLlmAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new MessageParameters
        {
            Model = _model,
            MaxTokens = _maxTokens,
            System = BuildSystemBlocks(),
            Messages = _messages,
            Tools = _tools,
            ToolChoice = _tools is { Count: > 0 }
                ? new ToolChoice { Type = ToolChoiceType.Auto, DisableParallelToolUse = false }
                : null,
            PromptCaching = PromptCacheType.FineGrained   // respects CacheControl on individual blocks
        };
        ApplyThinking(parameters);

        // Clear stale state from any previous call so CommitAssistantResponse always reflects
        // this iteration. If the stream throws before we set _lastStreamedContent, the
        // fallback CallLlmAsync will set _lastResponse instead.
        _lastStreamedContent = null;
        _lastResponse = null;

        var outputs = new List<MessageResponse>();
        string accText = "";
        string accThinking = "";      // accumulated extended-thinking (reasoning) text, all blocks concatenated

        // Faithful ordered reconstruction of the assistant turn's content blocks. Interleaved thinking
        // (beta) emits MULTIPLE thinking blocks — each with its own signature — interleaved with
        // tool_use and text blocks. Anthropic requires every thinking block to be replayed with its
        // signature and in original order relative to the tool_use blocks it precedes; otherwise the
        // follow-up request carrying the tool results is rejected. We therefore rebuild blocks in the
        // exact stream order (content_block_start → *_delta* → content_block_stop) instead of flattening
        // thinking/text/tools into fixed positions.
        var orderedBlocks = new List<ContentBase>();
        var toolCalls = new List<UnifiedToolCall>();
        var seenToolIds = new HashSet<string>(StringComparer.Ordinal);

        // Current in-flight content block state.
        string curBlockType = "";
        var curText = new StringBuilder();
        var curThinking = new StringBuilder();
        string? curSignature = null;
        string? curRedactedData = null;
        string? curToolId = null;
        string? curToolName = null;
        var curToolJson = new StringBuilder();

        // Finalises the in-flight block into orderedBlocks (and toolCalls for tool_use), then resets.
        void FlushBlock()
        {
            switch (curBlockType)
            {
                case "thinking":
                    // Preserve even a text-less thinking block when it carries a signature — the API
                    // needs the signed block replayed to accept the tool-result follow-up.
                    if (curThinking.Length > 0 || !string.IsNullOrEmpty(curSignature))
                        orderedBlocks.Add(new ThinkingContent
                        {
                            Thinking = curThinking.ToString(),
                            Signature = curSignature ?? string.Empty
                        });
                    break;
                case "redacted_thinking":
                    if (!string.IsNullOrEmpty(curRedactedData))
                        orderedBlocks.Add(new RedactedThinkingContent { Data = curRedactedData! });
                    break;
                case "text":
                    if (curText.Length > 0)
                        orderedBlocks.Add(new Anthropic.SDK.Messaging.TextContent { Text = curText.ToString() });
                    break;
                case "tool_use":
                    if (!string.IsNullOrEmpty(curToolId) && seenToolIds.Add(curToolId!))
                    {
                        var rawArgs = curToolJson.Length == 0 ? "{}" : curToolJson.ToString();
                        JsonNode? inputNode;
                        try { inputNode = JsonNode.Parse(rawArgs); }
                        catch (JsonException) { inputNode = null; }
                        if (inputNode is null) { inputNode = new JsonObject(); rawArgs = "{}"; }
                        orderedBlocks.Add(new ToolUseContent { Id = curToolId!, Name = curToolName ?? string.Empty, Input = inputNode });
                        toolCalls.Add(new UnifiedToolCall(curToolId!, curToolName ?? string.Empty, rawArgs));
                    }
                    break;
            }
            curBlockType = "";
            curText.Clear();
            curThinking.Clear();
            curSignature = null;
            curRedactedData = null;
            curToolId = null;
            curToolName = null;
            curToolJson.Clear();
        }

        await foreach (var response in _anthropic.StreamClaudeMessageAsync(parameters, ct, _apiKeyOverride))
        {
            outputs.Add(response);

            // content_block_start — close the previous block (safety) and open a new one.
            if (response.ContentBlock is { } cb)
            {
                FlushBlock();
                curBlockType = cb.Type ?? string.Empty;
                if (curBlockType == "tool_use")
                {
                    curToolId = cb.Id;
                    curToolName = cb.Name;
                }
                else if (curBlockType == "redacted_thinking")
                {
                    curRedactedData = cb.Data;
                }
            }

            var delta = response.Delta;
            if (delta is not null)
            {
                if (!string.IsNullOrEmpty(delta.Text))
                {
                    accText += delta.Text;
                    curText.Append(delta.Text);
                    yield return new UnifiedStreamDelta(delta.Text, false, null);
                }
                // Extended-thinking (reasoning) deltas. With interleaved thinking these carry plaintext
                // and stream live to the UI. Inert for non-thinking turns (Delta.Thinking null).
                if (!string.IsNullOrEmpty(delta.Thinking))
                {
                    accThinking += delta.Thinking;
                    curThinking.Append(delta.Thinking);
                    yield return new UnifiedStreamDelta(null, false, null, ThinkingDelta: delta.Thinking);
                }
                if (!string.IsNullOrEmpty(delta.Signature))
                    curSignature = delta.Signature;
                if (curBlockType == "tool_use"
                    && string.Equals(delta.Type, "input_json_delta", StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(delta.PartialJson))
                    curToolJson.Append(delta.PartialJson);
            }

            // content_block_stop — finalise the current block.
            if (string.Equals(response.Type, "content_block_stop", StringComparison.Ordinal))
                FlushBlock();
        }

        // Finalise any trailing block (content_block_stop normally handles this).
        FlushBlock();

        // Preserve reasoning text for a last-resort answer if this turn ends thinking-only.
        _lastThinkingText = string.IsNullOrEmpty(accThinking) ? null : accThinking;

        // ── Token usage ───────────────────────────────────────────────────────
        // input tokens are on the message_start event (StreamStartMessage.Usage);
        // output tokens are on message_delta (last event with Usage.OutputTokens > 0).
        var startEvent = outputs.FirstOrDefault(r => r.StreamStartMessage?.Usage is not null);
        var finalUsage = outputs.LastOrDefault(r => r.Usage?.OutputTokens > 0);
        _lastTokenUsage = new TokenUsage(
            startEvent?.StreamStartMessage?.Usage?.InputTokens ?? 0,
            finalUsage?.Usage?.OutputTokens ?? 0,
            startEvent?.StreamStartMessage?.Usage?.CacheReadInputTokens ?? 0,
            startEvent?.StreamStartMessage?.Usage?.CacheCreationInputTokens ?? 0);

        // ── Assistant turn content already reconstructed in stream order above ──
        // orderedBlocks holds thinking/redacted/text/tool_use blocks in their original sequence and
        // toolCalls holds the parsed tool calls. Fall back to the SDK's parsed ToolCalls only when
        // in-loop reconstruction captured no tools at all (defensive; the raw path is authoritative).
        var last = outputs.Count > 0 ? outputs[^1] : null;

        int eventsWithTools = 0;  // number of stream events that carried SDK-parsed tool calls (diagnostic)
        int maxPerEvent = 0;      // largest ToolCalls.Count seen on any single stream event (diagnostic)
        foreach (var resp in outputs)
        {
            if (resp.ToolCalls is not { Count: > 0 } funcs) continue;
            eventsWithTools++;
            if (funcs.Count > maxPerEvent) maxPerEvent = funcs.Count;
            if (toolCalls.Count > 0) continue;   // raw reconstruction already won — diagnostics only
            foreach (var f in funcs)
            {
                if (string.IsNullOrEmpty(f.Id) || !seenToolIds.Add(f.Id)) continue;

                // f.Arguments from streaming is JsonValuePrimitive<string> — not a JsonObject.
                // ToString() returns the raw JSON text; re-parse to get a proper JsonObject.
                // Guard against null/empty (tool with no args produces "" not "{}" in the SDK).
                var rawArgs = f.Arguments?.ToString();
                if (string.IsNullOrWhiteSpace(rawArgs)) rawArgs = "{}";
                var inputNode = JsonNode.Parse(rawArgs) ?? new JsonObject();
                orderedBlocks.Add(new ToolUseContent { Id = f.Id, Name = f.Name, Input = inputNode });
                toolCalls.Add(new UnifiedToolCall(f.Id, f.Name, rawArgs));
            }
        }
        _lastStreamedContent = orderedBlocks.Count > 0 ? orderedBlocks : null;

        // A turn with tool calls is a tool_use turn regardless of the SDK-reported stop reason
        // (which is end_turn when thinking is interleaved with the tool calls).
        var stopReason = toolCalls.Count > 0 ? "tool_use" : (last?.StopReason ?? "end_turn");

        _logger?.LogInformation(
            "[PARALLEL-DIAG streaming] stopReason={Stop} streamEvents={Events} eventsWithTools={EventsWithTools} maxToolsPerEvent={MaxPerEvent} capturedToolCalls={Captured} orderedBlocks={OrderedBlocks} thinkingBlocks={ThinkingBlocks} accText={AccText} accThinking={AccThinking} names=[{Names}]",
            stopReason, outputs.Count, eventsWithTools, maxPerEvent, toolCalls.Count,
            orderedBlocks.Count, orderedBlocks.OfType<ThinkingContent>().Count(), accText.Length, accThinking.Length,
            string.Join(", ", toolCalls.Select(t => t.Name)));

        // When a turn ends with no captured text, thinking, or tools, dump the raw event shape so we
        // can see where the output tokens went (delta types / content-block types / event types).
        if (accText.Length == 0 && accThinking.Length == 0 && toolCalls.Count == 0)
        {
            string GroupCounts(IEnumerable<string?> items) => string.Join(", ",
                items.Select(x => string.IsNullOrEmpty(x) ? "<null>" : x)
                     .GroupBy(x => x)
                     .Select(g => $"{g.Key}={g.Count()}"));

            _logger?.LogWarning(
                "[PARALLEL-DIAG EMPTY] deltaTypes=[{DeltaTypes}] eventTypes=[{EventTypes}] blockTypes=[{BlockTypes}] partialJsonLen={PartialJson}",
                GroupCounts(outputs.Select(o => o.Delta?.Type)),
                GroupCounts(outputs.Select(o => o.Type)),
                GroupCounts(outputs.Select(o => o.ContentBlock?.Type)),
                outputs.Sum(o => o.Delta?.PartialJson?.Length ?? 0));
        }

        yield return new UnifiedStreamDelta(null, true,
            new UnifiedLlmResponse(accText, toolCalls, toolCalls.Count > 0, stopReason));
    }

    public async Task<string?> CallReplanAsync(CancellationToken ct)
    {
        var parameters = new MessageParameters
        {
            Model = _model,
            MaxTokens = 2048,
            System = BuildSystemBlocks(),
            Messages = _messages,
            Tools = null,
            PromptCaching = PromptCacheType.FineGrained   // respects CacheControl on individual blocks
        };

        var response = await _anthropic.GetClaudeMessageAsync(parameters, ct, _apiKeyOverride);
        var text = string.Join("\n", response.Content
            .OfType<Anthropic.SDK.Messaging.TextContent>()
            .Select(b => b.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    // ── Message mutations ─────────────────────────────────────────────────────

    public void CommitAssistantResponse()
    {
        if (_lastStreamedContent is not null)
        {
            _messages.Add(new Message { Role = RoleType.Assistant, Content = _lastStreamedContent });
            _lastStreamedContent = null;
            return;
        }
        if (_lastResponse is null) return;
        _messages.Add(new Message { Role = RoleType.Assistant, Content = _lastResponse.Content });
    }

    private static List<ContentBase> BuildToolResultContent(UnifiedToolResult r)
    {
        if (r.ContentParts is not { Count: > 0 })
            return [new Anthropic.SDK.Messaging.TextContent { Text = r.Output }];

        var blocks = new List<ContentBase>();
        foreach (var part in r.ContentParts)
        {
            switch (part)
            {
                case ImageContentPart img:
                    blocks.Add(new ImageContent
                    {
                        Source = new ImageSource
                        {
                            Type = img.Data is not null ? SourceType.base64 : SourceType.url,
                            MediaType = img.MediaType,
                            Data = img.Data,
                            Url = img.Url
                        }
                    });
                    break;
                case TextContentPart tp:
                    blocks.Add(new Anthropic.SDK.Messaging.TextContent { Text = tp.Text });
                    break;
            }
        }
        // Always append text output as a summary when no TextContentPart is present.
        if (!r.ContentParts.OfType<TextContentPart>().Any() && !string.IsNullOrEmpty(r.Output))
            blocks.Add(new Anthropic.SDK.Messaging.TextContent { Text = r.Output });
        return blocks;
    }

    public void AddToolResults(IReadOnlyList<UnifiedToolResult> results)
    {
        var toolResults = results.Select(r => (ContentBase)new Anthropic.SDK.Messaging.ToolResultContent
        {
            ToolUseId = r.ToolCallId,
            Content = BuildToolResultContent(r),
            IsError = r.IsError
        }).ToList();

        var newMessage = new Message { Role = RoleType.User, Content = toolResults };
        _messages.Add(newMessage);

        // BP4 (sliding): move the cache marker to the newest tool-result block.
        // Clear the old marker first so we never have more than 4 total breakpoints.
        if (_enableHistoryCaching)
        {
            if (_slidingCacheBoundary?.Content is { Count: > 0 } prev)
                prev[^1].CacheControl = null;

            if (newMessage.Content.Count > 0)
                newMessage.Content[^1].CacheControl = new CacheControl { Type = CacheControlType.ephemeral };

            _slidingCacheBoundary = newMessage;
        }
    }

    public void AddUserMessage(string text) =>
        _messages.Add(new Message
        {
            Role = RoleType.User,
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = text }]
        });

    public void AddAssistantThenUser(string assistantText, string userText)
    {
        _messages.Add(new Message
        {
            Role = RoleType.Assistant,
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = assistantText }]
        });
        _messages.Add(new Message
        {
            Role = RoleType.User,
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = userText }]
        });
    }

    /// <summary>
    /// Updates the dynamic (volatile) system prompt block only.
    /// Called by hooks that inject per-iteration content (rule packs, session context).
    /// The static block (_staticSystemPrompt) is never mutated after construction.
    /// </summary>
    public void UpdateSystemPrompt(string dynamicSystemPrompt) => _dynamicSystemPrompt = dynamicSystemPrompt;

    // ── Context window management ─────────────────────────────────────────────

    public void CompactHistory(string systemPrompt, ContextWindowOverrideOptions? agentOverride)
    {
        _messages = _ctx.MaybeCompactAnthropicMessages(_messages, systemPrompt, agentOverride);
        _slidingCacheBoundary = null;   // _messages is a new allocation; old refs are stale

        // After compaction, orphaned CacheControl markers from BP3/BP4 may survive on kept
        // messages (the tail is copied by reference). Strip them all, then re-set BP3 so
        // we never exceed Anthropic's 4-breakpoint limit.
        if (_enableHistoryCaching)
            ResetCacheMarkersAfterCompaction();
    }

    public void PrepareNewWindow(string continuationContext, string systemPrompt, ContextWindowOverrideOptions? agentOverride)
    {
        _messages = _ctx.MaybeCompactAnthropicMessages(_messages, systemPrompt, agentOverride);
        _slidingCacheBoundary = null;

        if (_enableHistoryCaching)
            ResetCacheMarkersAfterCompaction();

        _messages.Add(new Message
        {
            Role = RoleType.User,
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = continuationContext }]
        });
        // No cache_control on the continuation context message — it is short, changes every
        // window, and a breakpoint here would consume BP4 before any tool results appear.
    }

    /// <summary>
    /// Clears all CacheControl markers from message content blocks, then re-sets BP3
    /// on the last message before the user query (if history exists).
    /// Called after compaction to prevent orphaned markers from accumulating past
    /// the Anthropic API's 4-breakpoint limit.
    /// </summary>
    private void ResetCacheMarkersAfterCompaction()
    {
        // Strip all CacheControl from every content block in the message list.
        foreach (var msg in _messages)
        {
            if (msg.Content is null) continue;
            foreach (var block in msg.Content)
                block.CacheControl = null;
        }

        // Re-set BP3: mark the last message before the trailing user query as cacheable.
        // After compaction the structure is [first, summary, ...kept_tail].
        // The "last history" message to cache is the one just before the final user message.
        if (_messages.Count >= 2)
        {
            var lastHist = _messages[^1]; // best candidate — last message in compacted list
            // Walk back to find a message with content (skip empty).
            for (int j = _messages.Count - 1; j >= 0; j--)
            {
                if (_messages[j].Content is { Count: > 0 })
                {
                    _messages[j].Content[^1].CacheControl = new CacheControl { Type = CacheControlType.ephemeral };
                    break;
                }
            }
        }
    }

    // ── Per-iteration model switching ─────────────────────────────────────────

    /// <inheritdoc/>
    public void SetModel(string model, int? maxTokens = null, string? apiKeyOverride = null, string? endpointOverride = null)
    {
        _model = model;
        if (maxTokens.HasValue) _maxTokens = maxTokens.Value;
        if (apiKeyOverride is not null) _apiKeyOverride = apiKeyOverride;
        // endpointOverride ignored for Anthropic SDK (no per-call endpoint support)
    }

    // ── Cross-provider history transfer ───────────────────────────────────────

    /// <inheritdoc/>
    public List<UnifiedHistoryEntry> ExportHistory()
    {
        var result = new List<UnifiedHistoryEntry>();
        foreach (var msg in _messages)
        {
            var parts = new List<UnifiedHistoryPart>();
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case Anthropic.SDK.Messaging.TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        parts.Add(new TextHistoryPart(tc.Text));
                        break;
                    case ToolUseContent tu:
                        parts.Add(new ToolCallHistoryPart(tu.Id, tu.Name, tu.Input?.ToString() ?? "{}"));
                        break;
                    case Anthropic.SDK.Messaging.ToolResultContent tr:
                        var output = tr.Content?.OfType<Anthropic.SDK.Messaging.TextContent>()
                                        .FirstOrDefault()?.Text ?? "";
                        parts.Add(new ToolResultHistoryPart(tr.ToolUseId, output, tr.IsError ?? false));
                        break;
                }
            }
            if (parts.Count > 0)
                result.Add(new UnifiedHistoryEntry
                {
                    Role = msg.Role == RoleType.Assistant ? "assistant" : "user",
                    Parts = parts
                });
        }
        return result;
    }

    /// <inheritdoc/>
    public void ImportHistory(List<UnifiedHistoryEntry> history, string systemPrompt, List<McpClientTool> tools)
    {
        // After a cross-provider model switch the coordinator only has the combined system prompt.
        // Treat it as the static block; dynamic starts empty (hooks will re-inject at OnBeforeIteration).
        _staticSystemPrompt = systemPrompt;
        _dynamicSystemPrompt = string.Empty;
        _messages = new List<Message>();
        _lastResponse = null;
        _slidingCacheBoundary = null;

        foreach (var entry in history)
        {
            var role = entry.Role == "assistant" ? RoleType.Assistant : RoleType.User;
            var content = new List<ContentBase>();

            foreach (var part in entry.Parts)
            {
                switch (part)
                {
                    case TextHistoryPart tp:
                        content.Add(new Anthropic.SDK.Messaging.TextContent { Text = tp.Text });
                        break;
                    case ToolCallHistoryPart tc:
                        content.Add(new ToolUseContent
                        {
                            Id = tc.Id,
                            Name = tc.Name,
                            Input = JsonNode.Parse(tc.InputJson)
                        });
                        break;
                    case ToolResultHistoryPart tr:
                        content.Add(new Anthropic.SDK.Messaging.ToolResultContent
                        {
                            ToolUseId = tr.ToolCallId,
                            Content = [new Anthropic.SDK.Messaging.TextContent { Text = tr.Output }],
                            IsError = tr.IsError
                        });
                        break;
                }
            }

            if (content.Count > 0)
                _messages.Add(new Message { Role = role, Content = content });
        }

        SetTools(tools);
        // Note: Tool has no CacheControl property in Anthropic.SDK 5.10.0; tool caching is
        // handled via PromptCacheType.FineGrained on the system block (BP1).
    }

    // ── Anthropic SDK tool conversion ─────────────────────────────────────────

    internal static Anthropic.SDK.Common.Tool ToAnthropicTool(AIFunction tool)
    {
        var schemaNode = JsonNode.Parse(tool.JsonSchema.GetRawText());
        var func = new Anthropic.SDK.Common.Function(
            tool.Name,
            tool.Description ?? string.Empty,
            schemaNode!);
        return new Anthropic.SDK.Common.Tool(func);
    }

    // ── Internal test accessors ───────────────────────────────────────────────

    /// <summary>For tests only. Returns the CacheControl on the last history message's last block (BP3).</summary>
    internal CacheControl? GetHistoryBoundaryCacheControl()
    {
        // History boundary is on the message immediately before the user query.
        // _messages[^1] = user query; _messages[^2] = last history turn (if it exists).
        if (_messages.Count < 2) return null;
        var hist = _messages[^2];
        return hist.Content is { Count: > 0 } ? hist.Content[^1].CacheControl : null;
    }

    /// <summary>For tests only. Returns the content block that currently holds the sliding BP4 cache marker.</summary>
    internal ContentBase? GetSlidingBoundaryContent()
        => _slidingCacheBoundary?.Content is { Count: > 0 }
            ? _slidingCacheBoundary.Content[^1]
            : null;

    /// <summary>For tests only. Counts all content blocks with CacheControl set across all messages.</summary>
    internal int CountCacheControlMarkers()
    {
        int count = 0;
        foreach (var msg in _messages)
        {
            if (msg.Content is null) continue;
            foreach (var block in msg.Content)
                if (block.CacheControl is not null) count++;
        }
        return count;
    }
}
