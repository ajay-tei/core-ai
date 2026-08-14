namespace Diva.LoadTest;

/// <summary>Outcome of one simulated user request (either a buffered /invoke call or one full SSE run).</summary>
public sealed class RequestResult
{
    public required DateTime StartedAtUtc { get; init; }
    public required double TotalMs { get; init; }
    public required bool Success { get; init; }
    public int? HttpStatus { get; init; }
    public string? Error { get; init; }

    // Populated only for streaming (/invoke/stream) requests — null for buffered /invoke calls.
    public double? TimeToFirstByteMs { get; init; }
    public double? TimeToFirstTokenMs { get; init; }
    public double? TimeToFirstToolCallMs { get; init; }
    public double? TimeToDoneMs { get; init; }
    public int ToolCallCount { get; init; }
    public int IterationCount { get; init; }
    public bool VerificationBlocked { get; init; }

    /// <summary>Which ramp/soak "bucket" (elapsed seconds since test start, rounded down) this belongs to — used for time-series reporting.</summary>
    public int BucketSecond { get; init; }
    public int ActiveUsersAtStart { get; init; }
}

/// <summary>Minimal mirror of Diva.Core.Models.AgentStreamChunk — only the fields the load tool needs to read.</summary>
public sealed class AgentStreamChunkDto
{
    public string Type { get; set; } = "";
    public int? Iteration { get; set; }
    public string? ToolName { get; set; }
    public string? SessionId { get; set; }
    public string? ErrorMessage { get; set; }
    public VerificationDto? Verification { get; set; }
}

public sealed class VerificationDto
{
    public bool WasBlocked { get; set; }
}

/// <summary>Minimal mirror of AgentSummaryDto (GET /api/agents) — only fields needed for auto-discovery.</summary>
public sealed class AgentSummaryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string AgentType { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsEnabled { get; set; }
}
