namespace Diva.Infrastructure.Scheduler;

/// <summary>Context loaded from a feedback token — pre-populates the feedback form.</summary>
public sealed record SchedulerFeedbackContext(
    string TaskName,
    string? AgentDisplayName,
    string RunId,
    string TaskId,
    string? SessionId,
    string TaskType,
    DateTime? RunCompletedAt,
    string? RunOutcome,
    string? RunSummary);

/// <summary>Request payload for submitting feedback.</summary>
public sealed record SubmitSchedulerFeedbackRequest(
    string Token,
    int? ThumbsRating,
    int? StarRating,
    string? Category,
    string? CorrectionText,
    string? SubmitterName,
    string? SubmitterEmail);

/// <summary>Admin review list item.</summary>
public sealed record SchedulerFeedbackDto(
    string Id,
    int TenantId,
    string RunId,
    string ScheduledTaskId,
    string TaskType,
    string? TaskName,
    string? AgentDisplayName,
    string? SessionId,
    int? ThumbsRating,
    int? StarRating,
    string? Category,
    string? CorrectionText,
    string? SubmitterName,
    string? SubmitterEmail,
    string Status,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    string? ReviewNotes);

public interface ISchedulerFeedbackService
{
    /// <summary>
    /// Validates the token and loads run/task context for the feedback form.
    /// Returns null if the token is invalid, expired, or the run no longer exists.
    /// Uses CreateDbContext() without TenantContext (anonymous path) and manually
    /// scopes queries using TenantId from the token claims.
    /// </summary>
    Task<SchedulerFeedbackContext?> GetContextByTokenAsync(string token, CancellationToken ct);

    /// <summary>Validates the token and persists the submitted feedback entity.</summary>
    Task SubmitAsync(SubmitSchedulerFeedbackRequest request, CancellationToken ct);

    /// <summary>Returns pending feedback items for admin review (scoped to tenantId).</summary>
    Task<List<SchedulerFeedbackDto>> GetPendingAsync(int tenantId, CancellationToken ct);

    /// <summary>Marks a feedback item approved. If CorrectionText is set, triggers rule learning.</summary>
    Task ApproveAsync(string id, int tenantId, CancellationToken ct);

    /// <summary>Marks a feedback item rejected with optional admin notes.</summary>
    Task RejectAsync(string id, int tenantId, string? notes, CancellationToken ct);
}
