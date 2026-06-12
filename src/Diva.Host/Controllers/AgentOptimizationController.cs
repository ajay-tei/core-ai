using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Optimization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/admin")]
[RequireTenantAdmin]
public class AgentOptimizationController : ControllerBase
{
    private readonly IAgentOptimizationService _service;
    private readonly ILogger<AgentOptimizationController> _logger;

    public AgentOptimizationController(
        IAgentOptimizationService service,
        ILogger<AgentOptimizationController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    private string CurrentUser()
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx?.UserId ?? ctx?.UserEmail ?? "unknown";
    }

    // ── Optimization Runs ─────────────────────────────────────────────────────

    [HttpPost("agents/{agentId}/optimize")]
    public async Task<IActionResult> StartRun(
        string agentId,
        [FromBody] TriggerOptimizationRequest request,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var tid = EffectiveTenantId(tenantId);
            var runId = await _service.StartRunAsync(agentId, tid, request, CurrentUser(), ct);
            return Ok(new { runId });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
        {
            var existingRunId = _service.GetActiveRunId(agentId, EffectiveTenantId(tenantId));
            return Conflict(new { error = ex.Message, runId = existingRunId });
        }
    }

    [HttpGet("agents/{agentId}/optimize/runs")]
    public async Task<IActionResult> GetRuns(
        string agentId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetRunsAsync(agentId, EffectiveTenantId(tenantId), ct));

    [HttpGet("agents/{agentId}/optimize/runs/{runId:int}")]
    public async Task<IActionResult> GetRunDetail(
        string agentId, int runId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var detail = await _service.GetRunDetailAsync(runId, EffectiveTenantId(tenantId), ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    // ── Suggestions ───────────────────────────────────────────────────────────

    [HttpGet("agents/{agentId}/optimize/suggestions")]
    public async Task<IActionResult> GetSuggestions(
        string agentId,
        [FromQuery] string? status = null,
        [FromQuery] string? type = null,
        [FromQuery] int? runId = null,
        [FromQuery] float minConfidence = 0f,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetSuggestionsAsync(
            agentId, EffectiveTenantId(tenantId), status, type, runId, minConfidence, ct));

    [HttpPost("agents/{agentId}/optimize/suggestions/{id:int}/approve")]
    public async Task<IActionResult> ApproveSuggestion(
        string agentId, int id,
        [FromBody] ReviewSuggestionRequest? body = null,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _service.ApproveSuggestionAsync(id, EffectiveTenantId(tenantId), CurrentUser(), body?.Notes, ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("agents/{agentId}/optimize/suggestions/{id:int}/reject")]
    public async Task<IActionResult> RejectSuggestion(
        string agentId, int id,
        [FromBody] ReviewSuggestionRequest? body = null,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _service.RejectSuggestionAsync(id, EffectiveTenantId(tenantId), CurrentUser(), body?.Notes, ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("agents/{agentId}/optimize/suggestions/{id:int}/apply")]
    public async Task<IActionResult> ApplySuggestion(
        string agentId, int id,
        [FromBody] ApplySuggestionRequest? body = null,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _service.ApplySuggestionAsync(id, EffectiveTenantId(tenantId), body?.ApplyMode ?? "append", ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("agents/{agentId}/optimize/suggestions/merge-prompt")]
    public async Task<IActionResult> MergePrompt(
        string agentId,
        [FromBody] MergePromptRequest body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var merged = await _service.MergePromptAsync(
                agentId, EffectiveTenantId(tenantId), body.SuggestionIds, ct);
            return Ok(new { mergedPrompt = merged });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("agents/{agentId}/optimize/suggestions/apply-merged")]
    public async Task<IActionResult> ApplyMerged(
        string agentId,
        [FromBody] ApplyMergedRequest body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _service.ApplyMergedAsync(
                agentId, EffectiveTenantId(tenantId), body.MergedPrompt, body.SuggestionIds, ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Schedule ──────────────────────────────────────────────────────────────

    [HttpGet("agents/{agentId}/optimize/schedule")]
    public async Task<IActionResult> GetSchedule(
        string agentId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var config = await _service.GetScheduleAsync(agentId, EffectiveTenantId(tenantId), ct);
        return config is null
            ? Ok(new OptimizationScheduleConfig { ScheduleType = "manual" })
            : Ok(config);
    }

    [HttpPut("agents/{agentId}/optimize/schedule")]
    public async Task<IActionResult> SaveSchedule(
        string agentId,
        [FromBody] OptimizationScheduleConfig config,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _service.SaveScheduleAsync(agentId, EffectiveTenantId(tenantId), config, ct);
        return Ok();
    }

    // ── Per-session analysis ──────────────────────────────────────────────────

    [HttpGet("sessions/{sessionId}/optimize/runs")]
    public async Task<IActionResult> GetRunsBySession(
        string sessionId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetRunsBySessionAsync(sessionId, EffectiveTenantId(tenantId), ct));

    [HttpPost("sessions/{sessionId}/optimize")]
    public async Task<IActionResult> StartSessionRun(
        string sessionId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        string? resolvedAgentId = null;
        try
        {
            // Resolve the agentId from the trace session (AgentSessionEntity stores type, not agent ID)
            await using var scope = HttpContext.RequestServices.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
            var traceDb = scope.ServiceProvider.GetRequiredService<SessionTraceDbContext>();

            var session = await db.Sessions
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tid, ct);
            if (session is null) return NotFound(new { error = "Session not found" });

            var traceSession = await traceDb.TraceSessions
                .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
            resolvedAgentId = traceSession?.AgentId
                ?? throw new InvalidOperationException("Session trace not found — cannot determine agent ID");

            var request = new TriggerOptimizationRequest { SessionId = sessionId };
            var runId = await _service.StartRunAsync(resolvedAgentId, tid, request, CurrentUser(), ct);
            return Ok(new { runId, agentId = resolvedAgentId });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
        {
            var existingRunId = resolvedAgentId is not null
                ? _service.GetActiveRunId(resolvedAgentId, tid)
                : null;
            return Conflict(new { error = ex.Message, runId = existingRunId, agentId = resolvedAgentId });
        }
    }

    // ── Few-shot Examples ─────────────────────────────────────────────────────

    [HttpGet("agents/{agentId}/examples")]
    public async Task<IActionResult> GetExamples(
        string agentId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _service.GetFewShotExamplesAsync(agentId, EffectiveTenantId(tenantId), ct));

    [HttpPost("agents/{agentId}/examples")]
    public async Task<IActionResult> AddExample(
        string agentId,
        [FromBody] FewShotExampleDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var id = await _service.AddFewShotExampleAsync(agentId, EffectiveTenantId(tenantId), dto, ct);
        return Ok(new { id });
    }

    [HttpDelete("agents/{agentId}/examples/{id:int}")]
    public async Task<IActionResult> DeleteExample(
        string agentId, int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _service.DeleteFewShotExampleAsync(id, EffectiveTenantId(tenantId), ct);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("agents/{agentId}/examples/reorder")]
    public async Task<IActionResult> ReorderExamples(
        string agentId,
        [FromBody] ReorderFewShotExamplesRequest body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _service.ReorderFewShotExamplesAsync(agentId, EffectiveTenantId(tenantId), body.OrderedIds, ct);
        return Ok();
    }

    [HttpPost("sessions/{sessionId}/turns/{turnNumber:int}/examples")]
    public async Task<IActionResult> MarkTurnAsExample(
        string sessionId, int turnNumber,
        [FromBody] MarkTurnAsExampleRequest? body = null,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);

        await using var scope = HttpContext.RequestServices.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var traceDb = scope.ServiceProvider.GetRequiredService<Diva.Infrastructure.Data.SessionTraceDbContext>();

        var session = await db.Sessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TenantId == tid, ct);
        if (session is null) return NotFound(new { error = "Session not found" });

        var traceSession = await traceDb.TraceSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        var agentId = traceSession?.AgentId ?? "";

        var traceTurn = await traceDb.TraceSessionTurns
            .FirstOrDefaultAsync(t => t.SessionId == sessionId && t.TurnNumber == turnNumber, ct);
        if (traceTurn is null) return NotFound(new { error = "Turn not found in trace" });

        var dto = new FewShotExampleDto
        {
            AgentId = agentId,
            SourceSessionId = sessionId,
            SourceTurnNumber = turnNumber,
            UserMessage = traceTurn.UserMessage,
            AssistantMessage = traceTurn.AssistantMessage,
            Description = body?.Description,
            IsEnabled = true,
            CreatedBy = CurrentUser()
        };

        var id = await _service.AddFewShotExampleAsync(agentId, tid, dto, ct);
        return Ok(new { id });
    }
}
