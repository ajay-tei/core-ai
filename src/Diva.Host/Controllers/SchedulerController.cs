using Diva.Core.Configuration;
using Diva.Core.Extensions;
using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Scheduler;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/schedules")]
[RequireTenantAdmin]
public class SchedulerController : ControllerBase
{
    private readonly IScheduledTaskService _service;
    private readonly ILogger<SchedulerController> _logger;
    private readonly IOptions<TaskSchedulerOptions> _schedulerOpts;
    private readonly ISchedulerManualDispatch _manualDispatch;
    private readonly IEntityDraftService _drafts;
    private readonly IPromotionLedgerService _ledger;
    private readonly IPromotableSnapshotSerializer _snapshotSerializer;
    private readonly IEnvironmentService _environments;

    public SchedulerController(
        IScheduledTaskService service,
        ILogger<SchedulerController> logger,
        IOptions<TaskSchedulerOptions> schedulerOpts,
        ISchedulerManualDispatch manualDispatch,
        IEntityDraftService drafts,
        IPromotionLedgerService ledger,
        IEnumerable<IPromotableSnapshotSerializer> snapshotSerializers,
        IEnvironmentService environments)
    {
        _service = service;
        _logger = logger;
        _schedulerOpts = schedulerOpts;
        _manualDispatch = manualDispatch;
        _drafts = drafts;
        _ledger = ledger;
        _snapshotSerializer = snapshotSerializers.First(s => s.ObjectType == "ScheduledTask");
        _environments = environments;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // Scheduled tasks are meant to be authored in the tenant's default environment and promoted
    // outward — editing a non-default-environment copy directly would let it drift from what was
    // actually promoted. Untagged (legacy) tasks remain editable everywhere.
    private async Task<bool> IsLockedForEditingAsync(ScheduledTaskEntity task, int tenantId, CancellationToken ct)
    {
        if (task.EnvironmentId is not { } envId) return false;
        var defaultEnv = await _environments.GetDefaultAsync(tenantId, ct);
        return defaultEnv is not null && envId != defaultEnv.Id;
    }

    private static IActionResult NonDefaultEnvironmentLocked() => new ObjectResult(new
    {
        error = "This scheduled task belongs to a non-default environment and cannot be edited directly. " +
                "Edit the version in the default environment and promote your changes here instead. " +
                "Template parameters and the run-as user can still be changed directly.",
    })
    { StatusCode = StatusCodes.Status403Forbidden };

    // ── GET /api/schedules?search=&page=1&pageSize=25 ────────────────────────
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] int? environmentId = null,
        CancellationToken ct = default)
    {
        var tasks = await _service.ListAsync(EffectiveTenantId(tenantId), ct);
        var filtered = tasks.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            filtered = filtered.Where(t =>
                t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (t.Description ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        if (environmentId is > 0)
            filtered = filtered.Where(t => t.EnvironmentId == environmentId || t.EnvironmentId == null);
        return Ok(filtered.ToPagedResult(page, pageSize));
    }

    // ── GET /api/schedules/{id} ─────────────────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(
        string id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var task = await _service.GetAsync(EffectiveTenantId(tenantId), id, ct);
        return task is null ? NotFound() : Ok(task);
    }

    // ── POST /api/schedules ─────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateScheduledTaskDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });
        var envId = HttpContext.TryGetTenantContext()?.EnvironmentId;

        Exception? ex = null;
        object? created = null;
        try
        {
            created = await _service.CreateAsync(EffectiveTenantId(tenantId), new CreateScheduledTaskRequest(
                dto.AgentId, dto.Name, dto.Description,
                dto.ScheduleType, dto.ScheduledAtUtc, dto.RunAtTime, dto.DayOfWeek,
                dto.TimeZoneId ?? "UTC",
                dto.PayloadType ?? "prompt",
                dto.PromptText, dto.ParametersJson,
                dto.IsEnabled,
                dto.NotifyEmails, dto.NotifyOn, dto.SuccessKeywords,
                dto.RunAsUserId, dto.RunAsUserEmail, dto.RunAsUserLabel,
                envId is > 0 ? envId : null), ct);
        }
        catch (ArgumentException e) { ex = e; }

        if (ex is ArgumentException argEx)
            return BadRequest(new { error = argEx.Message });

        return CreatedAtAction(nameof(Get), new { id = ((dynamic)created!).Id }, created);
    }

    // ── PUT /api/schedules/{id} ─────────────────────────────────────────────
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateScheduledTaskDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });
        var tid = EffectiveTenantId(tenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (await IsLockedForEditingAsync(existing, tid, ct)) return NonDefaultEnvironmentLocked();

        Exception? ex = null;
        object? updated = null;
        try
        {
            updated = await _service.UpdateAsync(tid, id, new UpdateScheduledTaskRequest(
                dto.AgentId, dto.Name, dto.Description,
                dto.ScheduleType, dto.ScheduledAtUtc, dto.RunAtTime, dto.DayOfWeek,
                dto.TimeZoneId, dto.PayloadType, dto.PromptText, dto.ParametersJson,
                dto.IsEnabled, dto.NotifyEmails, dto.NotifyOn, dto.SuccessKeywords,
                dto.RunAsUserId, dto.RunAsUserEmail, dto.RunAsUserLabel), ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { ex = e; }

        if (ex is ArgumentException argEx)
            return BadRequest(new { error = argEx.Message });

        // Direct saves bypass the Draft/Publish flow but must still be recorded in the version
        // ledger (Source="manual") — otherwise the environment's live-version pointer silently
        // goes stale relative to what's actually in the live row (mirrors AgentsController.Update).
        if (existing.LogicalId is { } logicalId && existing.EnvironmentId is { } environmentId)
        {
            var ctx = HttpContext.TryGetTenantContext();
            var snapshot = await _snapshotSerializer.SerializeAsync(tid, environmentId, logicalId, ct);
            if (snapshot is not null)
            {
                await _ledger.RecordVersionAsync(
                    tid, logicalId, "ScheduledTask", snapshot.Name, environmentId,
                    snapshot.SnapshotJson, "manual", null, ctx?.UserId, null, ct);
            }
        }

        return Ok(updated);
    }

    // ── PUT /api/schedules/{id}/runtime-overrides ───────────────────────────
    // Template parameters and the run-as-user identity are environment-specific execution knobs
    // (e.g. different sample data, or a different real user's credentials, per environment) —
    // unlike the rest of a scheduled task's config (timing, prompt, agent), which should stay
    // pinned to whatever was promoted. Deliberately NOT gated by IsLockedForEditingAsync, and
    // deliberately narrow (only these fields, set directly) so it can never be used as a backdoor
    // to edit anything else on a locked task. ScheduledTaskSnapshotSerializer preserves an existing
    // row's ParametersJson/RunAsUser* across re-promotion, so a value saved here survives the next
    // time this task is promoted/rolled back.
    [HttpPut("{id}/runtime-overrides")]
    public async Task<IActionResult> UpdateRuntimeOverrides(
        string id,
        [FromBody] UpdateScheduledTaskRuntimeOverridesDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });

        Exception? ex = null;
        object? updated = null;
        try
        {
            updated = await _service.UpdateRuntimeOverridesAsync(
                EffectiveTenantId(tenantId), id, dto.ParametersJson,
                dto.RunAsUserId, dto.RunAsUserEmail, dto.RunAsUserLabel, ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null) return StatusCode(500, new { error = ex.Message });
        return Ok(updated);
    }

    // ── PUT /api/schedules/{id}/draft — additive, does NOT touch the live row. ──────────────
    [HttpPut("{id}/draft")]
    public async Task<IActionResult> SaveDraft(
        string id, [FromBody] UpdateScheduledTaskDto dto, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });
        var tid = EffectiveTenantId(tenantId);
        var task = await _service.GetAsync(tid, id, ct);
        if (task is null) return NotFound();
        if (await IsLockedForEditingAsync(task, tid, ct)) return NonDefaultEnvironmentLocked();
        if (task.LogicalId is not { } logicalId || task.EnvironmentId is not { } environmentId)
            return BadRequest(new { error = "Scheduled task is missing environment/logical identity — cannot draft." });

        var ctx = HttpContext.TryGetTenantContext();
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        await _drafts.SaveDraftAsync(tid, "ScheduledTask", logicalId, environmentId, json, ctx?.UserId, ct);
        return Ok(new { message = "Draft saved." });
    }

    // ── GET /api/schedules/{id}/draft ────────────────────────────────────────────────────────
    [HttpGet("{id}/draft")]
    public async Task<IActionResult> GetDraft(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var task = await _service.GetAsync(tid, id, ct);
        if (task is null) return NotFound();
        if (task.LogicalId is not { } logicalId || task.EnvironmentId is not { } environmentId)
            return Ok(new { hasDraft = false });

        var draft = await _drafts.GetDraftAsync(tid, "ScheduledTask", logicalId, environmentId, ct);
        if (draft is null) return Ok(new { hasDraft = false });

        var dto = System.Text.Json.JsonSerializer.Deserialize<UpdateScheduledTaskDto>(draft.DraftJson);
        return Ok(new { hasDraft = true, draft = dto, updatedAt = draft.UpdatedAt, updatedBy = draft.UpdatedBy });
    }

    // ── DELETE /api/schedules/{id}/draft ─────────────────────────────────────────────────────
    [HttpDelete("{id}/draft")]
    public async Task<IActionResult> DiscardDraft(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var task = await _service.GetAsync(tid, id, ct);
        if (task is null) return NotFound();
        if (task.LogicalId is { } logicalId && task.EnvironmentId is { } environmentId)
            await _drafts.ClearDraftAsync(tid, "ScheduledTask", logicalId, environmentId, ct);
        return NoContent();
    }

    // ── POST /api/schedules/{id}/publish — applies the draft + records a ledger version. ────
    [HttpPost("{id}/publish")]
    public async Task<IActionResult> Publish(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var task = await _service.GetAsync(tid, id, ct);
        if (task is null) return NotFound();
        if (await IsLockedForEditingAsync(task, tid, ct)) return NonDefaultEnvironmentLocked();
        if (task.LogicalId is not { } logicalId || task.EnvironmentId is not { } environmentId)
            return BadRequest(new { error = "Scheduled task is missing environment/logical identity — cannot publish." });

        var draft = await _drafts.GetDraftAsync(tid, "ScheduledTask", logicalId, environmentId, ct);
        if (draft is null) return BadRequest(new { error = "No draft to publish." });

        var dto = System.Text.Json.JsonSerializer.Deserialize<UpdateScheduledTaskDto>(draft.DraftJson)
            ?? throw new InvalidOperationException("Invalid draft JSON.");

        object? updated;
        Exception? ex = null;
        try
        {
            updated = await _service.UpdateAsync(tid, id, new UpdateScheduledTaskRequest(
                dto.AgentId, dto.Name, dto.Description,
                dto.ScheduleType, dto.ScheduledAtUtc, dto.RunAtTime, dto.DayOfWeek,
                dto.TimeZoneId, dto.PayloadType, dto.PromptText, dto.ParametersJson,
                dto.IsEnabled, dto.NotifyEmails, dto.NotifyOn, dto.SuccessKeywords,
                dto.RunAsUserId, dto.RunAsUserEmail, dto.RunAsUserLabel), ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }

        var ctx = HttpContext.TryGetTenantContext();
        var snapshot = await _snapshotSerializer.SerializeAsync(tid, environmentId, logicalId, ct);
        if (snapshot is not null)
        {
            await _ledger.RecordVersionAsync(
                tid, logicalId, "ScheduledTask", snapshot.Name, environmentId,
                snapshot.SnapshotJson, "publish", null, ctx?.UserId, null, ct);
        }

        await _drafts.ClearDraftAsync(tid, "ScheduledTask", logicalId, environmentId, ct);
        return Ok(updated);
    }

    // ── DELETE /api/schedules/{id} ──────────────────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var existing = await _service.GetAsync(tid, id, ct);
        if (existing is null) return NotFound();
        if (await ActivelyDeployedBlockAsync(tid, existing.LogicalId, ct) is { } block) return block;

        Exception? ex = null;
        try { await _service.DeleteAsync(tid, id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "Delete failed for schedule '{Id}'.", id);
            return StatusCode(500, new { error = "Internal error deleting schedule." });
        }

        return NoContent();
    }

    // Promoted objects live independently per environment, but EnvironmentDeployments still
    // points to this row's version as "live" for whichever environment(s) recorded it — deleting
    // it out from under that pointer would break that environment's ability to run it.
    private async Task<IActionResult?> ActivelyDeployedBlockAsync(int tenantId, Guid? logicalId, CancellationToken ct)
    {
        if (logicalId is not { } lid) return null;
        var envIds = await _ledger.GetDeployedEnvironmentIdsAsync(tenantId, lid, ct);
        if (envIds.Count == 0) return null;

        var envs = await _environments.ListAsync(tenantId, ct);
        var names = envIds.Select(id => envs.FirstOrDefault(e => e.Id == id)?.DisplayName ?? $"#{id}");
        return new ObjectResult(new
        {
            error = $"This scheduled task is actively deployed to {string.Join(", ", names)} and cannot be deleted while deployed there.",
        })
        { StatusCode = StatusCodes.Status409Conflict };
    }

    // ── PATCH /api/schedules/{id}/enabled ──────────────────────────────────
    [HttpPatch("{id}/enabled")]
    public async Task<IActionResult> SetEnabled(
        string id,
        [FromBody] SetEnabledDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        Exception? ex = null;
        object? result = null;
        try { result = await _service.SetEnabledAsync(EffectiveTenantId(tenantId), id, dto.IsEnabled, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null) return StatusCode(500, new { error = ex.Message });
        return Ok(result);
    }

    // ── POST /api/schedules/{id}/trigger ───────────────────────────────────
    [HttpPost("{id}/trigger")]
    public async Task<IActionResult> TriggerNow(
        string id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        Exception? ex = null;
        ScheduledTaskRunEntity? run = null;
        try { run = await _service.TriggerNowAsync(EffectiveTenantId(tenantId), id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null) return StatusCode(500, new { error = ex.Message });

        // Dispatch immediately on this instance so the manual run executes here regardless of
        // whether this instance is the auto-poll leader (skipped runs are not dispatched).
        if (run is not null && run.Status != "skipped")
            _manualDispatch.RequestManualDispatch(id);

        return Ok(run);
    }

    // ── GET /api/schedules/{id}/runs?page=1&pageSize=25 ────────────────────
    [HttpGet("{id}/runs")]
    public async Task<IActionResult> RunHistory(
        string id,
        [FromQuery] int tenantId = 1,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        // Service caps at 200 rows; fetch that ceiling then paginate in memory on top of it.
        var runs = await _service.GetRunHistoryAsync(EffectiveTenantId(tenantId), id, 200, ct);
        return Ok(runs.ToPagedResult(page, pageSize));
    }

    // ── POST /api/schedules/import ─────────────────────────────────────────
    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] ScheduleImportRequest dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto?.Tasks is null) return BadRequest(new { error = "Request body is required." });

        var tid = EffectiveTenantId(tenantId);
        var envId = HttpContext.TryGetTenantContext()?.EnvironmentId;
        var existing = (await _service.ListAsync(tid, ct))
                           .Select(t => t.Name)
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0;
        var skippedNames = new List<string>();

        foreach (var task in dto.Tasks)
        {
            if (dto.SkipConflicts && existing.Contains(task.Name))
            {
                skippedNames.Add(task.Name);
                continue;
            }

            Exception? ex = null;
            try
            {
                await _service.CreateAsync(tid, new CreateScheduledTaskRequest(
                    task.AgentId, task.Name, task.Description,
                    task.ScheduleType, task.ScheduledAtUtc, task.RunAtTime, task.DayOfWeek,
                    task.TimeZoneId ?? "UTC", task.PayloadType ?? "prompt",
                    task.PromptText, task.ParametersJson, task.IsEnabled,
                    task.NotifyEmails, task.NotifyOn, task.SuccessKeywords,
                    task.RunAsUserId, task.RunAsUserEmail, task.RunAsUserLabel,
                    envId is > 0 ? envId : null), ct);
                created++;
            }
            catch (Exception e) { ex = e; }

            if (ex is not null)
                _logger.LogWarning(ex, "Import: skipping task '{Name}' due to error.", task.Name);
        }

        return Ok(new ScheduleImportResult(created, skippedNames.Count, skippedNames));
    }

    // ── GET /api/schedules/notification-settings ───────────────────────────────
    [HttpGet("notification-settings")]
    public async Task<IActionResult> GetNotificationSettings(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var settings = await _service.GetNotificationSettingsAsync(EffectiveTenantId(tenantId), ct);
        if (settings is null)
            return Ok(new { globalNotifyEmails = (string?)null, globalNotifyOn = (string?)null });
        return Ok(settings);
    }

    // ── PUT /api/schedules/notification-settings ──────────────────────────────
    [HttpPut("notification-settings")]
    public async Task<IActionResult> UpsertNotificationSettings(
        [FromBody] UpsertNotificationSettingsDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });
        await _service.UpsertNotificationSettingsAsync(
            EffectiveTenantId(tenantId), dto.GlobalNotifyEmails, dto.GlobalNotifyOn, ct);
        return NoContent();
    }

    // ── GET /api/schedules/feedback-settings ──────────────────────────────────
    [HttpGet("feedback-settings")]
    public async Task<IActionResult> GetFeedbackSettings(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var settings = await _service.GetFeedbackSettingsAsync(EffectiveTenantId(tenantId), ct);
        // Return current values, falling back to appsettings defaults
        return Ok(new
        {
            enableFeedbackLinks = settings?.EnableFeedbackLinks ?? _schedulerOpts.Value.EnableFeedbackLinks,
            feedbackLinkBaseUrl = settings?.FeedbackLinkBaseUrl ?? _schedulerOpts.Value.FeedbackLinkBaseUrl ?? "",
            expiryDays = settings?.ExpiryDays > 0 ? settings.ExpiryDays : _schedulerOpts.Value.FeedbackLinkExpiryDays,
        });
    }

    // ── PUT /api/schedules/feedback-settings ──────────────────────────────────
    [HttpPut("feedback-settings")]
    public async Task<IActionResult> UpsertFeedbackSettings(
        [FromBody] UpsertFeedbackSettingsDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto is null) return BadRequest(new { error = "Request body is required." });
        await _service.UpsertFeedbackSettingsAsync(
            EffectiveTenantId(tenantId), dto.EnableFeedbackLinks, dto.FeedbackLinkBaseUrl, dto.ExpiryDays, ct);
        return NoContent();
    }
}

// ── Request DTOs (inline, following AgentSummaryDto pattern) ───────────────

public sealed record CreateScheduledTaskDto(
    string AgentId,
    string Name,
    string? Description,
    string ScheduleType,
    DateTime? ScheduledAtUtc,
    string? RunAtTime,
    int? DayOfWeek,
    string? TimeZoneId,
    string? PayloadType,
    string PromptText,
    string? ParametersJson,
    bool IsEnabled = true,
    string? NotifyEmails = null,
    string? NotifyOn = null,
    string? SuccessKeywords = null,
    string? RunAsUserId = null,
    string? RunAsUserEmail = null,
    string? RunAsUserLabel = null);

public sealed record UpdateScheduledTaskDto(
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
    string? SuccessKeywords = null,
    string? RunAsUserId = null,
    string? RunAsUserEmail = null,
    string? RunAsUserLabel = null);

/// <summary>Body for PUT /api/schedules/{id}/runtime-overrides — the only fields still editable
/// directly on a scheduled task promoted to a non-default environment.</summary>
public sealed record UpdateScheduledTaskRuntimeOverridesDto(
    string? ParametersJson,
    string? RunAsUserId,
    string? RunAsUserEmail,
    string? RunAsUserLabel);

public sealed record UpsertNotificationSettingsDto(
    string? GlobalNotifyEmails,
    string? GlobalNotifyOn);

public sealed record UpsertFeedbackSettingsDto(
    bool EnableFeedbackLinks,
    string? FeedbackLinkBaseUrl,
    int ExpiryDays = 30);

public sealed record SetEnabledDto(bool IsEnabled);

public sealed record ScheduledTaskExport(
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
    string? SuccessKeywords = null,
    string? RunAsUserId = null,
    string? RunAsUserEmail = null,
    string? RunAsUserLabel = null);

public sealed record ScheduleImportRequest(
    List<ScheduledTaskExport> Tasks,
    bool SkipConflicts = true);

public sealed record ScheduleImportResult(
    int Created,
    int Skipped,
    List<string> SkippedNames);
