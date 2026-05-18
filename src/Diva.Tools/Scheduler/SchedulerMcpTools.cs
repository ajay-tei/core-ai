using System.ComponentModel;
using System.Text.Json;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Tools.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Diva.Tools.Scheduler;

[McpServerToolType]
public sealed class SchedulerMcpTools(
    IHttpContextAccessor http,
    IServiceScopeFactory scopeFactory,
    ILogger<SchedulerMcpTools> logger) : IDivaMcpToolType
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private string Error(string message) =>
        JsonSerializer.Serialize(new { error = "Error", message }, _json);

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a summary of all scheduled job runs for a given UTC date (default: today).
    /// Includes both tenant tasks and group tasks. Each row contains: taskId, taskName,
    /// status, scheduledForUtc, completedAtUtc, durationMs, errorMessage.
    /// </summary>
    [McpServerTool, Description(
        "Get a summary of all scheduled job runs for a specific UTC date (defaults to today). " +
        "Returns both tenant tasks and group tasks with status, duration, and any error messages.")]
    public async Task<string> get_job_run_summary(
        [Description("UTC date in yyyy-MM-dd format (e.g. '2026-05-15'). Defaults to today.")] string? date_utc = null,
        [Description("Maximum number of records to return per job type (default: 100).")] int? limit = null)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            var day = date_utc is not null ? DateTime.Parse(date_utc).Date : DateTime.UtcNow.Date;
            var dayEnd = day.AddDays(1);
            var take = limit ?? 100;

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

            // Tenant scheduled task runs
            var taskRuns = await db.ScheduledTaskRuns
                .Where(r => r.TenantId == ctx.TenantId
                         && r.ScheduledForUtc >= day
                         && r.ScheduledForUtc < dayEnd)
                .OrderBy(r => r.ScheduledForUtc)
                .Take(take)
                .Join(db.ScheduledTasks,
                      r => r.ScheduledTaskId,
                      t => t.Id,
                      (r, t) => new
                      {
                          source = "task",
                          runId = r.Id,
                          taskId = r.ScheduledTaskId,
                          taskName = t.Name,
                          agentId = t.AgentId,
                          status = r.Status,
                          scheduledForUtc = r.ScheduledForUtc,
                          startedAtUtc = r.StartedAtUtc,
                          completedAtUtc = r.CompletedAtUtc,
                          durationMs = r.DurationMs,
                          attemptNumber = r.AttemptNumber,
                          errorMessage = r.ErrorMessage,
                          sessionId = r.SessionId,
                          inputTokens = r.InputTokens,
                          outputTokens = r.OutputTokens,
                          iterationCount = r.IterationCount
                      })
                .ToListAsync();

            // Group scheduled task runs (for this tenant as member)
            var groupRuns = await db.GroupScheduledTaskRuns
                .Where(r => r.TenantId == ctx.TenantId
                         && r.ScheduledForUtc >= day
                         && r.ScheduledForUtc < dayEnd)
                .OrderBy(r => r.ScheduledForUtc)
                .Take(take)
                .Join(db.GroupScheduledTasks,
                      r => r.GroupTaskId,
                      t => t.Id,
                      (r, t) => new
                      {
                          source = "group_task",
                          runId = r.Id,
                          taskId = r.GroupTaskId,
                          taskName = t.Name,
                          agentId = (string?)null,
                          status = r.Status,
                          scheduledForUtc = r.ScheduledForUtc,
                          startedAtUtc = r.StartedAtUtc,
                          completedAtUtc = r.CompletedAtUtc,
                          durationMs = r.DurationMs,
                          attemptNumber = (int?)null,
                          errorMessage = r.ErrorMessage,
                          sessionId = r.SessionId,
                          inputTokens = r.InputTokens,
                          outputTokens = r.OutputTokens,
                          iterationCount = r.IterationCount
                      })
                .ToListAsync();

            var allRuns = taskRuns.Cast<object>().Concat(groupRuns.Cast<object>()).ToList();

            var summary = new
            {
                date = day.ToString("yyyy-MM-dd"),
                tenantId = ctx.TenantId,
                totalRuns = allRuns.Count,
                successCount = taskRuns.Count(r => r.status == "success") + groupRuns.Count(r => r.status == "success"),
                failedCount = taskRuns.Count(r => r.status == "failed") + groupRuns.Count(r => r.status == "failed"),
                runningCount = taskRuns.Count(r => r.status == "running") + groupRuns.Count(r => r.status == "running"),
                pendingCount = taskRuns.Count(r => r.status == "pending") + groupRuns.Count(r => r.status == "pending"),
                runs = allRuns
            };

            return JsonSerializer.Serialize(summary, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "get_job_run_summary failed for tenant {TenantId}", ctx.TenantId);
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Returns the run history for a single scheduled task.
    /// </summary>
    [McpServerTool, Description(
        "Get the execution history for a specific scheduled task by its ID. " +
        "Returns runs in reverse chronological order with status, duration, and error details.")]
    public async Task<string> get_job_run_history(
        [Description("Scheduled task ID (GUID string).")] string task_id,
        [Description("Maximum number of records to return (default: 20).")] int? limit = null)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            var take = limit ?? 20;

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

            var task = await db.ScheduledTasks
                .FirstOrDefaultAsync(t => t.Id == task_id && t.TenantId == ctx.TenantId);

            if (task is null)
                return JsonSerializer.Serialize(new { error = "NotFound", message = $"Task {task_id} not found" }, _json);

            var runs = await db.ScheduledTaskRuns
                .Where(r => r.ScheduledTaskId == task_id && r.TenantId == ctx.TenantId)
                .OrderByDescending(r => r.ScheduledForUtc)
                .Take(take)
                .Select(r => new
                {
                    runId = r.Id,
                    status = r.Status,
                    scheduledForUtc = r.ScheduledForUtc,
                    startedAtUtc = r.StartedAtUtc,
                    completedAtUtc = r.CompletedAtUtc,
                    durationMs = r.DurationMs,
                    attemptNumber = r.AttemptNumber,
                    errorMessage = r.ErrorMessage,
                    sessionId = r.SessionId,
                    inputTokens = r.InputTokens,
                    outputTokens = r.OutputTokens,
                    iterationCount = r.IterationCount
                })
                .ToListAsync();

            return JsonSerializer.Serialize(new
            {
                taskId = task_id,
                taskName = task.Name,
                agentId = task.AgentId,
                schedule = new { type = task.ScheduleType, runAtTime = task.RunAtTime, timeZoneId = task.TimeZoneId },
                totalReturned = runs.Count,
                runs
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "get_job_run_history failed for task {TaskId}", task_id);
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// Returns all failed or stuck (running beyond a threshold) job runs within the last N hours.
    /// </summary>
    [McpServerTool, Description(
        "Get all failed or stuck scheduled job runs within the last N hours (default: 24). " +
        "Covers both tenant tasks and group tasks. Use this to detect missed or broken scheduled jobs.")]
    public async Task<string> get_failed_jobs(
        [Description("How many hours back to search (default: 24).")] int? since_hours = null)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            var hours = since_hours ?? 24;
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            var stuckCutoff = DateTime.UtcNow.AddMinutes(-30); // running >30 min = stuck

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

            // Failed tenant task runs
            var failedTaskRuns = await db.ScheduledTaskRuns
                .Where(r => r.TenantId == ctx.TenantId
                         && r.ScheduledForUtc >= cutoff
                         && (r.Status == "failed"
                             || (r.Status == "running" && r.StartedAtUtc != null && r.StartedAtUtc < stuckCutoff)))
                .OrderByDescending(r => r.ScheduledForUtc)
                .Join(db.ScheduledTasks,
                      r => r.ScheduledTaskId,
                      t => t.Id,
                      (r, t) => new
                      {
                          source = "task",
                          runId = r.Id,
                          taskId = r.ScheduledTaskId,
                          taskName = t.Name,
                          status = r.Status,
                          scheduledForUtc = r.ScheduledForUtc,
                          startedAtUtc = r.StartedAtUtc,
                          durationMs = r.DurationMs,
                          attemptNumber = r.AttemptNumber,
                          errorMessage = r.ErrorMessage
                      })
                .ToListAsync();

            // Failed group task runs (for this tenant)
            var failedGroupRuns = await db.GroupScheduledTaskRuns
                .Where(r => r.TenantId == ctx.TenantId
                         && r.ScheduledForUtc >= cutoff
                         && (r.Status == "failed"
                             || (r.Status == "running" && r.StartedAtUtc != null && r.StartedAtUtc < stuckCutoff)))
                .OrderByDescending(r => r.ScheduledForUtc)
                .Join(db.GroupScheduledTasks,
                      r => r.GroupTaskId,
                      t => t.Id,
                      (r, t) => new
                      {
                          source = "group_task",
                          runId = r.Id,
                          taskId = r.GroupTaskId,
                          taskName = t.Name,
                          status = r.Status,
                          scheduledForUtc = r.ScheduledForUtc,
                          startedAtUtc = r.StartedAtUtc,
                          durationMs = r.DurationMs,
                          attemptNumber = (int?)null,
                          errorMessage = r.ErrorMessage
                      })
                .ToListAsync();

            var allFailed = failedTaskRuns.Cast<object>().Concat(failedGroupRuns.Cast<object>()).ToList();

            return JsonSerializer.Serialize(new
            {
                sinceHours = hours,
                cutoffUtc = cutoff,
                tenantId = ctx.TenantId,
                totalFailed = allFailed.Count,
                jobs = allFailed
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "get_failed_jobs failed for tenant {TenantId}", ctx.TenantId);
            return Error(ex.Message);
        }
    }
}
