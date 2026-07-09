namespace Diva.Core.Models.Session;

// ── List / summary ─────────────────────────────────────────────────────────

public sealed class SessionSummary
{
    public string SessionId { get; init; } = "";
    public string? ParentSessionId { get; init; }
    public int TenantId { get; init; }
    public string? UserId { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string? Title { get; init; }
    public bool IsSupervisor { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public int TotalTurns { get; init; }
    public int TotalIterations { get; init; }
    public int TotalToolCalls { get; init; }
    public int TotalDelegations { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
}

public sealed class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

// ── Detail ─────────────────────────────────────────────────────────────────

public sealed class SessionDetail
{
    public string SessionId { get; init; } = "";
    public string? ParentSessionId { get; init; }
    public int TenantId { get; init; }
    public string? UserId { get; init; }
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public string? Title { get; init; }
    public bool IsSupervisor { get; init; }
    public string Status { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public int TotalTurns { get; init; }
    public int TotalIterations { get; init; }
    public int TotalToolCalls { get; init; }
    public int TotalDelegations { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public List<TurnSummary> Turns { get; init; } = [];
}

public sealed class TurnSummary
{
    public int TurnNumber { get; init; }
    public string UserMessagePreview { get; init; } = "";
    public string AssistantMessagePreview { get; init; } = "";
    /// <summary>Full user message — included in session detail endpoint, null in list endpoints.</summary>
    public string? UserMessage { get; init; }
    /// <summary>Full assistant message — included in session detail endpoint, null in list endpoints.</summary>
    public string? AssistantMessage { get; init; }
    public int TotalIterations { get; init; }
    public int TotalToolCalls { get; init; }
    public int ContinuationWindows { get; init; }
    public string? VerificationMode { get; init; }
    public bool? VerificationPassed { get; init; }
    public long ExecutionTimeMs { get; init; }
    public string? ModelId { get; init; }
    public string? Provider { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public DateTime CreatedAt { get; init; }
}

// ── Iteration detail ────────────────────────────────────────────────────────

public sealed class IterationDetail
{
    public int IterationNumber { get; init; }
    public int ContinuationWindow { get; init; }
    public bool IsCorrection { get; init; }
    public string? ThinkingText { get; init; }
    public string? PlanText { get; init; }
    public string? ModelId { get; init; }
    public string? Provider { get; init; }
    public bool HadModelSwitch { get; init; }
    public string? FromModel { get; init; }
    public string? ToModel { get; init; }
    public string? ModelSwitchReason { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CacheReadTokens { get; init; }
    public int CacheCreationTokens { get; init; }
    public List<ToolCallDetail> ToolCalls { get; init; } = [];
}

public sealed class ToolCallDetail
{
    public int Sequence { get; init; }
    public string ToolName { get; init; } = "";
    public string? ToolInput { get; init; }
    public string? ToolOutput { get; init; }
    public bool IsAgentDelegation { get; init; }
    public string? DelegatedAgentId { get; init; }
    public string? DelegatedAgentName { get; init; }
    public string? LinkedA2ATaskId { get; init; }
    /// <summary>Resolved from TraceDelegationChain by LinkedA2ATaskId — the child session created for this delegation.</summary>
    public string? ChildSessionId { get; init; }
}

// ── Session tree ────────────────────────────────────────────────────────────

public sealed class SessionTreeNode
{
    public string SessionId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public bool IsSupervisor { get; init; }
    public string Status { get; init; } = "";
    public int TotalTurns { get; init; }
    public int TotalIterations { get; init; }
    public int TotalToolCalls { get; init; }
    /// <summary>True when this node represents the session currently being viewed.</summary>
    public bool IsCurrentSession { get; init; }
    public List<SessionTreeNode> Children { get; init; } = [];
}

// ── Continue / resume ──────────────────────────────────────────────────────

/// <summary>
/// Result of reactivating a stored session for continuation in Agent Test.
/// The frontend uses <see cref="AgentId"/> + <see cref="SessionId"/> to open the
/// chat route and append new turns to the same session.
/// </summary>
public sealed class ContinueSessionResult
{
    public string SessionId { get; init; } = "";
    public string AgentId { get; init; } = "";
    public string AgentName { get; init; } = "";
    public int TurnCount { get; init; }
    /// <summary>
    /// True when the underlying conversation session was found and reactivated,
    /// so prior context will be replayed. False means only trace metadata exists
    /// (e.g. conversation memory expired/purged) and a fresh session will start.
    /// </summary>
    public bool Reactivated { get; init; }
}
