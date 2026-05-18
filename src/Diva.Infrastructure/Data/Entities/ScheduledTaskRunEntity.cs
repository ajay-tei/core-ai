namespace Diva.Infrastructure.Data.Entities;

/// <summary>Records one execution attempt of a scheduled task.</summary>
public class ScheduledTaskRunEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }

    public string ScheduledTaskId { get; set; } = string.Empty;

    /// <summary>"pending" (queued behind a running run) | "running" | "success" | "failed"</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The UTC time this run was originally due.</summary>
    public DateTime ScheduledForUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>Truncated agent response text for history display.</summary>
    public string? ResponseText { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Agent session ID created for this run; null if run never started.</summary>
    public string? SessionId { get; set; }

    public int AttemptNumber { get; set; } = 1;

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? IterationCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ScheduledTaskEntity ScheduledTask { get; set; } = null!;
}
