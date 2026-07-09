namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Anchor table — one row per agent session in the trace database.
/// SessionId matches AgentSessions.Id in the main diva.db.
/// </summary>
public class TraceSessionEntity
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Session ID of the parent supervisor session, when this is a worker session.</summary>
    public string? ParentSessionId { get; set; }

    public int TenantId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable session title used to identify the session in history lists.
    /// Auto-derived from the first user question on the first turn. null until the first turn is flushed.
    /// </summary>
    public string? Title { get; set; }

    public bool IsSupervisor { get; set; }
    public string Status { get; set; } = "active";    // "active" | "completed" | "failed" | "deleted"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public int TotalTurns { get; set; }
    public int TotalIterations { get; set; }
    public int TotalToolCalls { get; set; }
    public int TotalDelegations { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }

    public List<TraceSessionTurnEntity> Turns { get; set; } = [];
}

/// <summary>One row per conversation turn (user question + agent response pair).</summary>
public class TraceSessionTurnEntity
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public int TurnNumber { get; set; }
    public string UserMessage { get; set; } = string.Empty;
    public string AssistantMessage { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int TotalIterations { get; set; }
    public int TotalToolCalls { get; set; }
    public int ContinuationWindows { get; set; }
    public string? VerificationMode { get; set; }
    public bool? VerificationPassed { get; set; }
    public long ExecutionTimeMs { get; set; }
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // LLM-as-Judge dimension scores (null until scored)
    public float? FaithfulnessScore { get; set; }
    public float? CompletenessScore { get; set; }
    public float? ToolEfficiencyScore { get; set; }
    public float? CoherenceScore { get; set; }
    public bool ScoresAvailable { get; set; } = false;

    public TraceSessionEntity Session { get; set; } = null!;
    public List<TraceIterationEntity> Iterations { get; set; } = [];
}

/// <summary>One row per ReAct inner-loop iteration.</summary>
public class TraceIterationEntity
{
    public int Id { get; set; }
    /// <summary>Denormalized — enables direct WHERE SessionId = ? queries without joins.</summary>
    public string SessionId { get; set; } = string.Empty;
    public int TurnId { get; set; }
    public int TurnNumber { get; set; }
    public int IterationNumber { get; set; }
    public int ContinuationWindow { get; set; }
    public string? ThinkingText { get; set; }
    public string? PlanText { get; set; }
    public string? ModelId { get; set; }
    public string? Provider { get; set; }
    public bool HadToolCalls { get; set; }
    public bool HadModelSwitch { get; set; }
    public string? FromModel { get; set; }
    public string? ToModel { get; set; }
    public string? ModelSwitchReason { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public bool IsCorrection { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TraceSessionTurnEntity Turn { get; set; } = null!;
    public List<TraceToolCallEntity> ToolCalls { get; set; } = [];
}

/// <summary>One row per tool_call / tool_result pair within an iteration.</summary>
public class TraceToolCallEntity
{
    public int Id { get; set; }
    /// <summary>Denormalized — enables direct WHERE SessionId = ? queries without joins.</summary>
    public string SessionId { get; set; } = string.Empty;
    public int IterationId { get; set; }
    public int TurnId { get; set; }
    public int TurnNumber { get; set; }
    public int IterationNumber { get; set; }
    public int Sequence { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string ToolInput { get; set; } = string.Empty;
    public string ToolOutput { get; set; } = string.Empty;
    public bool IsAgentDelegation { get; set; }
    public string? DelegatedAgentId { get; set; }
    public string? DelegatedAgentName { get; set; }
    public string? LinkedA2ATaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TraceIterationEntity Iteration { get; set; } = null!;
}

/// <summary>
/// Explicit parent → child A2A delegation graph.
/// Enables full delegation chain queries without traversing all tool calls.
/// </summary>
public class TraceDelegationChainEntity
{
    public int Id { get; set; }
    /// <summary>Session that originated the delegation call.</summary>
    public string CallerSessionId { get; set; } = string.Empty;
    /// <summary>Which iteration inside the caller session made the delegation call.</summary>
    public int? CallerIterationId { get; set; }
    public string ChildA2ATaskId { get; set; } = string.Empty;
    public string ChildAgentId { get; set; } = string.Empty;
    public string ChildAgentName { get; set; } = string.Empty;
    /// <summary>Trace session created for the child agent's execution (set when child starts).</summary>
    public string? ChildSessionId { get; set; }
    /// <summary>Delegation depth: 1 = direct call from a top-level session.</summary>
    public int Depth { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Status { get; set; } = "working";   // mirrors AgentTask.Status
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
