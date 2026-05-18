using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Scheduler;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/schedules")]
public class SchedulerController : ControllerBase
{
    private readonly IScheduledTaskService _service;
    private readonly ILogger<SchedulerController> _logger;

    public SchedulerController(
        IScheduledTaskService service,
        ILogger<SchedulerController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── GET /api/schedules ──────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _service.ListAsync(EffectiveTenantId(tenantId), ct));

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
                dto.NotifyEmails, dto.NotifyOn, dto.SuccessKeywords), ct);
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

        Exception? ex = null;
        object? updated = null;
        try
        {
            updated = await _service.UpdateAsync(EffectiveTenantId(tenantId), id, new UpdateScheduledTaskRequest(
                dto.AgentId, dto.Name, dto.Description,
                dto.ScheduleType, dto.ScheduledAtUtc, dto.RunAtTime, dto.DayOfWeek,
                dto.TimeZoneId, dto.PayloadType, dto.PromptText, dto.ParametersJson,
                dto.IsEnabled, dto.NotifyEmails, dto.NotifyOn, dto.SuccessKeywords), ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { ex = e; }

        if (ex is ArgumentException argEx)
            return BadRequest(new { error = argEx.Message });

        return Ok(updated);
    }

    // ── DELETE /api/schedules/{id} ──────────────────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(
        string id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        Exception? ex = null;
        try { await _service.DeleteAsync(EffectiveTenantId(tenantId), id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
        {
            _logger.LogError(ex, "Delete failed for schedule '{Id}'.", id);
            return StatusCode(500, new { error = "Internal error deleting schedule." });
        }

        return NoContent();
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
        object? run = null;
        try { run = await _service.TriggerNowAsync(EffectiveTenantId(tenantId), id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception e) { ex = e; }

        if (ex is not null) return StatusCode(500, new { error = ex.Message });
        return Ok(run);
    }

    // ── GET /api/schedules/{id}/runs ───────────────────────────────────────
    [HttpGet("{id}/runs")]
    public async Task<IActionResult> RunHistory(
        string id,
        [FromQuery] int tenantId = 1,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
        => Ok(await _service.GetRunHistoryAsync(EffectiveTenantId(tenantId), id, Math.Clamp(limit, 1, 200), ct));

    // ── POST /api/schedules/import ─────────────────────────────────────────
    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] ScheduleImportRequest dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (dto?.Tasks is null) return BadRequest(new { error = "Request body is required." });

        var tid = EffectiveTenantId(tenantId);
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
                    task.NotifyEmails, task.NotifyOn, task.SuccessKeywords), ct);
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
    string? SuccessKeywords = null);

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
    string? SuccessKeywords = null);

public sealed record UpsertNotificationSettingsDto(
    string? GlobalNotifyEmails,
    string? GlobalNotifyOn);

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
    string? SuccessKeywords = null);

public sealed record ScheduleImportRequest(
    List<ScheduledTaskExport> Tasks,
    bool SkipConflicts = true);

public sealed record ScheduleImportResult(
    int Created,
    int Skipped,
    List<string> SkippedNames);
