using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Sessions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Abstracts LLM provider differences (Anthropic SDK vs OpenAI-compatible IChatClient)
/// so the ReAct loop can be implemented once regardless of provider.
/// Strategies are created per-execution — lightweight, holding only the message list and provider refs.
/// </summary>
internal interface ILlmProviderStrategy
{
    /// <summary>Build initial message history from conversation turns + user query, and set tool definitions.</summary>
    void Initialize(
        string systemPrompt,
        List<ConversationTurn> history,
        string userQuery,
        List<McpClientTool> mcpTools,
        IReadOnlyList<ContentPart>? attachments = null);

    /// <summary>Add extra tools (e.g. agent delegation tools) to the existing tool set after initialization.</summary>
    void AddExtraTools(IReadOnlyList<AIFunction> tools);

    /// <summary>Call the LLM and return a unified response. Stores the raw response internally for <see cref="CommitAssistantResponse"/>.</summary>
    Task<UnifiedLlmResponse> CallLlmAsync(CancellationToken ct);

    /// <summary>Token usage from the most recent LLM call. All fields zero if not yet called or unavailable.</summary>
    TokenUsage LastTokenUsage { get; }

    /// <summary>
    /// Stream the LLM response token by token. Yields <see cref="UnifiedStreamDelta"/> per token,
    /// followed by a final item with <see cref="UnifiedStreamDelta.IsDone"/> = true.
    /// Falls back to a single buffered call if the provider does not support streaming.
    /// </summary>
    IAsyncEnumerable<UnifiedStreamDelta> StreamLlmAsync(CancellationToken ct);

    /// <summary>No-tools LLM call for adaptive re-planning (reduced MaxTokens). Returns revised text or null.</summary>
    Task<string?> CallReplanAsync(CancellationToken ct);

    /// <summary>Add the last raw LLM response to the internal message history as an assistant message.</summary>
    void CommitAssistantResponse();

    /// <summary>Update the current system prompt so later LLM calls use the latest hook-mutated prompt.</summary>
    void UpdateSystemPrompt(string systemPrompt);

    /// <summary>Add tool results to the internal message history (respects OpenAI ordering rules).</summary>
    void AddToolResults(IReadOnlyList<UnifiedToolResult> results);

    /// <summary>
    /// True for local LLM endpoints (LM Studio, Ollama) that cannot process images in
    /// follow-up messages after tool calls. When true, the runner calls SummarizeImageAsync
    /// to get a text description before injecting tool results.
    /// </summary>
    bool UseVisionSummarization => false;

    /// <summary>
    /// Makes a clean single-turn vision call and returns a text description of the image.
    /// Only called when UseVisionSummarization is true.
    /// </summary>
    Task<string> SummarizeImageAsync(ImageContentPart img, CancellationToken ct)
        => Task.FromResult(string.Empty);

    /// <summary>Append a user message to the internal message history.</summary>
    void AddUserMessage(string text);

    /// <summary>Append an assistant message then a user message (used for error retry + verification correction).</summary>
    void AddAssistantThenUser(string assistantText, string userText);

    /// <summary>
    /// Disables extended thinking for the remainder of this run (and strips thinking blocks from
    /// history so requests stay valid with thinking off). Used to force a plain-text final answer
    /// when a reasoning model keeps ending its turn with thinking but no visible text — once off it
    /// must not re-enable, otherwise a tool call would let the next turn go thinking-only again.
    /// No-op for providers/agents without extended thinking.
    /// </summary>
    void SuppressThinkingForRun() { }

    /// <summary>
    /// The extended-thinking (reasoning) text accumulated on the most recent LLM call, or null when
    /// the turn produced no thinking. Used as a last-resort answer when a turn ends with reasoning
    /// only and no visible text. Null for providers without extended thinking.
    /// </summary>
    string? LastThinkingText => null;

    /// <summary>Point A: in-run context compaction. Modifies internal message list in place.</summary>
    void CompactHistory(string systemPrompt, ContextWindowOverrideOptions? agentOverride);

    /// <summary>Prepare for a new continuation window: compact + inject continuation context message.</summary>
    void PrepareNewWindow(string continuationContext, string systemPrompt, ContextWindowOverrideOptions? agentOverride);

    /// <summary>
    /// Switch the active model (and optionally max_tokens, API key, endpoint) for the NEXT
    /// <see cref="CallLlmAsync"/> call. In-flight message history is preserved.
    /// Use this for same-provider model switches and same-provider cross-endpoint switches.
    /// For cross-provider switches (Anthropic ↔ OpenAI), use
    /// <see cref="ExportHistory"/> + new strategy + <see cref="ImportHistory"/> instead.
    /// </summary>
    void SetModel(string model, int? maxTokens = null, string? apiKeyOverride = null, string? endpointOverride = null);

    /// <summary>
    /// Export the current in-flight message history to a provider-agnostic format.
    /// Called before discarding the strategy on a cross-provider switch so that history
    /// can be imported into the new strategy via <see cref="ImportHistory"/>.
    /// </summary>
    List<UnifiedHistoryEntry> ExportHistory();

    /// <summary>
    /// Import message history from a provider-agnostic format.
    /// Replaces what <see cref="Initialize"/> would have built from ConversationTurns.
    /// Used after creating a new strategy when switching providers mid-execution.
    /// <paramref name="tools"/> re-registers MCP tool definitions on the new strategy.
    /// </summary>
    void ImportHistory(List<UnifiedHistoryEntry> history, string systemPrompt, List<ModelContextProtocol.Client.McpClientTool> tools);
}

/// <summary>Unified LLM response — provider-agnostic representation.</summary>
internal sealed record UnifiedLlmResponse(
    string Text,
    IReadOnlyList<UnifiedToolCall> ToolCalls,
    bool HasToolCalls,
    string? StopReason = null);

/// <summary>A single tool call extracted from the LLM response.</summary>
internal sealed record UnifiedToolCall(string Id, string Name, string InputJson);

/// <summary>A tool execution result to be fed back to the LLM.</summary>
internal sealed record UnifiedToolResult(
    string ToolCallId,
    string ToolName,
    string Output,
    bool IsError,
    IReadOnlyList<ContentPart>? ContentParts = null);

/// <summary>
/// Token counts from the most recent LLM call. Fields default to 0 when unavailable.
/// <para>For Anthropic: <see cref="CacheRead"/> and <see cref="CacheCreation"/> are populated when
/// <c>PromptCaching = AutomaticToolsAndSystem</c> is set. <see cref="Input"/> reflects only the
/// non-cached portion; <see cref="TotalEffectiveInput"/> gives the full context size.</para>
/// <para>For OpenAI-compatible providers: <see cref="CacheRead"/> and <see cref="CacheCreation"/>
/// are always 0. Streaming mode also returns 0 for <see cref="Input"/> and <see cref="Output"/>
/// as ME.AI 10.4.1 does not expose usage in streaming updates.</para>
/// </summary>
internal readonly record struct TokenUsage(
    int Input, int Output, int CacheRead = 0, int CacheCreation = 0)
{
    /// <summary>Total context size processed: non-cached input + cache-read tokens.</summary>
    public int TotalEffectiveInput => Input + CacheRead + CacheCreation;
}
