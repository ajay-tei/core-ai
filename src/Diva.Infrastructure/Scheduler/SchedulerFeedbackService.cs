using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Scheduler;

public sealed class SchedulerFeedbackService : ISchedulerFeedbackService
{
    private readonly ISchedulerFeedbackTokenService _tokenSvc;
    private readonly IDatabaseProviderFactory _db;
    private readonly FeedbackLearningService _learningService;
    private readonly ILogger<SchedulerFeedbackService> _logger;

    public SchedulerFeedbackService(
        ISchedulerFeedbackTokenService tokenSvc,
        IDatabaseProviderFactory db,
        FeedbackLearningService learningService,
        ILogger<SchedulerFeedbackService> logger)
    {
        _tokenSvc = tokenSvc;
        _db = db;
        _learningService = learningService;
        _logger = logger;
    }

    // ── Anonymous path ────────────────────────────────────────────────────────

    public async Task<SchedulerFeedbackContext?> GetContextByTokenAsync(string token, CancellationToken ct)
    {
        var claims = _tokenSvc.Validate(token);
        if (claims is null) return null;

        // CreateDbContext() without TenantContext → _currentTenantId = 0 → query filter is bypassed.
        // We manually scope every query with the TenantId from the validated token.
        using var db = _db.CreateDbContext();

        if (claims.TaskType == "group")
            return await LoadGroupContextAsync(db, claims, ct);

        return await LoadIndividualContextAsync(db, claims, ct);
    }

    public async Task SubmitAsync(SubmitSchedulerFeedbackRequest request, CancellationToken ct)
    {
        var claims = _tokenSvc.Validate(request.Token);
        if (claims is null) throw new ArgumentException("Invalid or expired feedback token.");

        // Validate rating ranges
        if (request.ThumbsRating is not null and not 1 and not (-1))
            throw new ArgumentException("ThumbsRating must be 1 (up) or -1 (down).");
        if (request.StarRating is < 1 or > 5)
            throw new ArgumentException("StarRating must be between 1 and 5.");

        var entity = new SchedulerFeedbackEntity
        {
            TenantId = claims.TenantId,
            RunId = claims.RunId,
            ScheduledTaskId = claims.TaskId,
            TaskType = claims.TaskType,
            ThumbsRating = request.ThumbsRating,
            StarRating = request.StarRating,
            Category = request.Category,
            CorrectionText = request.CorrectionText?.Trim(),
            SubmitterName = request.SubmitterName?.Trim(),
            SubmitterEmail = request.SubmitterEmail?.Trim(),
            Status = "pending",
            SubmittedAt = DateTime.UtcNow
        };

        // EF query filters only affect SELECT — inserts always work regardless of TenantContext.
        using var db = _db.CreateDbContext();
        db.SchedulerFeedbacks.Add(entity);

        Exception? ex = null;
        try { await db.SaveChangesAsync(ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "Failed to persist scheduler feedback for run '{RunId}'.", claims.RunId);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            throw ex; // unreachable but satisfies compiler
        }

        _logger.LogInformation(
            "Scheduler feedback submitted for run '{RunId}' (tenant {TenantId}).",
            claims.RunId, claims.TenantId);
    }

    // ── Admin paths ───────────────────────────────────────────────────────────

    public async Task<List<SchedulerFeedbackDto>> GetPendingAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var items = await db.SchedulerFeedbacks
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.Status == "pending")
            .OrderByDescending(f => f.SubmittedAt)
            .ToListAsync(ct);

        // Enrich with task names (best-effort — task may have been deleted)
        var taskIds = items.Select(f => f.ScheduledTaskId).Distinct().ToList();
        var taskNames = await db.ScheduledTasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id) && t.TenantId == tenantId)
            .Select(t => new { t.Id, t.Name, t.AgentId })
            .ToListAsync(ct);

        var agentIds = taskNames.Select(t => t.AgentId).Distinct().ToList();
        var agentNames = await db.AgentDefinitions
            .AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.DisplayName })
            .ToListAsync(ct);

        var taskMap = taskNames.ToDictionary(t => t.Id);
        var agentMap = agentNames.ToDictionary(a => a.Id);

        // Resolve sessionId from run records (best-effort)
        var runIds = items.Select(f => f.RunId).Distinct().ToList();
        var individualSessions = await db.ScheduledTaskRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id) && r.TenantId == tenantId)
            .Select(r => new { r.Id, r.SessionId })
            .ToListAsync(ct);
        var groupSessions = await db.GroupScheduledTaskRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id) && r.TenantId == tenantId)
            .Select(r => new { r.Id, r.SessionId })
            .ToListAsync(ct);
        var sessionMap = individualSessions.Concat(groupSessions)
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First().SessionId);

        return items.Select(f =>
        {
            taskMap.TryGetValue(f.ScheduledTaskId, out var task);
            var agentDisplayName = task is not null && agentMap.TryGetValue(task.AgentId, out var ag)
                ? ag.DisplayName : null;
            sessionMap.TryGetValue(f.RunId, out var sessionId);
            return new SchedulerFeedbackDto(
                f.Id, f.TenantId, f.RunId, f.ScheduledTaskId, f.TaskType,
                task?.Name, agentDisplayName, sessionId,
                f.ThumbsRating, f.StarRating, f.Category, f.CorrectionText,
                f.SubmitterName, f.SubmitterEmail,
                f.Status, f.SubmittedAt, f.ReviewedAt, f.ReviewNotes);
        }).ToList();
    }

    public async Task ApproveAsync(string id, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var feedback = await db.SchedulerFeedbacks
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);

        if (feedback is null) return;

        feedback.Status = "approved";
        feedback.ReviewedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Activate rule learning when correction text is present
        if (!string.IsNullOrWhiteSpace(feedback.CorrectionText))
        {
            // Resolve sessionId and agent response text from the run record (best-effort)
            var sessionId = await ResolveSessionIdAsync(db, feedback, ct) ?? feedback.RunId;
            var responseText = await ResolveResponseTextAsync(db, feedback, ct)
                ?? $"Scheduled task run {feedback.RunId}";

            Exception? learningEx = null;
            try
            {
                await _learningService.ProcessCorrectionAsync(
                    tenantId,
                    sessionId,
                    originalResponse: responseText,
                    userCorrection: feedback.CorrectionText,
                    ct);
            }
            catch (Exception e) { learningEx = e; }

            if (learningEx is not null)
                _logger.LogWarning(learningEx,
                    "FeedbackLearningService failed for feedback '{FeedbackId}'.", id);
        }

        _logger.LogInformation("Scheduler feedback '{FeedbackId}' approved (tenant {TenantId}).", id, tenantId);
    }

    public async Task RejectAsync(string id, int tenantId, string? notes, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var feedback = await db.SchedulerFeedbacks
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);

        if (feedback is null) return;

        feedback.Status = "rejected";
        feedback.ReviewedAt = DateTime.UtcNow;
        feedback.ReviewNotes = notes;

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Scheduler feedback '{FeedbackId}' rejected (tenant {TenantId}).", id, tenantId);
    }

    // ── Private context loaders ───────────────────────────────────────────────

    private static async Task<SchedulerFeedbackContext?> LoadIndividualContextAsync(
        DivaDbContext db, FeedbackTokenClaims claims, CancellationToken ct)
    {
        var run = await db.ScheduledTaskRuns
            .AsNoTracking()
            .Where(r => r.Id == claims.RunId && r.TenantId == claims.TenantId)
            .FirstOrDefaultAsync(ct);

        if (run is null) return null;

        var task = await db.ScheduledTasks
            .AsNoTracking()
            .Where(t => t.Id == claims.TaskId && t.TenantId == claims.TenantId)
            .FirstOrDefaultAsync(ct);

        string? agentDisplayName = null;
        if (task is not null)
        {
            agentDisplayName = await db.AgentDefinitions
                .AsNoTracking()
                .Where(a => a.Id == task.AgentId)
                .Select(a => a.DisplayName)
                .FirstOrDefaultAsync(ct);
        }

        return new SchedulerFeedbackContext(
            TaskName: task?.Name ?? claims.TaskId,
            AgentDisplayName: agentDisplayName,
            RunId: run.Id,
            TaskId: claims.TaskId,
            SessionId: run.SessionId,
            TaskType: "individual",
            RunCompletedAt: run.CompletedAtUtc,
            RunOutcome: run.Status,
            RunSummary: run.ResponseText);
    }

    private static async Task<SchedulerFeedbackContext?> LoadGroupContextAsync(
        DivaDbContext db, FeedbackTokenClaims claims, CancellationToken ct)
    {
        var run = await db.GroupScheduledTaskRuns
            .AsNoTracking()
            .Where(r => r.Id == claims.RunId && r.TenantId == claims.TenantId)
            .FirstOrDefaultAsync(ct);

        if (run is null) return null;

        var task = await db.GroupScheduledTasks
            .AsNoTracking()
            .Where(t => t.Id == claims.TaskId)
            .FirstOrDefaultAsync(ct);

        // For group tasks, look up the agent via the group's agent type
        string? agentDisplayName = null;
        if (task is not null)
        {
            agentDisplayName = await db.AgentDefinitions
                .AsNoTracking()
                .Where(a => a.TenantId == claims.TenantId && a.AgentType == task.AgentType && a.IsEnabled)
                .Select(a => a.DisplayName)
                .FirstOrDefaultAsync(ct);
        }

        return new SchedulerFeedbackContext(
            TaskName: task?.Name ?? claims.TaskId,
            AgentDisplayName: agentDisplayName,
            RunId: run.Id,
            TaskId: claims.TaskId,
            SessionId: run.SessionId,
            TaskType: "group",
            RunCompletedAt: run.CompletedAtUtc,
            RunOutcome: run.Status,
            RunSummary: run.ResponseText);
    }

    private static async Task<string?> ResolveSessionIdAsync(
        DivaDbContext db, SchedulerFeedbackEntity feedback, CancellationToken ct)
    {
        if (feedback.TaskType == "group")
        {
            return await db.GroupScheduledTaskRuns
                .AsNoTracking()
                .Where(r => r.Id == feedback.RunId)
                .Select(r => r.SessionId)
                .FirstOrDefaultAsync(ct);
        }
        return await db.ScheduledTaskRuns
            .AsNoTracking()
            .Where(r => r.Id == feedback.RunId)
            .Select(r => r.SessionId)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<string?> ResolveResponseTextAsync(
        DivaDbContext db, SchedulerFeedbackEntity feedback, CancellationToken ct)
    {
        if (feedback.TaskType == "group")
        {
            return await db.GroupScheduledTaskRuns
                .AsNoTracking()
                .Where(r => r.Id == feedback.RunId)
                .Select(r => r.ResponseText)
                .FirstOrDefaultAsync(ct);
        }
        return await db.ScheduledTaskRuns
            .AsNoTracking()
            .Where(r => r.Id == feedback.RunId)
            .Select(r => r.ResponseText)
            .FirstOrDefaultAsync(ct);
    }
}
