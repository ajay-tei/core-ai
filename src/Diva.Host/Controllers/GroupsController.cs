using Diva.Infrastructure.Auth;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

// ── Request DTOs ───────────────────────────────────────────────────────────────

public record CreateGroupRequest(string Name, string? Description);
public record UpdateGroupRequest(string Name, string? Description, bool IsActive);
public record AddGroupMemberRequest(int TenantId);

/// <summary>
/// Platform-level group management — master admin only.
/// Manages tenant groups and all their shared resources (agents, rules, prompts, schedules, LLM config).
/// </summary>
[ApiController]
[Route("api/platform/groups")]
public class GroupsController : ControllerBase
{
    private readonly ITenantGroupService _groups;

    public GroupsController(ITenantGroupService groups) => _groups = groups;

    private IActionResult? RequireMasterAdmin()
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx?.IsMasterAdmin == true ? null : StatusCode(403, "Master admin access required.");
    }

    // ── Groups ────────────────────────────────────────────────────────────────

    // GET /api/platform/groups
    [HttpGet]
    public async Task<IActionResult> ListGroups(CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var groups = await _groups.GetAllGroupsAsync(ct);
        return Ok(groups.Select(g => new
        {
            g.Id,
            g.Name,
            g.Description,
            g.IsActive,
            g.CreatedAt,
            memberCount = g.Members.Count,
        }));
    }

    // GET /api/platform/groups/{id}
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetGroup(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var group = await _groups.GetGroupByIdAsync(id, ct);
        if (group is null) return NotFound();
        return Ok(new
        {
            group.Id,
            group.Name,
            group.Description,
            group.IsActive,
            group.CreatedAt,
            memberCount = group.Members.Count,
            llmConfigCount = group.LlmConfigs.Count,
        });
    }

    // POST /api/platform/groups
    [HttpPost]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest req, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var group = await _groups.CreateGroupAsync(new CreateGroupDto(req.Name, req.Description), ct);
        return CreatedAtAction(nameof(GetGroup), new { id = group.Id },
            new { group.Id, group.Name, group.IsActive, group.CreatedAt });
    }

    // PUT /api/platform/groups/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateGroup(int id, [FromBody] UpdateGroupRequest req, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var group = await _groups.UpdateGroupAsync(id, new UpdateGroupDto(req.Name, req.Description, req.IsActive), ct);
            return Ok(new { group.Id, group.Name, group.IsActive });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteGroup(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeleteGroupAsync(id, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Members ───────────────────────────────────────────────────────────────

    // GET /api/platform/groups/{id}/members
    [HttpGet("{id:int}/members")]
    public async Task<IActionResult> ListMembers(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var members = await _groups.GetMembersAsync(id, ct);
        return Ok(members.Select(m => new { m.Id, m.GroupId, m.TenantId, m.JoinedAt }));
    }

    // POST /api/platform/groups/{id}/members
    [HttpPost("{id:int}/members")]
    public async Task<IActionResult> AddMember(int id, [FromBody] AddGroupMemberRequest req, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var member = await _groups.AddMemberAsync(id, req.TenantId, ct);
            return Ok(new { member.Id, member.GroupId, member.TenantId, member.JoinedAt });
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/members/{tenantId}
    [HttpDelete("{id:int}/members/{tenantId:int}")]
    public async Task<IActionResult> RemoveMember(int id, int tenantId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.RemoveMemberAsync(id, tenantId, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Agent Templates ───────────────────────────────────────────────────────

    // GET /api/platform/groups/{id}/agents
    [HttpGet("{id:int}/agents")]
    public async Task<IActionResult> ListAgentTemplates(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var templates = await _groups.GetAgentTemplatesAsync(id, ct);
        return Ok(templates.Select(t => new
        {
            t.Id,
            t.GroupId,
            t.Name,
            t.DisplayName,
            t.AgentType,
            t.Description,
            t.ModelId,
            t.Temperature,
            t.MaxIterations,
            t.IsEnabled,
            t.Status,
            t.CreatedAt,
        }));
    }

    // GET /api/platform/groups/{id}/agents/{templateId}
    [HttpGet("{id:int}/agents/{templateId}")]
    public async Task<IActionResult> GetAgentTemplate(int id, string templateId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var t = await _groups.GetAgentTemplateAsync(id, templateId, ct);
        if (t is null) return NotFound();
        return Ok(new
        {
            t.Id,
            t.GroupId,
            t.Name,
            t.DisplayName,
            t.Description,
            t.AgentType,
            t.SystemPrompt,
            t.ModelId,
            t.Temperature,
            t.MaxIterations,
            t.Capabilities,
            t.ToolBindings,
            t.VerificationMode,
            t.ContextWindowJson,
            t.CustomVariablesJson,
            t.MaxContinuations,
            t.MaxToolResultChars,
            t.MaxOutputTokens,
            t.EnableHistoryCaching,
            t.PipelineStagesJson,
            t.ToolFilterJson,
            t.StageInstructionsJson,
            t.LlmConfigId,
            // Phase-15 fields
            t.ArchetypeId,
            t.HooksJson,
            t.A2AEndpoint,
            t.A2AAuthScheme,
            t.A2ASecretRef,
            t.ExecutionMode,
            t.ModelSwitchingJson,
            t.IsEnabled,
            t.Status,
            t.Version,
            t.CreatedAt,
            t.UpdatedAt,
        });
    }

    // POST /api/platform/groups/{id}/agents
    [HttpPost("{id:int}/agents")]
    public async Task<IActionResult> CreateAgentTemplate(int id, [FromBody] CreateGroupAgentDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var t = await _groups.CreateAgentTemplateAsync(id, dto, ct);
        return CreatedAtAction(nameof(GetAgentTemplate), new { id, templateId = t.Id },
            new { t.Id, t.GroupId, t.Name, t.DisplayName, t.AgentType, t.IsEnabled });
    }

    // PUT /api/platform/groups/{id}/agents/{templateId}
    [HttpPut("{id:int}/agents/{templateId}")]
    public async Task<IActionResult> UpdateAgentTemplate(int id, string templateId, [FromBody] UpdateGroupAgentDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var t = await _groups.UpdateAgentTemplateAsync(id, templateId, dto, ct);
            return Ok(new { t.Id, t.Name, t.DisplayName, t.IsEnabled });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/agents/{templateId}
    [HttpDelete("{id:int}/agents/{templateId}")]
    public async Task<IActionResult> DeleteAgentTemplate(int id, string templateId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeleteAgentTemplateAsync(id, templateId, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Business Rules ────────────────────────────────────────────────────────

    // GET /api/platform/groups/{id}/business-rules
    [HttpGet("{id:int}/business-rules")]
    public async Task<IActionResult> ListBusinessRules(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        return Ok(await _groups.GetBusinessRulesAsync(id, ct));
    }

    // POST /api/platform/groups/{id}/business-rules
    [HttpPost("{id:int}/business-rules")]
    public async Task<IActionResult> CreateBusinessRule(int id, [FromBody] CreateGroupRuleDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var rule = await _groups.CreateBusinessRuleAsync(id, dto, ct);
        return Ok(rule);
    }

    // PUT /api/platform/groups/{id}/business-rules/{ruleId}
    [HttpPut("{id:int}/business-rules/{ruleId:int}")]
    public async Task<IActionResult> UpdateBusinessRule(int id, int ruleId, [FromBody] UpdateGroupRuleDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { return Ok(await _groups.UpdateBusinessRuleAsync(id, ruleId, dto, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/business-rules/{ruleId}
    [HttpDelete("{id:int}/business-rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteBusinessRule(int id, int ruleId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeleteBusinessRuleAsync(id, ruleId, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Prompt Overrides ──────────────────────────────────────────────────────

    // GET /api/platform/groups/{id}/prompt-overrides
    [HttpGet("{id:int}/prompt-overrides")]
    public async Task<IActionResult> ListPromptOverrides(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        return Ok(await _groups.GetPromptOverridesAsync(id, ct));
    }

    // POST /api/platform/groups/{id}/prompt-overrides
    [HttpPost("{id:int}/prompt-overrides")]
    public async Task<IActionResult> CreatePromptOverride(int id, [FromBody] CreateGroupPromptOverrideDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        return Ok(await _groups.CreatePromptOverrideAsync(id, dto, ct));
    }

    // PUT /api/platform/groups/{id}/prompt-overrides/{overrideId}
    [HttpPut("{id:int}/prompt-overrides/{overrideId:int}")]
    public async Task<IActionResult> UpdatePromptOverride(int id, int overrideId, [FromBody] UpdateGroupPromptOverrideDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { return Ok(await _groups.UpdatePromptOverrideAsync(id, overrideId, dto, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/prompt-overrides/{overrideId}
    [HttpDelete("{id:int}/prompt-overrides/{overrideId:int}")]
    public async Task<IActionResult> DeletePromptOverride(int id, int overrideId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeletePromptOverrideAsync(id, overrideId, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Scheduled Tasks ───────────────────────────────────────────────────────

    // GET /api/platform/groups/{id}/schedules
    [HttpGet("{id:int}/schedules")]
    public async Task<IActionResult> ListSchedules(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        return Ok(await _groups.GetScheduledTasksAsync(id, ct));
    }

    // GET /api/platform/groups/{id}/schedules/{taskId}
    [HttpGet("{id:int}/schedules/{taskId}")]
    public async Task<IActionResult> GetSchedule(int id, string taskId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var task = await _groups.GetScheduledTaskAsync(id, taskId, ct);
        return task is null ? NotFound() : Ok(task);
    }

    // POST /api/platform/groups/{id}/schedules
    [HttpPost("{id:int}/schedules")]
    public async Task<IActionResult> CreateSchedule(int id, [FromBody] CreateGroupTaskDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var task = await _groups.CreateScheduledTaskAsync(id, dto, ct);
        return CreatedAtAction(nameof(GetSchedule), new { id, taskId = task.Id }, task);
    }

    // PUT /api/platform/groups/{id}/schedules/{taskId}
    [HttpPut("{id:int}/schedules/{taskId}")]
    public async Task<IActionResult> UpdateSchedule(int id, string taskId, [FromBody] UpdateGroupTaskDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { return Ok(await _groups.UpdateScheduledTaskAsync(id, taskId, dto, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/schedules/{taskId}
    [HttpDelete("{id:int}/schedules/{taskId}")]
    public async Task<IActionResult> DeleteSchedule(int id, string taskId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeleteScheduledTaskAsync(id, taskId, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // PATCH /api/platform/groups/{id}/schedules/{taskId}/enabled
    [HttpPatch("{id:int}/schedules/{taskId}/enabled")]
    public async Task<IActionResult> SetScheduleEnabled(
        int id, string taskId, [FromBody] SetEnabledRequest req, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.SetScheduledTaskEnabledAsync(id, taskId, req.IsEnabled, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // POST /api/platform/groups/{id}/schedules/import
    [HttpPost("{id:int}/schedules/import")]
    public async Task<IActionResult> ImportSchedules(
        int id, [FromBody] GroupScheduleImportRequest dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        if (dto?.Tasks is null) return BadRequest(new { error = "Request body is required." });

        var existing = (await _groups.GetScheduledTasksAsync(id, ct))
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
                await _groups.CreateScheduledTaskAsync(id, new CreateGroupTaskDto(
                    task.AgentType, task.Name, task.Description,
                    task.ScheduleType, task.ScheduledAtUtc, task.RunAtTime, task.DayOfWeek,
                    task.TimeZoneId ?? "UTC", task.PayloadType ?? "prompt",
                    task.PromptText, task.ParametersJson, task.IsEnabled), ct);
                created++;
            }
            catch (Exception e) { ex = e; }
        }

        return Ok(new GroupScheduleImportResult(created, skippedNames.Count, skippedNames));
    }

    // ── Group LLM Config (default unnamed) ───────────────────────────────────

    // GET /api/platform/groups/{id}/llm-config  — default unnamed config
    [HttpGet("{id:int}/llm-config")]
    public async Task<IActionResult> GetGroupLlmConfig(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.GetGroupLlmConfigAsync(id, ct);
        if (config is null) return Ok(null);
        return Ok(MapLlmConfig(config));
    }

    // PUT /api/platform/groups/{id}/llm-config  — upsert default unnamed config
    [HttpPut("{id:int}/llm-config")]
    public async Task<IActionResult> UpsertGroupLlmConfig(int id, [FromBody] UpsertLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.UpsertGroupLlmConfigAsync(id, dto, ct);
        return Ok(MapLlmConfig(config));
    }

    // ── Group LLM Configs (named list) ────────────────────────────────────────

    // GET /api/platform/groups/{id}/llm-configs
    [HttpGet("{id:int}/llm-configs")]
    public async Task<IActionResult> ListGroupLlmConfigs(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var configs = await _groups.ListGroupLlmConfigsAsync(id, ct);
        return Ok(configs.Select(MapLlmConfig));
    }

    // POST /api/platform/groups/{id}/llm-configs
    [HttpPost("{id:int}/llm-configs")]
    public async Task<IActionResult> CreateGroupLlmConfig(int id, [FromBody] CreateNamedLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.CreateGroupLlmConfigAsync(id, dto, ct);
        return Ok(MapLlmConfig(config));
    }

    // PUT /api/platform/groups/{id}/llm-configs/{cfgId}
    [HttpPut("{id:int}/llm-configs/{cfgId:int}")]
    public async Task<IActionResult> UpdateGroupLlmConfig(int id, int cfgId, [FromBody] UpsertLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var config = await _groups.UpdateGroupLlmConfigByIdAsync(cfgId, id, dto, ct);
            return Ok(MapLlmConfig(config));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/groups/{id}/llm-configs/{cfgId}
    [HttpDelete("{id:int}/llm-configs/{cfgId:int}")]
    public async Task<IActionResult> DeleteGroupLlmConfig(int id, int cfgId, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeleteGroupLlmConfigByIdAsync(cfgId, id, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // POST /api/platform/groups/{id}/llm-configs/ref  — add a platform config reference to a group
    [HttpPost("{id:int}/llm-configs/ref")]
    public async Task<IActionResult> AddGroupPlatformRef(int id, [FromBody] AddGroupPlatformRefDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var config = await _groups.AddGroupPlatformRefAsync(id, dto, ct);
            return Ok(MapLlmConfig(config));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private static object MapLlmConfig(Diva.Infrastructure.Data.Entities.GroupLlmConfigEntity c) => new
    {
        c.Id,
        c.GroupId,
        c.Name,
        c.Provider,
        apiKey = c.ApiKey is not null ? "••••••••" : (string?)null,
        c.Model,
        c.Endpoint,
        c.DeploymentName,
        c.AvailableModelsJson,
        c.UpdatedAt,
        platformConfigRef = c.PlatformConfigRef,
        c.EnvironmentId,
    };
}

public record SetEnabledRequest(bool IsEnabled);

public sealed record GroupScheduledTaskExport(
    string AgentType,
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
    bool IsEnabled);

public sealed record GroupScheduleImportRequest(
    List<GroupScheduledTaskExport> Tasks,
    bool SkipConflicts = true);

public sealed record GroupScheduleImportResult(
    int Created,
    int Skipped,
    List<string> SkippedNames);
