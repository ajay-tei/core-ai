using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Learning;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/admin")]
[RequireTenantAdmin]
public class AdminController : ControllerBase
{
    private readonly ITenantBusinessRulesService _rules;
    private readonly IRuleLearningService _learnedRules;
    private readonly IDatabaseProviderFactory _db;
    private readonly ITenantSsoConfigService _sso;
    private readonly ITenantGroupService _groups;

    public AdminController(
        ITenantBusinessRulesService rules,
        IRuleLearningService learnedRules,
        IDatabaseProviderFactory db,
        ITenantSsoConfigService sso,
        ITenantGroupService groups)
    {
        _rules = rules;
        _learnedRules = learnedRules;
        _db = db;
        _sso = sso;
        _groups = groups;
    }

    /// <summary>
    /// Returns the tenant ID to use for the current request.
    /// Regular users: always their own JWT tenant (query param ignored — security).
    /// Master admin (TenantId=0): uses the query param so they can manage any tenant.
    /// </summary>
    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── Business Rules ────────────────────────────────────────────────────────

    // GET /api/admin/business-rules?tenantId=1&agentType=*&agentId=<optional>
    [HttpGet("business-rules")]
    public async Task<IActionResult> GetRules(
        [FromQuery] int tenantId = 1,
        [FromQuery] string agentType = "*",
        [FromQuery] string? agentId = null,
        CancellationToken ct = default)
        => Ok(await _rules.GetRulesAsync(EffectiveTenantId(tenantId), agentType, ct, agentId));

    // POST /api/admin/business-rules
    [HttpPost("business-rules")]
    public async Task<IActionResult> CreateRule(
        [FromBody] CreateRuleDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var entity = await _rules.CreateRuleAsync(tid, dto, ct);
        return CreatedAtAction(nameof(GetRules), new { tenantId = tid }, entity);
    }

    // PUT /api/admin/business-rules/{id}
    [HttpPut("business-rules/{id:int}")]
    public async Task<IActionResult> UpdateRule(
        int id,
        [FromBody] UpdateRuleDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _rules.UpdateRuleAsync(EffectiveTenantId(tenantId), id, dto, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/business-rules/{id}
    [HttpDelete("business-rules/{id:int}")]
    public async Task<IActionResult> DeleteRule(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _rules.DeleteRuleAsync(EffectiveTenantId(tenantId), id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // GET /api/admin/business-rules/by-pack/{packId}?tenantId=1
    [HttpGet("business-rules/by-pack/{packId:int}")]
    public async Task<IActionResult> GetRulesByPack(
        int packId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _rules.GetRulesForPackAsync(EffectiveTenantId(tenantId), packId, ct));

    // POST /api/admin/business-rules/{id}/assign-pack
    [HttpPost("business-rules/{id:int}/assign-pack")]
    public async Task<IActionResult> AssignRuleToPack(
        int id,
        [FromBody] AssignRuleToPackDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _rules.AssignRuleToPackAsync(EffectiveTenantId(tenantId), id, dto.RulePackId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // POST /api/admin/business-rules/{id}/unassign-pack
    [HttpPost("business-rules/{id:int}/unassign-pack")]
    public async Task<IActionResult> UnassignRuleFromPack(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _rules.AssignRuleToPackAsync(EffectiveTenantId(tenantId), id, null, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // POST /api/admin/business-rules/validate
    [HttpPost("business-rules/validate")]
    public IActionResult ValidateBusinessRule([FromBody] ValidateBusinessRuleDto dto)
    {
        var (valid, allowed) = RulePackRuleCompatibility.ValidateBusinessRule(
            dto.HookPoint ?? "OnInit", dto.HookRuleType ?? "inject_prompt");
        return Ok(new { valid, allowedTypes = allowed });
    }

    // ── Group Rule Templates ──────────────────────────────────────────────────

    // GET /api/admin/group-rule-templates?tenantId=1
    [HttpGet("group-rule-templates")]
    public async Task<IActionResult> GetGroupRuleTemplates(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _rules.GetAvailableGroupRuleTemplatesAsync(EffectiveTenantId(tenantId), ct));

    // POST /api/admin/group-rule-templates/{groupRuleId}/activate
    [HttpPost("group-rule-templates/{groupRuleId:int}/activate")]
    public async Task<IActionResult> ActivateGroupRuleTemplate(
        int groupRuleId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _rules.ActivateGroupRuleAsync(EffectiveTenantId(tenantId), groupRuleId, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/group-rule-templates/{groupRuleId}/activate
    [HttpDelete("group-rule-templates/{groupRuleId:int}/activate")]
    public async Task<IActionResult> DeactivateGroupRuleTemplate(
        int groupRuleId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _rules.DeactivateGroupRuleAsync(EffectiveTenantId(tenantId), groupRuleId, ct);
        return NoContent();
    }

    // ── Group Prompt Templates ────────────────────────────────────────────────

    // GET /api/admin/group-prompt-templates?tenantId=1
    [HttpGet("group-prompt-templates")]
    public async Task<IActionResult> GetGroupPromptTemplates(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _groups.GetAvailableGroupPromptTemplatesAsync(EffectiveTenantId(tenantId), ct));

    // POST /api/admin/group-prompt-templates/{groupOverrideId}/activate
    [HttpPost("group-prompt-templates/{groupOverrideId:int}/activate")]
    public async Task<IActionResult> ActivateGroupPromptTemplate(
        int groupOverrideId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _groups.ActivateGroupPromptTemplateAsync(EffectiveTenantId(tenantId), groupOverrideId, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/group-prompt-templates/{groupOverrideId}/activate
    [HttpDelete("group-prompt-templates/{groupOverrideId:int}/activate")]
    public async Task<IActionResult> DeactivateGroupPromptTemplate(
        int groupOverrideId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _groups.DeactivateGroupPromptTemplateAsync(EffectiveTenantId(tenantId), groupOverrideId, ct);
        return NoContent();
    }

    // ── Prompt Overrides ──────────────────────────────────────────────────────

    // GET /api/admin/prompt-overrides?tenantId=1&agentType=*&agentId=
    [HttpGet("prompt-overrides")]
    public async Task<IActionResult> GetPromptOverrides(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? agentType = null,
        [FromQuery] string? agentId = null,
        CancellationToken ct = default)
        => Ok(await _rules.ListAllPromptOverridesAsync(EffectiveTenantId(tenantId), agentType, agentId, ct));

    // POST /api/admin/prompt-overrides
    [HttpPost("prompt-overrides")]
    public async Task<IActionResult> CreatePromptOverride(
        [FromBody] CreatePromptOverrideDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var entity = await _rules.CreatePromptOverrideAsync(tid, dto, ct);
        return CreatedAtAction(nameof(GetPromptOverrides), new { tenantId = tid }, entity);
    }

    // PUT /api/admin/prompt-overrides/{id}
    [HttpPut("prompt-overrides/{id:int}")]
    public async Task<IActionResult> UpdatePromptOverride(
        int id,
        [FromBody] UpdatePromptOverrideDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _rules.UpdatePromptOverrideAsync(EffectiveTenantId(tenantId), id, dto, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/prompt-overrides/{id}
    [HttpDelete("prompt-overrides/{id:int}")]
    public async Task<IActionResult> DeletePromptOverride(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _rules.DeletePromptOverrideAsync(EffectiveTenantId(tenantId), id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Learned Rules (Phase 11) ──────────────────────────────────────────────

    // GET /api/admin/learned-rules?tenantId=1
    [HttpGet("learned-rules")]
    public async Task<IActionResult> GetLearnedRules(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _learnedRules.GetPendingRulesAsync(EffectiveTenantId(tenantId), ct));

    // POST /api/admin/learned-rules/{id}/approve
    [HttpPost("learned-rules/{id:int}/approve")]
    public async Task<IActionResult> ApproveLearnedRule(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        await _learnedRules.ApproveRuleAsync(tid, id, "admin", ct);
        _rules.InvalidateCache(tid, "*");
        return NoContent();
    }

    // POST /api/admin/learned-rules/{id}/reject
    [HttpPost("learned-rules/{id:int}/reject")]
    public async Task<IActionResult> RejectLearnedRule(
        int id,
        [FromBody] RejectRuleBody? body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _learnedRules.RejectRuleAsync(EffectiveTenantId(tenantId), id, "admin", body?.Notes ?? "", ct);
        return NoContent();
    }

    // ── Dashboard ─────────────────────────────────────────────────────────────

    // GET /api/admin/dashboard?tenantId=1
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        using var db = _db.CreateDbContext(TenantContext.System(tid));

        var agentCount = await db.AgentDefinitions.CountAsync(ct);
        var activeRuleCount = await db.BusinessRules.CountAsync(r => r.IsActive, ct);
        var pendingRuleCount = await db.LearnedRules.CountAsync(r => r.Status == "pending", ct);
        var sessionCount = await db.Sessions.CountAsync(ct);

        return Ok(new
        {
            agentCount,
            activeRuleCount,
            pendingRuleCount,
            sessionCount,
            asOf = DateTime.UtcNow,
        });
    }
}

public record RejectRuleBody(string? Notes);

// ── SSO Configurations ─────────────────────────────────────────────────────────

[ApiController]
[Route("api/admin/sso-configs")]
[RequireTenantAdmin]
public class SsoConfigController : ControllerBase
{
    private readonly ITenantSsoConfigService _sso;

    public SsoConfigController(ITenantSsoConfigService sso) => _sso = sso;

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/sso-configs?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
        => Ok(await _sso.GetForTenantAsync(EffectiveTenantId(tenantId), ct));

    // GET /api/admin/sso-configs/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var entity = await _sso.GetByIdAsync(EffectiveTenantId(tenantId), id, ct);
        return entity is null ? NotFound() : Ok(entity);
    }

    // POST /api/admin/sso-configs?tenantId=1
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateSsoConfigDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var entity = await _sso.CreateAsync(tid, dto, ct);
        return CreatedAtAction(nameof(GetById), new { id = entity.Id, tenantId = tid }, entity);
    }

    // PUT /api/admin/sso-configs/{id}?tenantId=1
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateSsoConfigDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _sso.UpdateAsync(EffectiveTenantId(tenantId), id, dto, ct);
            return Ok(entity);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/sso-configs/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        await _sso.DeleteAsync(EffectiveTenantId(tenantId), id, ct);
        return NoContent();
    }
}

// ── User Profiles ──────────────────────────────────────────────────────────────

[ApiController]
[Route("api/admin/user-profiles")]
[RequireTenantAdmin]
public class UserProfilesController : ControllerBase
{
    private readonly IUserProfileService _profiles;

    public UserProfilesController(IUserProfileService profiles) => _profiles = profiles;

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/user-profiles?tenantId=1&search=alice&role=admin
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int tenantId = 1,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        CancellationToken ct = default)
        => Ok(await _profiles.GetForTenantAsync(EffectiveTenantId(tenantId), search, role, ct));

    // GET /api/admin/user-profiles/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var entity = await _profiles.GetByIdAsync(EffectiveTenantId(tenantId), id, ct);
        return entity is null ? NotFound() : Ok(entity);
    }

    // PUT /api/admin/user-profiles/{id}?tenantId=1
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        [FromBody] UpdateUserProfileDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _profiles.UpdateAsync(EffectiveTenantId(tenantId), id, dto, ct);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // POST /api/admin/user-profiles/{id}/disable?tenantId=1
    [HttpPost("{id:int}/disable")]
    public async Task<IActionResult> Disable(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        try { await _profiles.SetActiveAsync(EffectiveTenantId(tenantId), id, false, ct); return NoContent(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // POST /api/admin/user-profiles/{id}/enable?tenantId=1
    [HttpPost("{id:int}/enable")]
    public async Task<IActionResult> Enable(int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        try { await _profiles.SetActiveAsync(EffectiveTenantId(tenantId), id, true, ct); return NoContent(); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }
}

// ── A2A Configuration (read-only view of appsettings A2A section) ─────────────

[ApiController]
[Route("api/admin/a2a-config")]
[RequireTenantAdmin]
public class A2AConfigController : ControllerBase
{
    private readonly A2AOptions _opts;

    public A2AConfigController(IOptions<A2AOptions> opts) => _opts = opts.Value;

    [HttpGet]
    public IActionResult Get() => Ok(_opts);
}

// ── Widget Config CRUD ─────────────────────────────────────────────────────────

[ApiController]
[Route("api/admin/widgets")]
[RequireTenantAdmin]
public class WidgetAdminController : ControllerBase
{
    private readonly IWidgetConfigService _widgets;

    public WidgetAdminController(IWidgetConfigService widgets) => _widgets = widgets;

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // GET /api/admin/widgets?tenantId=1
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int tenantId = 1, CancellationToken ct = default)
        => Ok(await _widgets.GetForTenantAsync(EffectiveTenantId(tenantId), ct));

    // POST /api/admin/widgets?tenantId=1
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] Diva.Core.Models.Widgets.CreateWidgetRequest dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var result = await _widgets.CreateAsync(EffectiveTenantId(tenantId), dto, ct);
        return CreatedAtAction(nameof(List), new { tenantId }, result);
    }

    // PUT /api/admin/widgets/{id}?tenantId=1
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] Diva.Core.Models.Widgets.CreateWidgetRequest dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            return Ok(await _widgets.UpdateAsync(EffectiveTenantId(tenantId), id, dto, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/widgets/{id}?tenantId=1
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        await _widgets.DeleteAsync(EffectiveTenantId(tenantId), id, ct);
        return NoContent();
    }
}
