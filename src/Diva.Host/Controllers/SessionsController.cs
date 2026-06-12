using Diva.Core.Models;
using Diva.Core.Models.Session;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly SessionTraceDbContext _trace;
    private readonly AgentSessionService _sessions;
    private readonly ILogger<SessionsController> _logger;

    private static readonly JsonSerializerOptions _exportJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Regular users (role "user" / "viewer") may only see their own sessions.
    // Tenant admins and the master admin see all sessions in their tenant.
    private static bool IsRestrictedUser(TenantContext? ctx)
        => ctx is not null && !ctx.IsAdmin && !ctx.IsMasterAdmin;

    public SessionsController(
        SessionTraceDbContext trace,
        AgentSessionService sessions,
        ILogger<SessionsController> logger)
    {
        _trace = trace;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>GET /api/sessions — paginated session list with filtering.</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<SessionSummary>>> GetSessions(
        [FromQuery] int? tenantId,
        [FromQuery] string? agentId,
        [FromQuery] string? userId,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? q,
        [FromQuery] bool supervisorOnly = false,
        [FromQuery] bool hasErrors = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : tenantId;

        // Non-admin users (user / viewer) may only see their own sessions.
        if (IsRestrictedUser(ctx))
        {
            userId = ctx!.UserId;
            if (string.IsNullOrEmpty(userId))
                return Ok(new PagedResult<SessionSummary>
                {
                    Items = [],
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = 0,
                    TotalPages = 0,
                });
        }

        var query = _trace.TraceSessions.AsNoTracking();

        if (effectiveTenantId.HasValue)
            query = query.Where(s => s.TenantId == effectiveTenantId.Value);
        if (!string.IsNullOrEmpty(agentId))
            query = query.Where(s => s.AgentId == agentId);
        if (!string.IsNullOrEmpty(userId))
            query = query.Where(s => s.UserId == userId);
        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);
        if (from.HasValue)
            query = query.Where(s => s.CreatedAt >= from.Value);
        if (to.HasValue)
            query = query.Where(s => s.CreatedAt <= to.Value);
        if (supervisorOnly)
            query = query.Where(s => s.IsSupervisor);
        if (hasErrors)
            query = query.Where(s => s.Status == "failed");
        if (!string.IsNullOrEmpty(q))
            query = query.Where(s => s.AgentName.Contains(q) || s.SessionId.Contains(q));

        query = query.Where(s => s.Status != "deleted");

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionSummary
            {
                SessionId = s.SessionId,
                ParentSessionId = s.ParentSessionId,
                TenantId = s.TenantId,
                UserId = s.UserId,
                AgentId = s.AgentId,
                AgentName = s.AgentName,
                IsSupervisor = s.IsSupervisor,
                Status = s.Status,
                CreatedAt = s.CreatedAt,
                LastActivityAt = s.LastActivityAt,
                TotalTurns = s.TotalTurns,
                TotalIterations = s.TotalIterations,
                TotalToolCalls = s.TotalToolCalls,
                TotalDelegations = s.TotalDelegations,
                TotalInputTokens = s.TotalInputTokens,
                TotalOutputTokens = s.TotalOutputTokens,
            })
            .ToListAsync(ct);

        return Ok(new PagedResult<SessionSummary>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
        });
    }

    /// <summary>GET /api/sessions/{id} — session metadata + turn list.</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<SessionDetail>> GetSession(string id, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        var session = await _trace.TraceSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == id, ct);

        if (session is null || session.Status == "deleted")
            return NotFound();

        if (effectiveTenantId.HasValue && session.TenantId != effectiveTenantId.Value)
            return NotFound();

        if (IsRestrictedUser(ctx) && session.UserId != ctx!.UserId)
            return NotFound();

        var turns = await _trace.TraceSessionTurns
            .AsNoTracking()
            .Where(t => t.SessionId == id)
            .OrderBy(t => t.TurnNumber)
            .Select(t => new TurnSummary
            {
                TurnNumber = t.TurnNumber,
                UserMessagePreview = t.UserMessage.Length > 200 ? t.UserMessage.Substring(0, 200) : t.UserMessage,
                AssistantMessagePreview = t.AssistantMessage.Length > 200 ? t.AssistantMessage.Substring(0, 200) : t.AssistantMessage,
                UserMessage = t.UserMessage,
                AssistantMessage = t.AssistantMessage,
                TotalIterations = t.TotalIterations,
                TotalToolCalls = t.TotalToolCalls,
                ContinuationWindows = t.ContinuationWindows,
                VerificationMode = t.VerificationMode,
                VerificationPassed = t.VerificationPassed,
                ExecutionTimeMs = t.ExecutionTimeMs,
                ModelId = t.ModelId,
                Provider = t.Provider,
                TotalInputTokens = t.TotalInputTokens,
                TotalOutputTokens = t.TotalOutputTokens,
                CreatedAt = t.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(new SessionDetail
        {
            SessionId = session.SessionId,
            ParentSessionId = session.ParentSessionId,
            TenantId = session.TenantId,
            UserId = session.UserId,
            AgentId = session.AgentId,
            AgentName = session.AgentName,
            IsSupervisor = session.IsSupervisor,
            Status = session.Status,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.LastActivityAt,
            TotalTurns = session.TotalTurns,
            TotalIterations = session.TotalIterations,
            TotalToolCalls = session.TotalToolCalls,
            TotalDelegations = session.TotalDelegations,
            TotalInputTokens = session.TotalInputTokens,
            TotalOutputTokens = session.TotalOutputTokens,
            Turns = turns,
        });
    }

    /// <summary>GET /api/sessions/{id}/turns/{turnNumber}/iterations — full iteration detail for a turn.</summary>
    [HttpGet("{id}/turns/{turnNumber}/iterations")]
    public async Task<ActionResult<List<IterationDetail>>> GetTurnIterations(
        string id, int turnNumber, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        if (effectiveTenantId.HasValue)
        {
            var sessionTenant = await _trace.TraceSessions.AsNoTracking()
                .Where(s => s.SessionId == id)
                .Select(s => (int?)s.TenantId)
                .FirstOrDefaultAsync(ct);
            if (sessionTenant is null || sessionTenant != effectiveTenantId.Value)
                return NotFound();
        }

        if (IsRestrictedUser(ctx))
        {
            var ownerId = await _trace.TraceSessions.AsNoTracking()
                .Where(s => s.SessionId == id)
                .Select(s => s.UserId)
                .FirstOrDefaultAsync(ct);
            if (ownerId != ctx!.UserId)
                return NotFound();
        }

        var turn = await _trace.TraceSessionTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.SessionId == id && t.TurnNumber == turnNumber, ct);

        if (turn is null)
            return NotFound();

        var iterations = await _trace.TraceIterations
            .AsNoTracking()
            .Where(i => i.TurnId == turn.Id)
            .OrderBy(i => i.IterationNumber)
            .ToListAsync(ct);

        var iterIds = iterations.Select(i => i.Id).ToList();
        var toolCalls = await _trace.TraceToolCalls
            .AsNoTracking()
            .Where(tc => iterIds.Contains(tc.IterationId))
            .OrderBy(tc => tc.IterationId).ThenBy(tc => tc.Sequence)
            .ToListAsync(ct);

        // Resolve ChildSessionId for delegation tool calls via TraceDelegationChain
        var a2aTaskIds = toolCalls
            .Where(tc => tc.IsAgentDelegation && tc.LinkedA2ATaskId != null)
            .Select(tc => tc.LinkedA2ATaskId!)
            .Distinct()
            .ToList();

        var childSessionMap = new Dictionary<string, string>();
        if (a2aTaskIds.Count > 0)
        {
            var delegationRows = await _trace.TraceDelegationChain
                .AsNoTracking()
                .Where(d => a2aTaskIds.Contains(d.ChildA2ATaskId) && d.ChildSessionId != null)
                .Select(d => new { d.ChildA2ATaskId, d.ChildSessionId })
                .ToListAsync(ct);
            foreach (var row in delegationRows)
                childSessionMap[row.ChildA2ATaskId] = row.ChildSessionId!;
        }

        var toolsByIter = toolCalls.ToLookup(tc => tc.IterationId);

        var result = iterations.Select(iter => new IterationDetail
        {
            IterationNumber = iter.IterationNumber,
            ContinuationWindow = iter.ContinuationWindow,
            IsCorrection = iter.IsCorrection,
            ThinkingText = iter.ThinkingText,
            PlanText = iter.PlanText,
            ModelId = iter.ModelId,
            Provider = iter.Provider,
            HadModelSwitch = iter.HadModelSwitch,
            FromModel = iter.FromModel,
            ToModel = iter.ToModel,
            ModelSwitchReason = iter.ModelSwitchReason,
            InputTokens = iter.InputTokens,
            OutputTokens = iter.OutputTokens,
            CacheReadTokens = iter.CacheReadTokens,
            CacheCreationTokens = iter.CacheCreationTokens,
            ToolCalls = toolsByIter[iter.Id].Select(tc => new ToolCallDetail
            {
                Sequence = tc.Sequence,
                ToolName = tc.ToolName,
                ToolInput = tc.ToolInput,
                ToolOutput = tc.ToolOutput,
                IsAgentDelegation = tc.IsAgentDelegation,
                DelegatedAgentId = tc.DelegatedAgentId,
                DelegatedAgentName = tc.DelegatedAgentName,
                LinkedA2ATaskId = tc.LinkedA2ATaskId,
                ChildSessionId = tc.LinkedA2ATaskId != null
                    ? childSessionMap.GetValueOrDefault(tc.LinkedA2ATaskId)
                    : null,
            }).ToList(),
        }).ToList();

        return Ok(result);
    }

    /// <summary>GET /api/sessions/{id}/tree — full session hierarchy (supervisor → workers).</summary>
    [HttpGet("{id}/tree")]
    public async Task<ActionResult<List<SessionTreeNode>>> GetSessionTree(string id, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        var current = await _trace.TraceSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == id, ct);
        if (current is null) return NotFound();

        if (effectiveTenantId.HasValue && current.TenantId != effectiveTenantId.Value)
            return NotFound();

        if (IsRestrictedUser(ctx) && current.UserId != ctx!.UserId)
            return NotFound();

        // Walk up to the true root
        var rootId = id;
        while (current?.ParentSessionId != null)
        {
            current = await _trace.TraceSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == current.ParentSessionId, ct);
            if (current != null) rootId = current.SessionId;
        }

        var allSessions = await LoadSubtreeAsync(rootId, ct);
        return Ok(BuildTree(allSessions, rootId, id));
    }

    private async Task<List<TraceSessionEntity>> LoadSubtreeAsync(string rootId, CancellationToken ct)
    {
        var result = new List<TraceSessionEntity>();
        var queue = new Queue<string>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var children = await _trace.TraceSessions.AsNoTracking()
                .Where(s => s.SessionId == parentId || s.ParentSessionId == parentId)
                .ToListAsync(ct);
            foreach (var s in children)
            {
                if (!result.Any(r => r.SessionId == s.SessionId))
                {
                    result.Add(s);
                    if (s.SessionId != parentId)
                        queue.Enqueue(s.SessionId);
                }
            }
        }
        return result;
    }

    private static List<SessionTreeNode> BuildTree(List<TraceSessionEntity> all, string rootId, string currentId)
    {
        var root = all.FirstOrDefault(s => s.SessionId == rootId);
        if (root is null) return [];
        return [BuildNode(root, all, currentId)];
    }

    private static SessionTreeNode BuildNode(TraceSessionEntity s, List<TraceSessionEntity> all, string currentId) =>
        new()
        {
            SessionId = s.SessionId,
            AgentName = s.AgentName,
            IsSupervisor = s.IsSupervisor,
            Status = s.Status,
            TotalTurns = s.TotalTurns,
            TotalIterations = s.TotalIterations,
            TotalToolCalls = s.TotalToolCalls,
            IsCurrentSession = s.SessionId == currentId,
            Children = all
                .Where(c => c.ParentSessionId == s.SessionId)
                .Select(c => BuildNode(c, all, currentId))
                .ToList(),
        };

    /// <summary>GET /api/sessions/{id}/export — full trace JSON export.</summary>
    [HttpGet("{id}/export")]
    public async Task<IActionResult> ExportSession(string id, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        var session = await _trace.TraceSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == id, ct);
        if (session is null) return NotFound();

        if (effectiveTenantId.HasValue && session.TenantId != effectiveTenantId.Value)
            return NotFound();

        if (IsRestrictedUser(ctx) && session.UserId != ctx!.UserId)
            return NotFound();

        var turns = await _trace.TraceSessionTurns.AsNoTracking()
            .Where(t => t.SessionId == id).ToListAsync(ct);
        var iterations = await _trace.TraceIterations.AsNoTracking()
            .Where(i => i.SessionId == id).ToListAsync(ct);
        var toolCalls = await _trace.TraceToolCalls.AsNoTracking()
            .Where(tc => tc.SessionId == id).ToListAsync(ct);
        var delegations = await _trace.TraceDelegationChain.AsNoTracking()
            .Where(d => d.CallerSessionId == id).ToListAsync(ct);

        var export = new
        {
            session,
            turns,
            iterations,
            toolCalls,
            delegations,
            exportedAt = DateTime.UtcNow,
        };

        var json = JsonSerializer.Serialize(export, _exportJsonOpts);
        return File(System.Text.Encoding.UTF8.GetBytes(json),
            "application/json", $"session-{id[..8]}.json");
    }

    /// <summary>
    /// POST /api/sessions/{id}/continue — reactivate a stored session so it can be
    /// resumed in Agent Test. Returns the agent + turn metadata needed to open chat
    /// and append new turns to the same session.
    /// </summary>
    [HttpPost("{id}/continue")]
    public async Task<ActionResult<ContinueSessionResult>> ContinueSession(string id, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        var session = await _trace.TraceSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == id, ct);

        if (session is null || session.Status == "deleted")
            return NotFound();

        // Tenant isolation: non-master users may only continue their own tenant's sessions.
        if (effectiveTenantId.HasValue && session.TenantId != effectiveTenantId.Value)
            return NotFound();

        if (IsRestrictedUser(ctx) && session.UserId != ctx!.UserId)
            return NotFound();

        var tenant = ctx is { TenantId: > 0 } ? ctx : TenantContext.System(session.TenantId);
        var reactivated = await _sessions.ReactivateAsync(id, tenant, ct);

        if (!reactivated)
            _logger.LogInformation(
                "ContinueSession: conversation memory for {SessionId} not found; chat will start a fresh session",
                id);

        return Ok(new ContinueSessionResult
        {
            SessionId = session.SessionId,
            AgentId = session.AgentId,
            AgentName = session.AgentName,
            TurnCount = session.TotalTurns,
            Reactivated = reactivated,
        });
    }

    /// <summary>DELETE /api/sessions/{id} — soft-delete a session.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSession(string id, CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        var effectiveTenantId = ctx is { TenantId: > 0 } ? ctx.TenantId : (int?)null;

        var session = await _trace.TraceSessions
            .FirstOrDefaultAsync(s => s.SessionId == id, ct);
        if (session is null) return NotFound();

        if (effectiveTenantId.HasValue && session.TenantId != effectiveTenantId.Value)
            return NotFound();

        if (IsRestrictedUser(ctx) && session.UserId != ctx!.UserId)
            return NotFound();

        session.Status = "deleted";
        await _trace.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// DELETE /api/sessions/purge?olderThanDays=N&amp;status=completed
    /// Hard-deletes sessions (+ cascade child rows) older than the given number of days.
    /// Defaults to sessions older than 30 days. Optionally filter by status.
    /// Returns { deleted: N }.
    /// </summary>
    [HttpDelete("purge")]
    [RequireTenantAdmin]
    public async Task<IActionResult> PurgeSessions(
        [FromQuery] int olderThanDays = 30,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (olderThanDays < 1)
            return BadRequest(new { error = "olderThanDays must be >= 1" });

        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);

        var query = _trace.TraceSessions.Where(s => s.LastActivityAt < cutoff);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(s => s.Status == status);

        var deleted = await query.ExecuteDeleteAsync(ct);

        _logger.LogInformation("Session trace purge: deleted {Count} sessions older than {Days} days (cutoff={Cutoff:u})",
            deleted, olderThanDays, cutoff);

        return Ok(new { deleted });
    }
}
