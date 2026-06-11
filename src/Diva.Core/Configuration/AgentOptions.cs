namespace Diva.Core.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public int MaxIterations { get; set; } = 10;

    /// <summary>
    /// Maximum number of additional iteration windows started after <see cref="MaxIterations"/> is
    /// exhausted without natural completion. 0 = no continuations (hard stop at MaxIterations).
    /// Default 2 = up to 3 total windows (1 initial + 2 continuations).
    /// </summary>
    public int MaxContinuations { get; set; } = 2;

    public double DefaultTemperature { get; set; } = 0.7;
    public int MaxToolResultChars { get; set; } = 8000;
    /// <summary>Max tokens the LLM may produce per call. 4096 is often too tight when the model
    /// needs to compile large tool results into a tool call argument (e.g. email body).</summary>
    public int MaxOutputTokens { get; set; } = 8192;
    public bool InjectToolStrategy { get; set; } = true;

    /// <summary>
    /// Enable token-level response streaming via <c>StreamLlmAsync</c>.
    /// When true, text tokens arrive as <c>text_delta</c> SSE events during LLM generation
    /// instead of waiting for the full response. The <c>thinking</c> event still fires after
    /// streaming with the complete aggregated text. Default true.
    /// Disable if your LLM endpoint does not support streaming, or set per-agent via
    /// Variables["__disable_streaming"] = "true".
    /// </summary>
    public bool EnableResponseStreaming { get; set; } = true;

    /// <summary>
    /// When true (default), splits the Anthropic system prompt into a stable static block
    /// (base prompt + group/tenant overrides + group rules) and a volatile dynamic block
    /// (session rules, caller instructions, history summary), then adds explicit
    /// cache_control breakpoints on the static block (BP1), tool definitions (BP2),
    /// the last history message (BP3), and the most-recent tool-result exchange (BP4).
    /// Only applies to AnthropicProviderStrategy; no-op for OpenAI-compatible providers.
    /// </summary>
    public bool EnableHistoryCaching { get; set; } = true;

    /// <summary>Max tokens for the Agent Setup Assistant LLM suggestion calls. Default 4096.</summary>
    public int MaxSuggestionTokens { get; set; } = 4096;
    /// <summary>
    /// Max tokens for rule pack suggestion calls. Rule packs produce larger JSON arrays
    /// (multiple packs × multiple rules each), so they need a higher limit than simple
    /// prompt suggestions. Default 4096. Increase if you have many existing rule packs.
    /// </summary>
    public int MaxRulePackSuggestionTokens { get; set; } = 4096;
    /// <summary>
    /// Maximum time in seconds that a single MCP tool call may run before it is cancelled.
    /// Read from <c>Agent:ToolTimeoutSeconds</c> in appsettings. Default 30 s.
    /// </summary>
    public int ToolTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum time in seconds for a buffered (non-streaming) LLM call before cancellation.
    /// Applied per-call inside <c>CallLlmForIterationAsync</c>. This is tighter than the
    /// outer <c>LlmOptions.HttpTimeoutSeconds</c> (HttpClient-level, default 600 s).
    /// Default 120 s. Set to 0 to rely solely on the HttpClient timeout.
    /// </summary>
    public int LlmTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum idle time in seconds between streamed chunks before the streaming LLM call
    /// is cancelled. Resets on every received chunk, so long responses that stream actively
    /// are never killed. Must be generous enough for the model to "think" after processing
    /// large tool results (20 K+ chars) before emitting the first text token.
    /// Default 120 s. Set to 0 to disable idle timeout on streaming.
    /// </summary>
    public int LlmStreamIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum time in seconds that a single sub-agent call may run before cancellation.
    /// Used by both <c>DispatchStage</c> (supervisor pipeline) and <c>AgentToolExecutor</c>
    /// (agents-as-tools delegation). Sub-agents run a full ReAct loop (multiple LLM iterations
    /// + tool calls), so this must be significantly larger than <c>ToolTimeoutSeconds</c>.
    /// Default 300 s (5 minutes). Set to 0 to disable the per-sub-agent timeout.
    /// </summary>
    public int SubAgentTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of MCP tool calls executed concurrently via <c>Task.WhenAll</c>
    /// in a single iteration. Prevents overwhelming MCP servers when the LLM emits
    /// many parallel tool calls (e.g. sending emails to 20 locations).
    /// Default 10. Set to 0 for unlimited.
    /// </summary>
    public int MaxParallelToolCalls { get; set; } = 10;

    /// <summary>
    /// Tool name prefixes that should never be deduplicated within an intra-batch
    /// parallel execution. Tools whose name starts with any of these prefixes will
    /// always execute individually even when two calls share identical input JSON.
    /// Prevents silent single-execution of action tools like <c>send_email</c>.
    /// Default: <c>send_</c>, <c>create_</c>, <c>post_</c>, <c>delete_</c>, <c>update_</c>.
    /// </summary>
    public List<string> NeverDeduplicateToolPrefixes { get; set; } =
        ["send_", "create_", "post_", "delete_", "update_"];

    /// <summary>
    /// Minimum tool count required to trigger semantic pre-filtering via LlmToolSelector.
    /// When tool count is at or below this threshold the filter is skipped (no LLM call).
    /// Set to 0 to disable semantic tool filtering entirely. Default 8.
    /// </summary>
    public int SemanticToolFilterThreshold { get; set; } = 8;

    /// <summary>
    /// Maximum number of tools to keep after semantic filtering. Default 6.
    /// Has no effect when SemanticToolFilterThreshold is 0 or tool count is at/below threshold.
    /// </summary>
    public int SemanticToolFilterMaxTools { get; set; } = 6;

    public RuleLearningOptions RuleLearning { get; set; } = new();
    public LlmRetryOptions Retry { get; set; } = new();
    public ContextWindowOptions ContextWindow { get; set; } = new();
    public OptimizationOptions Optimization { get; set; } = new();
}

public sealed class LlmRetryOptions
{
    public int MaxRetries { get; set; } = 3;
    public int BaseDelayMs { get; set; } = 1000;  // doubles each attempt: 2s, 4s, 8s
}

public sealed class ContextWindowOptions
{
    /// <summary>
    /// Soft token budget per LLM call (input side).
    /// Default 120 000 = 60 % of Claude Sonnet 4's 200 K window.
    /// </summary>
    public int BudgetTokens { get; set; } = 120_000;

    /// <summary>Compact in-run messages when estimated tokens exceed this fraction of BudgetTokens.</summary>
    public double CompactionThreshold { get; set; } = 0.65;

    /// <summary>Number of most-recent messages always kept verbatim during in-run compaction.</summary>
    public int KeepLastRawMessages { get; set; } = 6;

    /// <summary>
    /// Maximum cross-run history turns loaded verbatim from the database.
    /// Older turns are summarised and injected into the system prompt instead.
    /// </summary>
    public int MaxHistoryTurns { get; set; } = 20;

    /// <summary>
    /// Model for LLM-based summarisation of offloaded cross-run context.
    /// null = fall back to the session's own model, then to rule-based summarisation.
    /// </summary>
    public string? SummarizerModel { get; set; }

    /// <summary>Max output tokens for the cross-run (Point B) LLM summariser call. Default 512.</summary>
    public int SummarizerMaxTokens { get; set; } = 512;
}

/// <summary>
/// Per-agent overrides stored as JSON in AgentDefinitionEntity.ContextWindowJson.
/// Null fields fall through to the global ContextWindowOptions defaults.
/// </summary>
public sealed class ContextWindowOverrideOptions
{
    public int? BudgetTokens { get; set; }
    public double? CompactionThreshold { get; set; }
    public int? KeepLastRawMessages { get; set; }
    public int? MaxHistoryTurns { get; set; }
    public string? SummarizerModel { get; set; }
}

public sealed class OptimizationOptions
{
    public float ConfidenceThreshold { get; set; } = 0.6f;
    public int MaxSuggestionsPerRun { get; set; } = 5;
    public int SampleTurnsForLlm { get; set; } = 5;
    public int MaxTranscriptChars { get; set; } = 8000;
    /// <summary>Max output tokens for the analysis LLM call that produces suggestions. Default 2048.</summary>
    public int AnalyzerMaxTokens { get; set; } = 2048;
    /// <summary>Max output tokens for the prompt merge LLM call. Needs to be large enough to
    /// reproduce the full merged system prompt. Default 8192. Can be overridden per-agent via
    /// AgentDefinition.OptimizationOverrideJson → MergeMaxTokens.</summary>
    public int MergeMaxTokens { get; set; } = 8192;
    public int ScorerMaxTokens { get; set; } = 256;
    public bool EnablePerTurnScoring { get; set; } = true;
    public int SchedulerPollIntervalSeconds { get; set; } = 300;
    public int MaxFewShotExamplesPerAgent { get; set; } = 5;
}

/// <summary>
/// Per-agent optimization overrides stored as JSON in AgentDefinitionEntity.OptimizationOverrideJson.
/// Null fields fall through to the global OptimizationOptions defaults.
/// </summary>
public sealed class OptimizationOverrideOptions
{
    /// <summary>Override max output tokens for the merge LLM call. Useful for agents with very
    /// long system prompts that exceed the global MergeMaxTokens default.</summary>
    public int? MergeMaxTokens { get; set; }
    /// <summary>Override max output tokens for the analysis LLM call.</summary>
    public int? AnalyzerMaxTokens { get; set; }
}

public sealed class RuleLearningOptions
{
    /// <summary>AutoApprove | RequireAdmin | SessionOnly</summary>
    public string ApprovalMode { get; set; } = "RequireAdmin";
    public double ConfidenceThreshold { get; set; } = 0.8;
    /// <summary>Max output tokens for the rule-extraction LLM call.</summary>
    public int ExtractorMaxTokens { get; set; } = 2048;
}
