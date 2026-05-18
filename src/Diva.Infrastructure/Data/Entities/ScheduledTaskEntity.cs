using System.ComponentModel.DataAnnotations.Schema;

namespace Diva.Infrastructure.Data.Entities;

/// <summary>Persisted schedule definition — owns the recurrence config and task payload.</summary>
public class ScheduledTaskEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }

    /// <summary>FK to AgentDefinitionEntity. The agent that will be invoked.</summary>
    public string AgentId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // ── Schedule configuration ────────────────────────────────────────────────
    /// <summary>"once" | "daily" | "weekly"</summary>
    public string ScheduleType { get; set; } = "once";

    /// <summary>UTC run time for one-time schedules (ScheduleType = "once").</summary>
    public DateTime? ScheduledAtUtc { get; set; }

    /// <summary>Time of day for recurring schedules in HH:mm format (e.g. "09:00").</summary>
    public string? RunAtTime { get; set; }

    /// <summary>0–6 (Sunday = 0) for ScheduleType = "weekly".</summary>
    public int? DayOfWeek { get; set; }

    /// <summary>IANA or Windows timezone ID (e.g. "UTC", "America/New_York").</summary>
    public string TimeZoneId { get; set; } = "UTC";

    // ── Task payload ──────────────────────────────────────────────────────────
    /// <summary>"prompt" — PromptText is sent as-is. "template" — PromptText may contain {{var}} placeholders resolved from ParametersJson.</summary>
    public string PayloadType { get; set; } = "prompt";

    /// <summary>The agent query/prompt to execute.</summary>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>JSON dictionary of substitution values used when PayloadType = "template".</summary>
    public string? ParametersJson { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastRunAtUtc { get; set; }

    /// <summary>Pre-computed next run time in UTC. Updated after each run and on configuration change.</summary>
    public DateTime? NextRunUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ── Notification config ───────────────────────────────────────────────────

    /// <summary>Comma-separated email addresses to notify after this job runs. Null = per-job notification disabled.</summary>
    public string? NotifyEmails { get; set; }

    /// <summary>"failure" | "success" | "always" | null (disabled).</summary>
    public string? NotifyOn { get; set; }

    /// <summary>Outcome of the most recent run: "success" | "failed" | "skipped" | null (never run).</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>
    /// Optional comma-separated phrases that confirm the agent task succeeded.
    /// If set, at least one phrase must appear in the final response content
    /// (case-insensitive) or the run is marked failed.
    /// </summary>
    [Column("FailureKeywords")]
    public string? SuccessKeywords { get; set; }
}
