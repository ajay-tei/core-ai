using Diva.Infrastructure.Data.Entities;

namespace Diva.Infrastructure.Scheduler;

public interface IScheduledTaskService
{
    Task<ScheduledTaskEntity> CreateAsync(int tenantId, CreateScheduledTaskRequest request, CancellationToken ct);
    Task<ScheduledTaskEntity?> GetAsync(int tenantId, string taskId, CancellationToken ct);
    Task<List<ScheduledTaskEntity>> ListAsync(int tenantId, CancellationToken ct);
    Task<ScheduledTaskEntity> UpdateAsync(int tenantId, string taskId, UpdateScheduledTaskRequest request, CancellationToken ct);
    Task DeleteAsync(int tenantId, string taskId, CancellationToken ct);
    Task<ScheduledTaskEntity> SetEnabledAsync(int tenantId, string taskId, bool enabled, CancellationToken ct);
    Task<ScheduledTaskRunEntity> TriggerNowAsync(int tenantId, string taskId, CancellationToken ct);
    Task<List<ScheduledTaskRunEntity>> GetRunHistoryAsync(int tenantId, string taskId, int limit, CancellationToken ct);

    /// <summary>Returns all enabled tasks whose NextRunUtc is due. Called by the hosted worker.</summary>
    Task<List<ScheduledTaskEntity>> GetDueTasksAsync(DateTime utcNow, CancellationToken ct);

    /// <summary>Creates a run record and advances NextRunUtc. Called by the hosted worker before dispatching.</summary>
    Task<ScheduledTaskRunEntity> BeginRunAsync(string taskId, DateTime scheduledForUtc, CancellationToken ct);

    /// <summary>
    /// Marks the oldest "pending" run for <paramref name="taskId"/> as "running".
    /// Returns null if no pending run exists.
    /// </summary>
    Task<ScheduledTaskRunEntity?> ActivateOldestPendingRunAsync(string taskId, CancellationToken ct);

    /// <summary>Finalises a run record after agent execution. Called by the hosted worker.</summary>
    Task CompleteRunAsync(string runId, bool success, string? responseText, string? errorMessage, string? sessionId, long durationMs,
        int? inputTokens, int? outputTokens, int? iterationCount, CancellationToken ct);

    // ── Group task scheduler support ──────────────────────────────────────────

    /// <summary>Returns all enabled group tasks whose NextRunUtc is due. Includes Group.Members.</summary>
    Task<List<GroupScheduledTaskEntity>> GetDueGroupTasksAsync(DateTime utcNow, CancellationToken ct);

    /// <summary>Returns the first enabled agent of <paramref name="agentType"/> for <paramref name="tenantId"/>.</summary>
    Task<AgentDefinitionEntity?> GetFirstEnabledAgentByTypeAsync(int tenantId, string agentType, CancellationToken ct);

    /// <summary>Creates a "running" group task run record for a specific member tenant.</summary>
    Task<GroupScheduledTaskRunEntity> BeginGroupRunAsync(string groupTaskId, int tenantId, int groupId, DateTime scheduledForUtc, CancellationToken ct);

    /// <summary>Finalises a group task run record after execution.</summary>
    Task CompleteGroupRunAsync(string runId, bool success, string? responseText, string? errorMessage, string? sessionId, long durationMs,
        int? inputTokens, int? outputTokens, int? iterationCount, CancellationToken ct);

    /// <summary>Advances NextRunUtc (and disables if once) after all members have been dispatched.</summary>
    Task AdvanceGroupTaskNextRunAsync(string groupTaskId, CancellationToken ct);

    /// <summary>
    /// Returns tasks that have at least one pending run but no active running run.
    /// Used by the hosted worker to dispatch manually-triggered (TriggerNow) runs.
    /// </summary>
    Task<List<(ScheduledTaskEntity Task, ScheduledTaskRunEntity Run)>> GetTasksWithOrphanedPendingRunsAsync(CancellationToken ct);

    /// <summary>
    /// Marks all runs whose status is "running" and whose StartedAtUtc is before
    /// <paramref name="cutoffUtc"/> (or is null) as failed with a timeout/restart error.
    /// Pass <see cref="DateTime.UtcNow"/> on startup to recover all stuck runs from a
    /// previous process; pass <c>UtcNow - timeout</c> during normal polling to catch
    /// genuinely hung runs. Returns the number of runs recovered.
    /// </summary>
    Task<int> RecoverStuckRunsAsync(DateTime cutoffUtc, CancellationToken ct);

    // ── Notification settings ───────────────────────────────────────────────

    Task<TenantNotificationSettingsEntity?> GetNotificationSettingsAsync(int tenantId, CancellationToken ct);
    Task UpsertNotificationSettingsAsync(int tenantId, string? globalNotifyEmails, string? globalNotifyOn, CancellationToken ct);

    // ── Feedback settings ───────────────────────────────────────────────────

    Task<TenantFeedbackSettingsEntity?> GetFeedbackSettingsAsync(int tenantId, CancellationToken ct);
    Task UpsertFeedbackSettingsAsync(int tenantId, bool enableFeedbackLinks, string? feedbackLinkBaseUrl, int expiryDays, CancellationToken ct);
    // ── Dashboard stats ───────────────────────────────────────────────

    Task<SchedulerStatsDto> GetStatsAsync(int tenantId, CancellationToken ct);
}

public sealed record CreateScheduledTaskRequest(
    string AgentId,
    string Name,
    string? Description,
    string ScheduleType,
    DateTime? ScheduledAtUtc,
    string? RunAtTime,
    int? DayOfWeek,
    string TimeZoneId,
    string PayloadType,
    string PromptText,
    string? ParametersJson,
    bool IsEnabled,
    string? NotifyEmails = null,
    string? NotifyOn = null,
    string? SuccessKeywords = null);

public sealed record UpdateScheduledTaskRequest(
    string? AgentId,
    string? Name,
    string? Description,
    string? ScheduleType,
    DateTime? ScheduledAtUtc,
    string? RunAtTime,
    int? DayOfWeek,
    string? TimeZoneId,
    string? PayloadType,
    string? PromptText,
    string? ParametersJson,
    bool? IsEnabled,
    string? NotifyEmails = null,
    string? NotifyOn = null,
    string? SuccessKeywords = null);

/// <summary>Aggregate scheduler stats for the dashboard.</summary>
public sealed record SchedulerStatsDto(
    int TotalTasks,
    int EnabledTasks,
    int TodayRuns,
    int TodaySucceeded,
    int TodayFailed,
    int TodaySkipped,
    List<SchedulerRecentFailureDto> RecentFailures);

public sealed record SchedulerRecentFailureDto(
    string TaskId,
    string TaskName,
    DateTime ScheduledForUtc,
    string Status,
    string? ErrorMessage);
