using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Scheduler;

public sealed class ScheduledTaskService : IScheduledTaskService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IOptions<TaskSchedulerOptions> _opts;
    private readonly ILogger<ScheduledTaskService> _logger;

    public ScheduledTaskService(
        IDatabaseProviderFactory db,
        IOptions<TaskSchedulerOptions> opts,
        ILogger<ScheduledTaskService> logger)
    {
        _db = db;
        _opts = opts;
        _logger = logger;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    public async Task<ScheduledTaskEntity> CreateAsync(
        int tenantId, CreateScheduledTaskRequest req, CancellationToken ct)
    {
        Validate(req.ScheduleType, req.ScheduledAtUtc, req.RunAtTime, req.DayOfWeek, req.TimeZoneId, req.PromptText);

        var entity = new ScheduledTaskEntity
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            AgentId = req.AgentId,
            Name = req.Name,
            Description = req.Description,
            ScheduleType = req.ScheduleType,
            ScheduledAtUtc = req.ScheduledAtUtc,
            RunAtTime = req.RunAtTime,
            DayOfWeek = req.DayOfWeek,
            TimeZoneId = req.TimeZoneId,
            PayloadType = req.PayloadType,
            PromptText = req.PromptText,
            ParametersJson = req.ParametersJson,
            IsEnabled = req.IsEnabled,
            NotifyEmails = req.NotifyEmails,
            NotifyOn = req.NotifyOn,
            SuccessKeywords = req.SuccessKeywords,
            CreatedAt = DateTime.UtcNow,
            NextRunUtc = null
        };

        entity.NextRunUtc = req.IsEnabled ? ComputeNextRunUtc(entity, DateTime.UtcNow) : null;

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        db.ScheduledTasks.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<ScheduledTaskEntity?> GetAsync(int tenantId, string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.ScheduledTasks.FindAsync([taskId], ct);
    }

    public async Task<List<ScheduledTaskEntity>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.ScheduledTasks
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ScheduledTaskEntity> UpdateAsync(
        int tenantId, string taskId, UpdateScheduledTaskRequest req, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new KeyNotFoundException($"Scheduled task '{taskId}' not found.");

        if (req.AgentId is not null) entity.AgentId = req.AgentId;
        if (req.Name is not null) entity.Name = req.Name;
        if (req.Description is not null) entity.Description = req.Description;
        if (req.ScheduleType is not null) entity.ScheduleType = req.ScheduleType;
        if (req.ScheduledAtUtc is not null) entity.ScheduledAtUtc = req.ScheduledAtUtc;
        if (req.RunAtTime is not null) entity.RunAtTime = req.RunAtTime;
        if (req.DayOfWeek is not null) entity.DayOfWeek = req.DayOfWeek;
        if (req.TimeZoneId is not null) entity.TimeZoneId = req.TimeZoneId;
        if (req.PayloadType is not null) entity.PayloadType = req.PayloadType;
        if (req.PromptText is not null) entity.PromptText = req.PromptText;
        if (req.ParametersJson is not null) entity.ParametersJson = req.ParametersJson;
        if (req.IsEnabled is not null) entity.IsEnabled = req.IsEnabled.Value;
        if (req.NotifyEmails is not null) entity.NotifyEmails = req.NotifyEmails;
        if (req.NotifyOn is not null) entity.NotifyOn = req.NotifyOn;
        if (req.SuccessKeywords is not null) entity.SuccessKeywords = req.SuccessKeywords;

        Validate(entity.ScheduleType, entity.ScheduledAtUtc, entity.RunAtTime,
                 entity.DayOfWeek, entity.TimeZoneId, entity.PromptText);

        entity.UpdatedAt = DateTime.UtcNow;
        entity.NextRunUtc = entity.IsEnabled ? ComputeNextRunUtc(entity, DateTime.UtcNow) : null;

        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteAsync(int tenantId, string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new KeyNotFoundException($"Scheduled task '{taskId}' not found.");
        db.ScheduledTasks.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ScheduledTaskEntity> SetEnabledAsync(
        int tenantId, string taskId, bool enabled, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new KeyNotFoundException($"Scheduled task '{taskId}' not found.");

        entity.IsEnabled = enabled;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.NextRunUtc = enabled ? ComputeNextRunUtc(entity, DateTime.UtcNow) : null;

        await db.SaveChangesAsync(ct);
        return entity;
    }

    // ── Run operations ────────────────────────────────────────────────────────

    public async Task<ScheduledTaskRunEntity> TriggerNowAsync(
        int tenantId, string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new KeyNotFoundException($"Scheduled task '{taskId}' not found.");

        // Check active run count (queue-on-overlap applies to trigger-now too)
        var running = await db.ScheduledTaskRuns
            .CountAsync(r => r.ScheduledTaskId == taskId && (r.Status == "pending" || r.Status == "running"), ct);

        var run = new ScheduledTaskRunEntity
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = tenantId,
            ScheduledTaskId = taskId,
            Status = running >= _opts.Value.MaxQueuedRunsPerTask ? "skipped" : "pending",
            ScheduledForUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            AttemptNumber = running + 1
        };

        db.ScheduledTaskRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<List<ScheduledTaskRunEntity>> GetRunHistoryAsync(
        int tenantId, string taskId, int limit, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.ScheduledTaskRuns
            .Where(r => r.ScheduledTaskId == taskId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> RecoverStuckRunsAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var stuck = await db.ScheduledTaskRuns
            .Where(r => r.Status == "running" &&
                        (r.StartedAtUtc == null || r.StartedAtUtc < cutoffUtc))
            .ToListAsync(ct);

        if (stuck.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var run in stuck)
        {
            run.Status = "failed";
            run.CompletedAtUtc = now;
            run.ErrorMessage = "Run did not complete — marked failed by scheduler recovery (process restart or timeout).";
            if (run.StartedAtUtc.HasValue)
                run.DurationMs = (long)(now - run.StartedAtUtc.Value).TotalMilliseconds;
        }

        await db.SaveChangesAsync(ct);
        return stuck.Count;
    }

    // ── Scheduler worker support ──────────────────────────────────────────────

    public async Task<List<ScheduledTaskEntity>> GetDueTasksAsync(DateTime utcNow, CancellationToken ct)
    {
        // System context (tenantId = 0) bypasses tenant query filter → returns all tenants
        using var db = _db.CreateDbContext();
        return await db.ScheduledTasks
            .Where(t => t.IsEnabled && t.NextRunUtc != null && t.NextRunUtc <= utcNow)
            .ToListAsync(ct);
    }

    public async Task<List<(ScheduledTaskEntity Task, ScheduledTaskRunEntity Run)>> GetTasksWithOrphanedPendingRunsAsync(CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        // Task IDs that have at least one pending run
        var taskIdsWithPending = await db.ScheduledTaskRuns
            .Where(r => r.Status == "pending")
            .Select(r => r.ScheduledTaskId)
            .Distinct()
            .ToListAsync(ct);

        if (taskIdsWithPending.Count == 0) return [];

        // Of those, which already have an active running run (not orphaned)
        var taskIdsWithRunning = await db.ScheduledTaskRuns
            .Where(r => r.Status == "running" && taskIdsWithPending.Contains(r.ScheduledTaskId))
            .Select(r => r.ScheduledTaskId)
            .Distinct()
            .ToListAsync(ct);

        var orphanedTaskIds = taskIdsWithPending.Except(taskIdsWithRunning).ToList();
        if (orphanedTaskIds.Count == 0) return [];

        var result = new List<(ScheduledTaskEntity, ScheduledTaskRunEntity)>();
        foreach (var taskId in orphanedTaskIds)
        {
            var task = await db.ScheduledTasks.FindAsync([taskId], ct);
            if (task is null || !task.IsEnabled) continue;

            var run = await db.ScheduledTaskRuns
                .Where(r => r.ScheduledTaskId == taskId && r.Status == "pending")
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (run is null) continue;

            result.Add((task, run));
        }
        return result;
    }

    public async Task<ScheduledTaskRunEntity?> ActivateOldestPendingRunAsync(
        string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var run = await db.ScheduledTaskRuns
            .Where(r => r.ScheduledTaskId == taskId && r.Status == "pending")
            .OrderBy(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (run is null) return null;

        run.Status = "running";
        run.StartedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<ScheduledTaskRunEntity> BeginRunAsync(
        string taskId, DateTime scheduledForUtc, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var task = await db.ScheduledTasks.FindAsync([taskId], ct)
            ?? throw new InvalidOperationException($"Task '{taskId}' missing in BeginRunAsync.");

        // Count active + queued runs for the task
        var activeRuns = await db.ScheduledTaskRuns
            .CountAsync(r => r.ScheduledTaskId == taskId && (r.Status == "pending" || r.Status == "running"), ct);

        string status;
        if (activeRuns == 0)
            status = "running";
        else if (activeRuns >= _opts.Value.MaxQueuedRunsPerTask)
            status = "skipped";
        else
            status = "pending";

        var run = new ScheduledTaskRunEntity
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = task.TenantId,
            ScheduledTaskId = taskId,
            Status = status,
            ScheduledForUtc = scheduledForUtc,
            StartedAtUtc = status == "running" ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            AttemptNumber = activeRuns + 1
        };

        db.ScheduledTaskRuns.Add(run);

        // Advance NextRunUtc so this task doesn't fire again until next window
        task.LastRunAtUtc = scheduledForUtc;
        task.NextRunUtc = ComputeNextRunUtc(task, scheduledForUtc);

        // Track skipped runs on the parent task for the UI badge
        if (status == "skipped")
            task.LastRunStatus = "skipped";

        // Disable one-time tasks once dispatched
        if (task.ScheduleType == "once" && status != "skipped")
            task.IsEnabled = false;

        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task CompleteRunAsync(
        string runId, bool success, string? responseText, string? errorMessage,
        string? sessionId, long durationMs,
        int? inputTokens, int? outputTokens, int? iterationCount,
        CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var run = await db.ScheduledTaskRuns.FindAsync([runId], ct);
        if (run is null)
        {
            _logger.LogWarning("CompleteRunAsync: run '{RunId}' not found.", runId);
            return;
        }

        run.Status = success ? "success" : "failed";
        run.CompletedAtUtc = DateTime.UtcNow;
        run.DurationMs = durationMs;
        run.ResponseText = responseText is { Length: > 0 }
            ? responseText[..Math.Min(responseText.Length, _opts.Value.MaxResponseStorageChars)]
            : null;
        run.ErrorMessage = errorMessage;
        run.SessionId = sessionId;
        run.InputTokens = inputTokens;
        run.OutputTokens = outputTokens;
        run.IterationCount = iterationCount;

        // Update parent task's last-run status for the dashboard badge
        var task = await db.ScheduledTasks.FindAsync([run.ScheduledTaskId], ct);
        if (task is not null)
            task.LastRunStatus = success ? "success" : "failed";

        await db.SaveChangesAsync(ct);

        // Promote the oldest pending run for this task so the next poll cycle can dispatch it.
        // (The hosted service queries GetDispatchableQueuedRunsAsync each poll cycle.)
        // We just need to mark it as explicitly ready — leave Status = "pending" here;
        // the hosted service will pick it up and call BeginRunAsync which will set it to "running".
    }

    // ── Group task scheduler support ──────────────────────────────────────────

    public async Task<List<GroupScheduledTaskEntity>> GetDueGroupTasksAsync(DateTime utcNow, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupScheduledTasks
            .Where(t => t.IsEnabled && t.NextRunUtc != null && t.NextRunUtc <= utcNow)
            .Include(t => t.Group)
                .ThenInclude(g => g.Members)
            .ToListAsync(ct);
    }

    public async Task<AgentDefinitionEntity?> GetFirstEnabledAgentByTypeAsync(
        int tenantId, string agentType, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.AgentDefinitions
            .Where(a => a.AgentType == agentType && a.IsEnabled)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<GroupScheduledTaskRunEntity> BeginGroupRunAsync(
        string groupTaskId, int tenantId, int groupId, DateTime scheduledForUtc, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var run = new GroupScheduledTaskRunEntity
        {
            Id = Guid.NewGuid().ToString(),
            GroupTaskId = groupTaskId,
            TenantId = tenantId,
            GroupId = groupId,
            Status = "running",
            ScheduledForUtc = scheduledForUtc,
            StartedAtUtc = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.GroupScheduledTaskRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task CompleteGroupRunAsync(
        string runId, bool success, string? responseText, string? errorMessage,
        string? sessionId, long durationMs,
        int? inputTokens, int? outputTokens, int? iterationCount,
        CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var run = await db.GroupScheduledTaskRuns.FindAsync([runId], ct);
        if (run is null)
        {
            _logger.LogWarning("CompleteGroupRunAsync: run '{RunId}' not found.", runId);
            return;
        }

        run.Status = success ? "success" : "failed";
        run.CompletedAtUtc = DateTime.UtcNow;
        run.DurationMs = durationMs;
        run.ResponseText = responseText is { Length: > 0 }
            ? responseText[..Math.Min(responseText.Length, _opts.Value.MaxResponseStorageChars)]
            : null;
        run.ErrorMessage = errorMessage;
        run.SessionId = sessionId;
        run.InputTokens = inputTokens;
        run.OutputTokens = outputTokens;
        run.IterationCount = iterationCount;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TenantNotificationSettingsEntity?> GetNotificationSettingsAsync(
        int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantNotificationSettings.FindAsync([tenantId], ct);
    }

    public async Task UpsertNotificationSettingsAsync(
        int tenantId, string? globalNotifyEmails, string? globalNotifyOn, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var existing = await db.TenantNotificationSettings.FindAsync([tenantId], ct);
        if (existing is null)
        {
            db.TenantNotificationSettings.Add(new TenantNotificationSettingsEntity
            {
                TenantId = tenantId,
                GlobalNotifyEmails = globalNotifyEmails,
                GlobalNotifyOn = globalNotifyOn,
            });
        }
        else
        {
            existing.GlobalNotifyEmails = globalNotifyEmails;
            existing.GlobalNotifyOn = globalNotifyOn;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<SchedulerStatsDto> GetStatsAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var todayUtc = DateTime.UtcNow.Date;
        var tomorrowUtc = todayUtc.AddDays(1);

        var allTasks = await db.ScheduledTasks.ToListAsync(ct);
        var todayRuns = await db.ScheduledTaskRuns
            .Where(r => r.ScheduledForUtc >= todayUtc && r.ScheduledForUtc < tomorrowUtc)
            .ToListAsync(ct);

        var recentFailures = await db.ScheduledTaskRuns
            .Where(r => r.Status == "failed" || r.Status == "skipped")
            .OrderByDescending(r => r.ScheduledForUtc)
            .Take(10)
            .Join(db.ScheduledTasks, r => r.ScheduledTaskId, t => t.Id,
                  (r, t) => new SchedulerRecentFailureDto(t.Id, t.Name, r.ScheduledForUtc, r.Status, r.ErrorMessage))
            .ToListAsync(ct);

        return new SchedulerStatsDto(
            TotalTasks: allTasks.Count,
            EnabledTasks: allTasks.Count(t => t.IsEnabled),
            TodayRuns: todayRuns.Count,
            TodaySucceeded: todayRuns.Count(r => r.Status == "success"),
            TodayFailed: todayRuns.Count(r => r.Status == "failed"),
            TodaySkipped: todayRuns.Count(r => r.Status == "skipped"),
            RecentFailures: recentFailures);
    }

    public async Task AdvanceGroupTaskNextRunAsync(string groupTaskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var task = await db.GroupScheduledTasks.FindAsync([groupTaskId], ct);
        if (task is null) return;

        task.LastRunAtUtc = DateTime.UtcNow;
        task.NextRunUtc = ComputeNextRunUtc(
            task.ScheduleType, task.ScheduledAtUtc, task.RunAtTime,
            task.DayOfWeek, task.TimeZoneId, DateTime.UtcNow);

        if (task.ScheduleType == "once")
            task.IsEnabled = false;

        await db.SaveChangesAsync(ct);
    }

    // ── Next-run calculation ──────────────────────────────────────────────────

    /// <summary>
    /// Computes the next UTC run time for <paramref name="task"/> from <paramref name="fromUtc"/>.
    /// Returns null for one-time tasks that have already been used.
    /// Pure — no side effects. Testable as internal static.
    /// </summary>
    internal static DateTime? ComputeNextRunUtc(ScheduledTaskEntity task, DateTime fromUtc)
        => ComputeNextRunUtc(task.ScheduleType, task.ScheduledAtUtc, task.RunAtTime,
                             task.DayOfWeek, task.TimeZoneId, fromUtc);

    /// <summary>
    /// Public overload — accepts raw schedule fields so callers outside this assembly
    /// (e.g. TenantGroupService for GroupScheduledTaskEntity) can reuse the same logic.
    /// </summary>
    public static DateTime? ComputeNextRunUtc(
        string scheduleType, DateTime? scheduledAtUtc, string? runAtTime,
        int? dayOfWeek, string timeZoneId, DateTime fromUtc)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { tz = TimeZoneInfo.Utc; }

        // Synthesize a lightweight proxy for the private helpers
        var proxy = new ScheduledTaskEntity
        {
            ScheduleType = scheduleType,
            ScheduledAtUtc = scheduledAtUtc,
            RunAtTime = runAtTime,
            DayOfWeek = dayOfWeek,
            TimeZoneId = timeZoneId,
        };

        return scheduleType switch
        {
            "once" => ComputeOnce(proxy, fromUtc),
            "hourly" => ComputeHourly(fromUtc),
            "daily" => ComputeDaily(proxy, tz, fromUtc),
            "weekly" => ComputeWeekly(proxy, tz, fromUtc),
            _ => null
        };
    }

    private static DateTime? ComputeOnce(ScheduledTaskEntity task, DateTime fromUtc)
    {
        if (task.ScheduledAtUtc is null) return null;
        return task.ScheduledAtUtc.Value > fromUtc ? task.ScheduledAtUtc.Value : null;
    }

    private static DateTime? ComputeHourly(DateTime fromUtc)
        => fromUtc.AddHours(1);

    private static DateTime? ComputeDaily(ScheduledTaskEntity task, TimeZoneInfo tz, DateTime fromUtc)
    {
        if (!TryParseRunAtTime(task.RunAtTime, out var tod)) return null;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz);
        var candidate = localNow.Date.Add(tod);
        var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
        if (candidateUtc <= fromUtc)
            candidateUtc = candidateUtc.AddDays(1);
        return candidateUtc;
    }

    private static DateTime? ComputeWeekly(ScheduledTaskEntity task, TimeZoneInfo tz, DateTime fromUtc)
    {
        if (!TryParseRunAtTime(task.RunAtTime, out var tod)) return null;
        if (task.DayOfWeek is null) return null;

        var target = (DayOfWeek)task.DayOfWeek.Value;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz);
        int daysUntil = ((int)target - (int)localNow.DayOfWeek + 7) % 7;
        var candidate = localNow.Date.AddDays(daysUntil).Add(tod);
        var candidateUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
        if (candidateUtc <= fromUtc)
            candidateUtc = candidateUtc.AddDays(7);
        return candidateUtc;
    }

    internal static bool TryParseRunAtTime(string? runAtTime, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(runAtTime)) return false;

        var parts = runAtTime.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || h is < 0 or > 23) return false;
        if (!int.TryParse(parts[1], out var m) || m is < 0 or > 59) return false;

        result = new TimeSpan(h, m, 0);
        return true;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static void Validate(
        string scheduleType, DateTime? scheduledAtUtc, string? runAtTime,
        int? dayOfWeek, string timeZoneId, string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("PromptText is required.");

        if (scheduleType == "once" && scheduledAtUtc is null)
            throw new ArgumentException("ScheduledAtUtc is required for one-time schedules.");

        if (scheduleType is "daily" or "weekly" && !TryParseRunAtTime(runAtTime, out _))
            throw new ArgumentException("RunAtTime (HH:mm) is required for daily/weekly schedules.");

        if (scheduleType is not ("once" or "hourly" or "daily" or "weekly"))
            throw new ArgumentException($"Unrecognised scheduleType: '{scheduleType}'.");

        if (scheduleType == "weekly" && dayOfWeek is null)
            throw new ArgumentException("DayOfWeek is required for weekly schedules.");

        if (scheduleType == "weekly" && dayOfWeek is < 0 or > 6)
            throw new ArgumentException("DayOfWeek must be 0–6 (Sunday=0).");

        try { TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch { throw new ArgumentException($"Unrecognised timezone: '{timeZoneId}'."); }
    }
}
