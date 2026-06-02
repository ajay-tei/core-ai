namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Stores user-submitted feedback for a specific scheduler job run.
/// RunId is a plain string (no FK) so it works for both individual
/// ScheduledTaskRuns and GroupScheduledTaskRuns without a polymorphic FK.
/// SessionId is NOT stored here — it is loaded from the run record at
/// review time via GetContextByTokenAsync.
/// </summary>
public class SchedulerFeedbackEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }

    /// <summary>ID of the run (ScheduledTaskRunEntity or GroupScheduledTaskRunEntity).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>ID of the parent scheduled task definition.</summary>
    public string ScheduledTaskId { get; set; } = string.Empty;

    /// <summary>"individual" | "group"</summary>
    public string TaskType { get; set; } = "individual";

    // ── Feedback payload ──────────────────────────────────────────────────────

    /// <summary>1 = thumbs up, -1 = thumbs down, null = not rated.</summary>
    public int? ThumbsRating { get; set; }

    /// <summary>1–5 star rating, null = not rated.</summary>
    public int? StarRating { get; set; }

    /// <summary>"incorrect_data" | "missing_info" | "formatting" | "other" | null.</summary>
    public string? Category { get; set; }

    /// <summary>Free-text correction or comment from the submitter.</summary>
    public string? CorrectionText { get; set; }

    // ── Submitter identity (optional — form is anonymous) ─────────────────────

    public string? SubmitterName { get; set; }
    public string? SubmitterEmail { get; set; }

    // ── Review workflow ───────────────────────────────────────────────────────

    /// <summary>"pending" | "approved" | "rejected"</summary>
    public string Status { get; set; } = "pending";

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNotes { get; set; }
}
