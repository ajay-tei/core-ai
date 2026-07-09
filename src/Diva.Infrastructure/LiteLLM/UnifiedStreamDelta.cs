namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// A single streaming delta yielded by <see cref="ILlmProviderStrategy.StreamLlmAsync"/>.
/// Text deltas arrive with <see cref="TextDelta"/> set; extended-thinking (reasoning) deltas arrive
/// with <see cref="ThinkingDelta"/> set; the final item has <see cref="IsDone"/> = true
/// and <see cref="Final"/> populated with the complete accumulated response.
/// </summary>
internal sealed record UnifiedStreamDelta(
    string? TextDelta,               // non-null on text token events; null on Done
    bool IsDone,
    UnifiedLlmResponse? Final,       // set only when IsDone = true
    string? ThinkingDelta = null);   // non-null on extended-thinking token events (Anthropic only)
