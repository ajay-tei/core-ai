namespace Diva.Core.Models;

/// <summary>
/// A single event emitted during streaming agent execution.
/// Sent as SSE (text/event-stream) from POST /api/agents/{id}/invoke/stream.
///
/// Type values (in order of emission):
///   tools_available       — emitted once before the first iteration; ToolCount = 0 means no MCP tools connected
///   plan                  — initial step-by-step execution plan, detected from iteration 1 thinking text
///   plan_revised          — revised plan emitted after 2 consecutive step failures
///   iteration_start       — new ReAct iteration starting
///   text_delta            — incremental token during LLM generation (Content = token text); fired before thinking
///   thinking              — full LLM reasoning text for the iteration (follows text_delta events when streaming)
///   tool_call             — MCP tool about to be invoked
///   a2a_delegation_start  — A2A delegation call starting (A2ATaskId, DelegatedAgentId, DelegatedAgentName, ToolName)
///   tool_result           — MCP tool result received
///   model_switch          — active model changed mid-execution (FromModel, ToModel, FromProvider, ToProvider, Reason)
///   continuation_start    — emitted at the start of each continuation window (window ≥ 2)
///   token_usage           — token metrics per iteration (all providers; Anthropic includes cache fields)
///                           (IterationInputTokens, IterationOutputTokens, TotalInputTokens, TotalOutputTokens,
///                            IterationCacheRead, IterationCacheCreation, TotalCacheRead, TotalCacheCreation)
///   final_response        — complete agent answer
///   verification          — verification badge data
///   session_save_error    — response delivered but turn could not be persisted (ErrorMessage contains user-facing warning)
///   rule_suggestion       — rule learning follow-up question
///   correction            — inline verification triggered re-iteration
///   hook_executed         — lifecycle hook completed (HookName, HookPoint, HookDurationMs)
///   error                 — execution failure
///   done                  — stream complete (last event always)
/// </summary>
public sealed class AgentStreamChunk
{
    public string Type { get; init; } = string.Empty;

    /// <summary>ReAct loop iteration number (1-based).</summary>
    public int? Iteration { get; init; }

    /// <summary>Text content — thinking text or final response.</summary>
    public string? Content { get; init; }

    /// <summary>MCP tool name (tool_call / tool_result events).</summary>
    public string? ToolName { get; init; }

    /// <summary>Provider tool-call id (e.g. Anthropic tool_use id) — pairs a tool_result with its tool_call unambiguously when the same tool name is invoked more than once in an iteration.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>JSON-serialised tool input parameters (tool_call event).</summary>
    public string? ToolInput { get; init; }

    /// <summary>Raw tool output text (tool_result event).</summary>
    public string? ToolOutput { get; init; }

    /// <summary>Authoritative wall-clock duration of the tool execution itself (tool_result event) — null for agent-delegation calls, which run a nested ReAct loop rather than a single timed MCP call.</summary>
    public long? ToolDurationMs { get; init; }

    /// <summary>Attached to the verification event.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>Rule suggestions (rule_suggestion event).</summary>
    public FollowUpQuestion[]? FollowUpQuestions { get; init; }

    /// <summary>Session ID — set on the final_response event.</summary>
    public string? SessionId { get; init; }

    /// <summary>Human-readable elapsed time — set on the done event.</summary>
    public string? ExecutionTime { get; init; }

    /// <summary>Error message (error event).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Number of MCP tools connected (tools_available event). 0 = no tools; model will not call any tools.</summary>
    public int? ToolCount { get; init; }

    /// <summary>Names of all connected MCP tools (tools_available event).</summary>
    public string[]? ToolNames { get; init; }

    /// <summary>Numbered plan steps extracted from the LLM's planning text (plan / plan_revised event).</summary>
    public string[]? PlanSteps { get; init; }

    /// <summary>Raw plan text as produced by the LLM (plan / plan_revised event).</summary>
    public string? PlanText { get; init; }

    /// <summary>
    /// Current continuation window number (1-based). 1 = initial window, 2 = first continuation, etc.
    /// Only present on continuation_start events.
    /// </summary>
    public int? ContinuationWindow { get; init; }

    /// <summary>Lifecycle hook name (hook_executed event), e.g. "OnBeforeIteration".</summary>
    public string? HookName { get; init; }

    /// <summary>Lifecycle hook point (hook_executed event), e.g. "before_iteration".</summary>
    public string? HookPoint { get; init; }

    /// <summary>Hook execution time in milliseconds (hook_executed event).</summary>
    public double? HookDurationMs { get; init; }

    /// <summary>Number of Rule Pack rules that triggered during the hook (hook_executed event).</summary>
    public int? RulePackTriggeredCount { get; init; }

    /// <summary>Rule Pack trigger labels like "regex_redact:modified" (hook_executed event).</summary>
    public string[]? RulePackTriggeredRules { get; init; }

    /// <summary>Number of tool calls filtered by Rule Packs (hook_executed event for OnToolFilter).</summary>
    public int? RulePackFilteredCount { get; init; }

    /// <summary>Error action selected by Rule Packs (hook_executed event for OnError).</summary>
    public string? RulePackErrorAction { get; init; }

    /// <summary>Whether Rule Pack evaluation marked the stage as blocked.</summary>
    public bool? RulePackBlocked { get; init; }

    // ── model_switch event fields ─────────────────────────────────────────────

    /// <summary>Model that was active before the switch (model_switch event).</summary>
    public string? FromModel { get; init; }

    /// <summary>Model switched to (model_switch event).</summary>
    public string? ToModel { get; init; }

    /// <summary>Provider that was active before the switch (model_switch event).</summary>
    public string? FromProvider { get; init; }

    /// <summary>Provider switched to (model_switch event).</summary>
    public string? ToProvider { get; init; }

    /// <summary>
    /// Why the model was switched (model_switch event).
    /// Values: "static_config" | "rule_pack" | "smart_router" | "failure_upgrade"
    /// </summary>
    public string? Reason { get; init; }

    // ── a2a_delegation_start event fields ─────────────────────────────────────

    /// <summary>A2A task ID created for this delegation (a2a_delegation_start event).</summary>
    public string? A2ATaskId { get; init; }

    /// <summary>ID of the agent being delegated to (a2a_delegation_start event).</summary>
    public string? DelegatedAgentId { get; init; }

    /// <summary>Display name of the agent being delegated to (a2a_delegation_start event).</summary>
    public string? DelegatedAgentName { get; init; }

    // ── token_usage event fields (all providers; cache fields Anthropic only) ──

    /// <summary>Input tokens consumed by this iteration's LLM call (token_usage event).
    /// 0 for OpenAI streaming due to ME.AI SDK limitation.</summary>
    public int? IterationInputTokens { get; init; }

    /// <summary>Output tokens generated by this iteration's LLM call (token_usage event).
    /// 0 for OpenAI streaming due to ME.AI SDK limitation.</summary>
    public int? IterationOutputTokens { get; init; }

    /// <summary>Cumulative input tokens across all iterations so far (token_usage event).</summary>
    public int? TotalInputTokens { get; init; }

    /// <summary>Cumulative output tokens across all iterations so far (token_usage event).</summary>
    public int? TotalOutputTokens { get; init; }

    /// <summary>Cache read tokens for this iteration (token_usage event, Anthropic only).
    /// Tokens served from Anthropic's prompt cache — not charged as regular input tokens.</summary>
    public int? IterationCacheRead { get; init; }

    /// <summary>Cache creation tokens for this iteration (token_usage event, Anthropic only).
    /// Tokens written into Anthropic's prompt cache — charged at 1.25× the normal rate.</summary>
    public int? IterationCacheCreation { get; init; }

    /// <summary>Cumulative cache read tokens across all iterations so far (token_usage event, Anthropic only).</summary>
    public int? TotalCacheRead { get; init; }

    /// <summary>Cumulative cache creation tokens across all iterations so far (token_usage event, Anthropic only).</summary>
    public int? TotalCacheCreation { get; init; }
}
