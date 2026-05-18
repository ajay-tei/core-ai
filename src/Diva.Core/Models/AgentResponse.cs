namespace Diva.Core.Models;

public sealed class AgentResponse
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public string? AgentName { get; init; }
    public string? SessionId { get; init; }
    public List<string> ToolsUsed { get; init; } = [];
    public TimeSpan ExecutionTime { get; init; }
    public List<FollowUpQuestion> FollowUpQuestions { get; init; } = [];
    public Dictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>Verification result. Null when Mode=Off or when the response failed.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>Concatenated raw text of all MCP tool results used during this response (evidence trail).</summary>
    public string ToolEvidence { get; init; } = string.Empty;

    /// <summary>Total LLM input tokens consumed across all ReAct iterations.</summary>
    public int InputTokens { get; init; }

    /// <summary>Total LLM output tokens produced across all ReAct iterations.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Number of ReAct iterations executed (continuation windows included).</summary>
    public int IterationCount { get; init; }
}

public sealed class FollowUpQuestion
{
    public string Type { get; init; } = string.Empty;       // "rule_confirmation" | "clarification"
    public string Text { get; init; } = string.Empty;
    public string[] Options { get; init; } = [];
    public object? Metadata { get; init; }
}
