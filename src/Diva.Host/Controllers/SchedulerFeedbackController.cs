using Diva.Core.Configuration;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Scheduler;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Diva.Host.Controllers;

/// <summary>
/// Public + admin endpoints for scheduler run feedback.
///
/// Public (no auth) — protected by rate limiting + HMAC token validation:
///   GET  /api/scheduler-feedback/context?token=...    — load run context for the feedback form
///   POST /api/scheduler-feedback/submit               — submit feedback
///
/// Admin (Bearer/API-Key auth required):
///   GET  /api/scheduler-feedback?tenantId=1           — list pending items for review
///   PUT  /api/scheduler-feedback/{id}/approve         — approve (triggers rule learning if correction present)
///   PUT  /api/scheduler-feedback/{id}/reject          — reject with optional notes
/// </summary>
[ApiController]
[Route("api/scheduler-feedback")]
public class SchedulerFeedbackController : ControllerBase
{
    private readonly ISchedulerFeedbackService _feedbackSvc;
    private readonly ISchedulerFeedbackTokenService _tokenSvc;
    private readonly IOptions<TaskSchedulerOptions> _opts;
    private readonly IScheduledTaskService _schedulerSvc;
    private readonly ILogger<SchedulerFeedbackController> _logger;

    public SchedulerFeedbackController(
        ISchedulerFeedbackService feedbackSvc,
        ISchedulerFeedbackTokenService tokenSvc,
        IOptions<TaskSchedulerOptions> opts,
        IScheduledTaskService schedulerSvc,
        ILogger<SchedulerFeedbackController> logger)
    {
        _feedbackSvc = feedbackSvc;
        _tokenSvc = tokenSvc;
        _opts = opts;
        _schedulerSvc = schedulerSvc;
        _logger = logger;
    }

    // ── Helper: enforce tenant scoping after auth ─────────────────────────────
    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── Public: load context for the feedback form ────────────────────────────

    /// <summary>
    /// GET /api/scheduler-feedback/context?token=...
    /// Validates the HMAC token and returns run/task context for the feedback form.
    /// No authentication required — token is the proof of origin.
    /// </summary>
    [HttpGet("context")]
    [EnableRateLimiting("scheduler_feedback")]
    public async Task<IActionResult> GetContext(
        [FromQuery] string token,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(new { error = "Token is required." });

        SchedulerFeedbackContext? context = null;
        Exception? ex = null;
        try { context = await _feedbackSvc.GetContextByTokenAsync(token, ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogWarning(ex, "GetContext failed for scheduler feedback token.");
            return BadRequest(new { error = "Invalid or expired token." });
        }

        if (context is null)
            return NotFound(new { error = "Run context not found or token expired." });

        return Ok(context);
    }

    // ── Public: submit feedback ───────────────────────────────────────────────

    /// <summary>
    /// POST /api/scheduler-feedback/submit
    /// Validates the HMAC token and persists the feedback submission.
    /// No authentication required — token is the proof of origin.
    /// </summary>
    [HttpPost("submit")]
    [EnableRateLimiting("scheduler_feedback")]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitFeedbackRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            return BadRequest(new { error = "Token is required." });

        // Validate at least one rating field is provided
        if (request.ThumbsRating is null && request.StarRating is null
            && string.IsNullOrWhiteSpace(request.CorrectionText))
            return BadRequest(new { error = "At least one feedback field (thumbs, stars, or correction) is required." });

        Exception? ex = null;
        try
        {
            await _feedbackSvc.SubmitAsync(
                new SubmitSchedulerFeedbackRequest(
                    request.Token,
                    request.ThumbsRating,
                    request.StarRating,
                    request.Category?.Trim(),
                    request.CorrectionText?.Trim(),
                    request.SubmitterName?.Trim(),
                    request.SubmitterEmail?.Trim()),
                ct);
        }
        catch (ArgumentException ae)
        {
            return BadRequest(new { error = ae.Message });
        }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "Submit failed for scheduler feedback.");
            return StatusCode(500, new { error = "An error occurred while saving feedback." });
        }

        return Ok(new { message = "Thank you — your feedback has been submitted." });
    }

    // ── Admin: generate feedback link for a specific run ──────────────────────

    /// <summary>
    /// GET /api/scheduler-feedback/generate-link?runId=...&amp;taskId=...&amp;tenantId=1&amp;taskType=individual
    /// Generates a signed feedback URL for an existing run.
    /// Requires authentication so only admins/portal users can create links.
    /// </summary>
    [HttpGet("generate-link")]
    public async Task<IActionResult> GenerateLink(
        [FromQuery] string runId,
        [FromQuery] string taskId,
        [FromQuery] int tenantId = 1,
        [FromQuery] string taskType = "individual",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(taskId))
            return BadRequest(new { error = "runId and taskId are required." });

        var tid = EffectiveTenantId(tenantId);

        // DB settings take precedence over appsettings
        var dbSettings = await _schedulerSvc.GetFeedbackSettingsAsync(tid, ct);
        var enabled = dbSettings?.EnableFeedbackLinks ?? _opts.Value.EnableFeedbackLinks;
        var baseUrl = (!string.IsNullOrWhiteSpace(dbSettings?.FeedbackLinkBaseUrl)
                           ? dbSettings.FeedbackLinkBaseUrl
                           : _opts.Value.FeedbackLinkBaseUrl) ?? "";

        if (!enabled || string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { error = "Feedback links are not configured. Set the portal base URL in Schedules → Feedback Settings." });

        var token = _tokenSvc.Generate(runId, taskId, tid, taskType);
        var url = $"{baseUrl.TrimEnd('/')}/scheduler-feedback?token={Uri.EscapeDataString(token)}";
        return Ok(new { url });
    }

    // ── Admin: list pending ───────────────────────────────────────────────────

    /// <summary>
    /// GET /api/scheduler-feedback?tenantId=1
    /// Returns pending feedback items for admin review.
    /// Requires authentication.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPending(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var items = await _feedbackSvc.GetPendingAsync(tid, ct);
        return Ok(items);
    }

    // ── Admin: approve ────────────────────────────────────────────────────────

    /// <summary>
    /// PUT /api/scheduler-feedback/{id}/approve
    /// Approves feedback and triggers rule learning if CorrectionText is set.
    /// </summary>
    [HttpPut("{id}/approve")]
    public async Task<IActionResult> Approve(
        string id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        Exception? ex = null;
        try { await _feedbackSvc.ApproveAsync(id, EffectiveTenantId(tenantId), ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "ApproveAsync failed for feedback '{Id}'.", id);
            return StatusCode(500, new { error = "Approval failed." });
        }

        return Ok(new { message = "Feedback approved." });
    }

    // ── Admin: reject ─────────────────────────────────────────────────────────

    /// <summary>
    /// PUT /api/scheduler-feedback/{id}/reject
    /// Rejects feedback with optional admin notes.
    /// </summary>
    [HttpPut("{id}/reject")]
    public async Task<IActionResult> Reject(
        string id,
        [FromBody] RejectFeedbackRequest request,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        Exception? ex = null;
        try { await _feedbackSvc.RejectAsync(id, EffectiveTenantId(tenantId), request.Notes, ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "RejectAsync failed for feedback '{Id}'.", id);
            return StatusCode(500, new { error = "Rejection failed." });
        }

        return Ok(new { message = "Feedback rejected." });
    }
}

// ── Request DTOs ─────────────────────────────────────────────────────────────

public sealed class SubmitFeedbackRequest
{
    public string Token { get; set; } = string.Empty;
    public int? ThumbsRating { get; set; }
    public int? StarRating { get; set; }
    public string? Category { get; set; }
    public string? CorrectionText { get; set; }
    public string? SubmitterName { get; set; }
    public string? SubmitterEmail { get; set; }
}

public sealed class RejectFeedbackRequest
{
    public string? Notes { get; set; }
}
