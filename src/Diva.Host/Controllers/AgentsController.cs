using System.Text.Json;
using System.Text.Json.Serialization;
using Diva.Agents.Registry;
using Diva.Agents.Workers;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.AgentExport;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Optimization;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/agents")]
public class AgentsController : ControllerBase
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IAgentRunner _runner;
    private readonly ITenantGroupService _groups;
    private readonly IGroupAgentOverlayService _overlays;
    private readonly IArchetypeRegistry _archetypes;
    private readonly IAgentRegistry _registry;
    private readonly IAgentSetupAssistant _assistant;
    private readonly IOptimizationLlmAnalyzer _promptImprover;
    private readonly IAgentExportService _agentExport;
    private readonly ICredentialResolver? _credentialResolver;
    private readonly ILogger<AgentsController> _logger;

    public AgentsController(
        IDatabaseProviderFactory db,
        IAgentRunner runner,
        ITenantGroupService groups,
        IGroupAgentOverlayService overlays,
        IArchetypeRegistry archetypes,
        IAgentRegistry registry,
        IAgentSetupAssistant assistant,
        IOptimizationLlmAnalyzer promptImprover,
        IAgentExportService agentExport,
        ILogger<AgentsController> logger,
        ICredentialResolver? credentialResolver = null)
    {
        _db = db;
        _runner = runner;
        _groups = groups;
        _overlays = overlays;
        _archetypes = archetypes;
        _registry = registry;
        _assistant = assistant;
        _promptImprover = promptImprover;
        _agentExport = agentExport;
        _credentialResolver = credentialResolver;
        _logger = logger;
    }

    private TenantContext Tenant =>
        HttpContext.TryGetTenantContext() ?? TenantContext.System(tenantId: 1);

    // ── GET /api/agents ───────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var tenant = Tenant;
        using var db = _db.CreateDbContext(tenant);
        var ownAgents = await db.AgentDefinitions
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AgentSummaryDto(a.Id, a.Name, a.DisplayName, a.AgentType, a.Status, a.IsEnabled, a.CreatedAt, false, null, null, a.LlmConfigId))
            .ToListAsync(ct);

        // Merge shared group templates (read-only — the tenant cannot edit these)
        var groupTemplates = await _groups.GetAgentTemplatesForTenantAsync(tenant.TenantId, ct);
        var overlayMap = (await _overlays.GetOverlaysAsync(tenant.TenantId, ct))
            .ToDictionary(o => o.GroupTemplateId);
        var sharedSummaries = groupTemplates
            .Where(t => ownAgents.All(own => own.Id != t.Id))  // don't duplicate if tenant has own copy
            .Select(t =>
            {
                overlayMap.TryGetValue(t.Id, out var ov);
                return new AgentSummaryDto(t.Id, t.Name, t.DisplayName, t.AgentType, t.Status, t.IsEnabled,
                    t.CreatedAt, true, t.GroupId, t.Group?.Name, t.LlmConfigId,
                    IsActivated: ov?.IsEnabled ?? false, OverlayGuid: ov?.Guid);
            });

        return Ok(ownAgents.Concat(sharedSummaries));
    }

    // ── GET /api/agents/{id} ──────────────────────────────────────────────────
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var tenant = Tenant;
        using var db = _db.CreateDbContext(tenant);
        var agent = await db.AgentDefinitions.FindAsync([id], ct);
        if (agent is not null) return Ok(agent);

        // Fall through to group templates
        var groupTemplates = await _groups.GetAgentTemplatesForTenantAsync(tenant.TenantId, ct);
        var groupTemplate = groupTemplates.FirstOrDefault(t => t.Id == id);
        if (groupTemplate is null) return NotFound();
        return Ok(MapToDefinition(groupTemplate, tenant.TenantId));
    }

    // ── POST /api/agents ──────────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgentDefinitionEntity dto, CancellationToken ct)
    {
        var tenant = Tenant;
        dto.Id = Guid.NewGuid().ToString();
        dto.TenantId = tenant.TenantId;
        dto.CreatedAt = DateTime.UtcNow;
        dto.Status = "Draft";

        using var db = _db.CreateDbContext(tenant);
        db.AgentDefinitions.Add(dto);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    // ── PUT /api/agents/{id} ──────────────────────────────────────────────────
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] AgentDefinitionEntity dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(Tenant);
        var existing = await db.AgentDefinitions.FindAsync([id], ct);
        if (existing is null) return NotFound();

        existing.Name = dto.Name;
        existing.DisplayName = dto.DisplayName;
        existing.Description = dto.Description;
        existing.SystemPrompt = dto.SystemPrompt;
        existing.Temperature = dto.Temperature;
        existing.MaxIterations = dto.MaxIterations;
        existing.Capabilities = dto.Capabilities;
        existing.ToolBindings = dto.ToolBindings;
        existing.VerificationMode = dto.VerificationMode;
        existing.ContextWindowJson = dto.ContextWindowJson;
        existing.OptimizationOverrideJson = dto.OptimizationOverrideJson;
        existing.CustomVariablesJson = dto.CustomVariablesJson;
        existing.MaxContinuations = dto.MaxContinuations;
        existing.MaxToolResultChars = dto.MaxToolResultChars;
        existing.MaxOutputTokens = dto.MaxOutputTokens;
        existing.EnableHistoryCaching = dto.EnableHistoryCaching;
        existing.PipelineStagesJson = dto.PipelineStagesJson;
        existing.ToolFilterJson = dto.ToolFilterJson;
        existing.StageInstructionsJson = dto.StageInstructionsJson;
        existing.ArchetypeId = dto.ArchetypeId;
        existing.HooksJson = dto.HooksJson;
        existing.A2AEndpoint = dto.A2AEndpoint;
        existing.A2AAuthScheme = dto.A2AAuthScheme;
        existing.A2ASecretRef = dto.A2ASecretRef;
        existing.A2ARemoteAgentId = dto.A2ARemoteAgentId;
        existing.ModelId = dto.ModelId;
        existing.LlmConfigId = dto.LlmConfigId;
        existing.ExecutionMode = dto.ExecutionMode;
        existing.ModelSwitchingJson = dto.ModelSwitchingJson;
        existing.DelegateAgentIdsJson = dto.DelegateAgentIdsJson;
        existing.IsEnabled = dto.IsEnabled;
        existing.Status = dto.Status;
        if (dto.Status == "Published") existing.PublishedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return Ok(existing);
    }

    // ── DELETE /api/agents/{id} ───────────────────────────────────────────────
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(Tenant);
        var existing = await db.AgentDefinitions.FindAsync([id], ct);
        if (existing is null) return NotFound();
        db.AgentDefinitions.Remove(existing);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── POST /api/agents/{id}/prompt/improve ──────────────────────────────────
    [HttpPost("{id}/prompt/improve")]
    public async Task<IActionResult> ImprovePrompt(
        string id,
        [FromBody] ImprovePromptRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Instruction))
            return BadRequest(new { error = "Instruction is required." });

        using var db = _db.CreateDbContext(Tenant);
        var agent = await db.AgentDefinitions.FindAsync([id], ct);
        if (agent is null) return NotFound();

        try
        {
            var improved = await _promptImprover.QuickImprovePromptAsync(
                agent.SystemPrompt ?? "", body.Instruction, agent, ct);
            return Ok(new { improvedPrompt = improved });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quick prompt improve failed for agent {AgentId}", id);
            return StatusCode(500, new { error = "LLM call failed — check API key and provider configuration." });
        }
    }

    // ── POST /api/agents/{id}/invoke ──────────────────────────────────────────
    [HttpPost("{id}/invoke")]
    public async Task<IActionResult> Invoke(string id, [FromBody] AgentInvokeRequest req, CancellationToken ct)
    {
        var tenant = Tenant;
        var agent = await ResolveAgentAsync(id, tenant, ct);
        if (agent is null) return NotFound();
        if (!agent.IsEnabled) return BadRequest(new { error = "Agent is disabled." });
        if (!tenant.CanInvokeAgent(agent.AgentType))
            return StatusCode(403, new { error = "Access denied to this agent." });

        var request = new AgentRequest { Query = req.Query, SessionId = req.SessionId, ModelId = req.ModelId, LlmConfigId = req.LlmConfigId, Attachments = req.Attachments ?? [] };

        var result = await _runner.RunAsync(agent, request, tenant, ct);
        return Ok(result);
    }

    // ── POST /api/agents/{id}/invoke/stream (SSE) ─────────────────────────────
    [HttpPost("{id}/invoke/stream")]
    public async Task InvokeStream(string id, [FromBody] AgentInvokeRequest req, CancellationToken ct)
    {
        var tenant = Tenant;
        var agent = await ResolveAgentAsync(id, tenant, ct);
        if (agent is null) { Response.StatusCode = 404; return; }
        if (!agent.IsEnabled) { Response.StatusCode = 400; return; }
        if (!tenant.CanInvokeAgent(agent.AgentType))
        {
            Response.StatusCode = 403;
            await Response.WriteAsync("Access denied to this agent.");
            return;
        }

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var request = new AgentRequest { Query = req.Query, SessionId = req.SessionId, ModelId = req.ModelId, LlmConfigId = req.LlmConfigId, ForwardSsoToMcp = req.ForwardSsoToMcp, Attachments = req.Attachments ?? [] };

        // If the resolved agent is an IStreamableWorkerAgent (e.g. RemoteA2AAgent, DynamicReActAgent),
        // delegate streaming directly to the worker instead of going through the runner.
        var worker = await _registry.GetByIdAsync(agent.Id, tenant.TenantId, ct);
        if (worker is IStreamableWorkerAgent streamable)
        {
            await foreach (var chunk in streamable.InvokeStreamAsync(request, tenant, ct))
            {
                var json = JsonSerializer.Serialize(chunk, _sseOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        else
        {
            await foreach (var chunk in _runner.InvokeStreamAsync(agent, request, tenant, ct))
            {
                var json = JsonSerializer.Serialize(chunk, _sseOptions);
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
    }

    // ── GET /api/agents/{id}/export ───────────────────────────────────────────
    [HttpGet("{id}/export")]
    public async Task<IActionResult> Export(string id, CancellationToken ct)
    {
        var tenant = Tenant;
        try
        {
            var bundle = await _agentExport.ExportAsync(id, tenant, ct);
            var fileName = $"{bundle.Agent.Name.Replace(" ", "-").ToLowerInvariant()}-export.json";
            var json = System.Text.Json.JsonSerializer.Serialize(bundle, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            });
            return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── POST /api/agents/import ───────────────────────────────────────────────
    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] AgentExportBundle bundle,
        [FromQuery] bool overwrite = false,
        [FromQuery] bool importRules = true,
        CancellationToken ct = default)
    {
        if (bundle?.Agent is null)
            return BadRequest(new { error = "Invalid export bundle." });

        var tenant = Tenant;
        var options = new AgentImportOptions
        {
            OverwriteExisting = overwrite,
            ImportRules = importRules,
        };

        var result = await _agentExport.ImportAsync(bundle, tenant, options, ct);
        return CreatedAtAction(nameof(Get), new { id = result.AgentId }, result);
    }

    // ── POST /api/agents/mcp-probe ────────────────────────────────────────────
    // Connects to an MCP server (HTTP or stdio) and returns its available tools.
    // PassSsoToken=true forwards the caller's Bearer JWT; CredentialRef resolves a stored credential.
    [HttpPost("mcp-probe")]
    public async Task<IActionResult> McpProbe([FromBody] McpProbeRequest req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            IClientTransport transport;

            if (!string.IsNullOrWhiteSpace(req.Endpoint))
            {
                Uri uri;
                try { uri = new Uri(req.Endpoint); }
                catch { return BadRequest(new { error = "Invalid URL" }); }

                var tenant = Tenant;
                ResolvedCredential? credential = null;
                if (!string.IsNullOrEmpty(req.CredentialRef) && _credentialResolver is not null)
                    credential = await _credentialResolver.ResolveAsync(tenant.TenantId, req.CredentialRef, cts.Token);

                var httpClient = new HttpClient();

                // Forward SSO Bearer token when requested
                if (req.PassSsoToken && !string.IsNullOrEmpty(tenant.AccessToken))
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {tenant.AccessToken}");

                // Inject stored credential when no Bearer token was added
                if (credential is not null && !httpClient.DefaultRequestHeaders.Contains("Authorization"))
                {
                    switch (credential.AuthScheme.ToLowerInvariant())
                    {
                        case "apikey":
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", credential.ApiKey);
                            break;
                        case "custom" when !string.IsNullOrEmpty(credential.CustomHeaderName):
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(credential.CustomHeaderName, credential.ApiKey);
                            break;
                        default:
                            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {credential.ApiKey}");
                            break;
                    }
                }

                transport = new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = uri, Name = "probe" },
                    httpClient);
            }
            else if (!string.IsNullOrWhiteSpace(req.Command))
            {
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "probe",
                    Command = req.Command,
                    Arguments = req.Args ?? [],
                });
            }
            else
            {
                return BadRequest(new { error = "Provide either endpoint (HTTP) or command (stdio)" });
            }

            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cts.Token);
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

            var result = tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "")).ToList();
            return Ok(new McpProbeResult(true, result, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP probe failed");
            return Ok(new McpProbeResult(false, [], ex.Message));
        }
    }

    // ── My groups (tenant-scoped) ─────────────────────────────────────────────

    /// <summary>Returns groups the current tenant belongs to. Used by tenant for "Publish to Group" picker.</summary>
    [HttpGet("my-groups")]
    public async Task<IActionResult> ListMyGroups(CancellationToken ct)
    {
        var tenant = Tenant;
        // TenantGroupMemberEntity has no ITenantEntity filter — query TenantId manually.
        // Use CreateDbContext() without tenant scope so no global filter interferes.
        using var db = _db.CreateDbContext();
        var memberships = await db.TenantGroupMembers
            .Where(m => m.TenantId == tenant.TenantId)
            .Include(m => m.Group)
            .Where(m => m.Group.IsActive)
            .Select(m => new { m.Group.Id, m.Group.Name, m.Group.Description })
            .ToListAsync(ct);
        return Ok(memberships);
    }

    // ── Group-template overlay endpoints ──────────────────────────────────────

    /// <summary>Lists all group templates available to the tenant with activation status.</summary>
    [HttpGet("group-templates")]
    public async Task<IActionResult> ListGroupTemplates(CancellationToken ct)
    {
        var tenant = Tenant;
        var templates = await _groups.GetAgentTemplatesForTenantAsync(tenant.TenantId, ct);
        var overlayMap = (await _overlays.GetOverlaysAsync(tenant.TenantId, ct))
            .ToDictionary(o => o.GroupTemplateId);

        var result = templates.Select(t =>
        {
            overlayMap.TryGetValue(t.Id, out var ov);
            return new GroupTemplateSummaryDto(
                t.Id, t.Name, t.DisplayName, t.Description, t.AgentType,
                t.GroupId, t.Group?.Name, t.IsEnabled,
                IsActivated: ov?.IsEnabled ?? false, OverlayGuid: ov?.Guid);
        });
        return Ok(result);
    }

    /// <summary>Returns read-only detail of a single group template.</summary>
    [HttpGet("group-templates/{templateId}")]
    public async Task<IActionResult> GetGroupTemplate(string templateId, CancellationToken ct)
    {
        var tenant = Tenant;
        var templates = await _groups.GetAgentTemplatesForTenantAsync(tenant.TenantId, ct);
        var template = templates.FirstOrDefault(t => t.Id == templateId);
        return template is null ? NotFound() : Ok(template);
    }

    /// <summary>Returns the tenant's current overlay for a group template (null if not applied).</summary>
    [HttpGet("group-templates/{templateId}/overlay")]
    public async Task<IActionResult> GetOverlay(string templateId, CancellationToken ct)
    {
        var overlay = await _overlays.GetOverlayAsync(Tenant.TenantId, templateId, ct);
        return overlay is null ? NotFound() : Ok(overlay);
    }

    /// <summary>Applies (activates) a group template for this tenant — upsert.</summary>
    [HttpPost("group-templates/{templateId}/overlay")]
    public async Task<IActionResult> ApplyOverlay(
        string templateId,
        [FromBody] ApplyGroupAgentOverlayDto dto,
        CancellationToken ct)
    {
        try
        {
            var overlay = await _overlays.ApplyTemplateAsync(Tenant.TenantId, templateId, dto, ct);
            return Ok(overlay);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Updates an existing overlay's fields.</summary>
    [HttpPut("group-templates/{templateId}/overlay")]
    public async Task<IActionResult> UpdateOverlay(
        string templateId,
        [FromBody] UpdateGroupAgentOverlayDto dto,
        CancellationToken ct)
    {
        var existing = await _overlays.GetOverlayAsync(Tenant.TenantId, templateId, ct);
        if (existing is null) return NotFound();
        try
        {
            var updated = await _overlays.UpdateOverlayAsync(Tenant.TenantId, existing.Guid, dto, ct);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Removes the tenant's overlay for a group template.</summary>
    [HttpDelete("group-templates/{templateId}/overlay")]
    public async Task<IActionResult> RemoveOverlay(string templateId, CancellationToken ct)
    {
        var existing = await _overlays.GetOverlayAsync(Tenant.TenantId, templateId, ct);
        if (existing is null) return NotFound();
        await _overlays.RemoveOverlayAsync(Tenant.TenantId, existing.Guid, ct);
        return NoContent();
    }

    /// <summary>Toggles IsEnabled on the tenant's overlay for a group template.</summary>
    [HttpPatch("group-templates/{templateId}/overlay/enabled")]
    public async Task<IActionResult> SetOverlayEnabled(
        string templateId,
        [FromBody] SetOverlayEnabledDto dto,
        CancellationToken ct)
    {
        var existing = await _overlays.GetOverlayAsync(Tenant.TenantId, templateId, ct);
        if (existing is null) return NotFound();
        var update = new UpdateGroupAgentOverlayDto(
            dto.IsEnabled,
            existing.SystemPromptAddendum,
            existing.ModelId,
            existing.Temperature,
            existing.ExtraToolBindingsJson,
            existing.CustomVariablesJson,
            existing.LlmConfigId,
            existing.MaxOutputTokens);
        var updated = await _overlays.UpdateOverlayAsync(Tenant.TenantId, existing.Guid, update, ct);
        return Ok(updated);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Resolves an agent by ID: checks own tenant definitions first, then group templates.
    private async Task<AgentDefinitionEntity?> ResolveAgentAsync(string id, TenantContext tenant, CancellationToken ct)
    {
        using var db = _db.CreateDbContext(tenant);
        var agent = await db.AgentDefinitions.FindAsync([id], ct);
        if (agent is not null) return agent;

        var groupTemplates = await _groups.GetAgentTemplatesForTenantAsync(tenant.TenantId, ct);
        var template = groupTemplates.FirstOrDefault(t => t.Id == id);
        return template is null ? null : MapToDefinition(template, tenant.TenantId);
    }

    private static AgentDefinitionEntity MapToDefinition(Diva.Infrastructure.Data.Entities.GroupAgentTemplateEntity t, int tenantId) => new()
    {
        Id = t.Id,
        TenantId = tenantId,
        Name = t.Name,
        DisplayName = t.DisplayName,
        Description = t.Description,
        AgentType = t.AgentType,
        SystemPrompt = t.SystemPrompt,
        ModelId = t.ModelId,
        Temperature = t.Temperature,
        MaxIterations = t.MaxIterations,
        Capabilities = t.Capabilities,
        ToolBindings = t.ToolBindings,
        VerificationMode = t.VerificationMode,
        ContextWindowJson = t.ContextWindowJson,
        CustomVariablesJson = t.CustomVariablesJson,
        MaxContinuations = t.MaxContinuations,
        MaxToolResultChars = t.MaxToolResultChars,
        MaxOutputTokens = t.MaxOutputTokens,
        EnableHistoryCaching = t.EnableHistoryCaching,
        PipelineStagesJson = t.PipelineStagesJson,
        ToolFilterJson = t.ToolFilterJson,
        StageInstructionsJson = t.StageInstructionsJson,
        IsEnabled = t.IsEnabled,
        Status = t.Status,
        CreatedAt = t.CreatedAt,
        // Phase-15 fields
        ArchetypeId = t.ArchetypeId,
        HooksJson = t.HooksJson,
        A2AEndpoint = t.A2AEndpoint,
        A2AAuthScheme = t.A2AAuthScheme,
        A2ASecretRef = t.A2ASecretRef,
        A2ARemoteAgentId = t.A2ARemoteAgentId,
        ExecutionMode = t.ExecutionMode,
        ModelSwitchingJson = t.ModelSwitchingJson,
        LlmConfigId = t.LlmConfigId,
    };

    private static readonly JsonSerializerOptions _sseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── GET /api/agents/archetypes ────────────────────────────────────────────
    [HttpGet("archetypes")]
    public IActionResult ListArchetypes()
        => Ok(_archetypes.GetAll().Select(a => new ArchetypeDto(a.Id, a.DisplayName, a.Description, a.Icon, a.Category)));

    // ── GET /api/agents/archetypes/{id} ───────────────────────────────────────
    [HttpGet("archetypes/{archetypeId}")]
    public IActionResult GetArchetype(string archetypeId)
    {
        var arch = _archetypes.GetById(archetypeId);
        if (arch is null) return NotFound();
        return Ok(arch);
    }

    // ── POST /api/agents/suggest-prompt ──────────────────────────────────────
    [HttpPost("suggest-prompt")]
    public async Task<IActionResult> SuggestPrompt(
        [FromBody] AgentSetupContext ctx,
        CancellationToken ct)
    {
        if (ctx is null) return BadRequest("Request body required.");
        if (ctx.AgentDescription?.Length > 500)
            return BadRequest("AgentDescription exceeds 500 character limit.");
        if (ctx.AdditionalContext?.Length > 300)
            return BadRequest("AdditionalContext exceeds 300 character limit.");

        var tenant = Tenant;
        ctx.TenantId = tenant.TenantId > 0 ? tenant.TenantId : ctx.TenantId;

        var suggestion = await _assistant.SuggestSystemPromptAsync(ctx, ct);
        return Ok(suggestion);
    }

    // ── POST /api/agents/suggest-rule-packs ───────────────────────────────────
    [HttpPost("suggest-rule-packs")]
    public async Task<IActionResult> SuggestRulePacks(
        [FromBody] AgentSetupContext ctx,
        CancellationToken ct)
    {
        if (ctx is null) return BadRequest("Request body required.");
        if (ctx.AgentDescription?.Length > 500)
            return BadRequest("AgentDescription exceeds 500 character limit.");
        if (ctx.AdditionalContext?.Length > 300)
            return BadRequest("AdditionalContext exceeds 300 character limit.");

        var tenant = Tenant;
        ctx.TenantId = tenant.TenantId > 0 ? tenant.TenantId : ctx.TenantId;

        var suggestions = await _assistant.SuggestRulePacksAsync(ctx, ct);
        return Ok(suggestions);
    }

    // ── GET /api/agents/{agentId}/prompt-history ─────────────────────────────
    [HttpGet("{agentId}/prompt-history")]
    public async Task<IActionResult> GetPromptHistory(string agentId, CancellationToken ct)
    {
        var tenant = Tenant;
        var history = await _assistant.GetAgentPromptHistoryAsync(agentId, tenant.TenantId, ct);
        return Ok(history);
    }

    // ── POST /api/agents/{agentId}/prompt-history/{version}/restore ──────────
    [HttpPost("{agentId}/prompt-history/{version:int}/restore")]
    public async Task<IActionResult> RestorePromptVersion(
        string agentId,
        int version,
        [FromBody] RestorePromptVersionRequestDto request,
        CancellationToken ct)
    {
        var tenant = Tenant;
        var actor = tenant.UserEmail ?? "unknown";
        var restored = await _assistant.RestorePromptVersionAsync(agentId, tenant.TenantId, version, request.Reason, actor, ct);
        if (restored is null) return NotFound($"Prompt history version {version} not found for agent {agentId}.");
        return Ok(restored);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────
public record AgentSummaryDto(string Id, string Name, string DisplayName, string AgentType, string Status, bool IsEnabled, DateTime CreatedAt, bool IsShared, int? GroupId, string? GroupName, int? LlmConfigId = null, bool IsActivated = false, string? OverlayGuid = null);
public record GroupTemplateSummaryDto(string Id, string Name, string DisplayName, string? Description, string AgentType, int GroupId, string? GroupName, bool IsEnabled, bool IsActivated, string? OverlayGuid);
public record SetOverlayEnabledDto(bool IsEnabled);
public record AgentInvokeRequest(
    string Query,
    string? SessionId = null,
    string? ModelId = null,
    int? LlmConfigId = null,
    bool ForwardSsoToMcp = false,
    List<ContentPart>? Attachments = null);
public record ImprovePromptRequest(string Instruction);
public record McpProbeRequest(string? Endpoint, string? Command, List<string>? Args, bool PassSsoToken = false, string? CredentialRef = null);
public record McpToolInfo(string Name, string Description);
public record McpProbeResult(bool Success, List<McpToolInfo> Tools, string? Error);
public record ArchetypeDto(string Id, string DisplayName, string Description, string Icon, string Category);
