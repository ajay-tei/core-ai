using Diva.Agents.Supervisor;
using Diva.Core.Models;
using Diva.Host.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

/// <summary>
/// Entry point for the supervisor pipeline.
/// Routes queries to the best-matched worker agent(s) using capability-based dispatch.
/// Use this endpoint for complex, multi-step, or capability-agnostic queries.
/// Use POST /api/agents/{id}/invoke for direct single-agent invocations.
/// </summary>
[ApiController]
[Route("api/supervisor")]
[RequireTenantAdmin]
public class SupervisorController : ControllerBase
{
    private readonly ISupervisorAgent _supervisor;
    private readonly ILogger<SupervisorController> _logger;

    public SupervisorController(ISupervisorAgent supervisor, ILogger<SupervisorController> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <summary>
    /// Invoke the supervisor pipeline with a natural language query.
    /// The supervisor decomposes the query, matches it to capable agents, executes them, and integrates the results.
    /// </summary>
    [HttpPost("invoke")]
    public async Task<IActionResult> Invoke([FromBody] SupervisorInvokeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
            return BadRequest(new { error = "Query is required." });

        // Use system tenant context for direct invocations (no auth middleware yet)
        var tenant = TenantContext.System(tenantId: req.TenantId ?? 1);

        var request = new AgentRequest
        {
            Query = req.Query,
            SessionId = req.SessionId,
            PreferredAgent = req.PreferredAgent,
            TriggerType = "api"
        };

        _logger.LogInformation("Supervisor invoke: tenant={TenantId}, preferred={Preferred}",
            tenant.TenantId, req.PreferredAgent ?? "auto");

        var result = await _supervisor.InvokeAsync(request, tenant, ct);
        return Ok(result);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────
public record SupervisorInvokeRequest(
    string Query,
    string? SessionId = null,
    string? PreferredAgent = null,
    int? TenantId = null);
