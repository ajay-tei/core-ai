using Diva.Core.Models;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/admin/rule-packs")]
[RequireTenantAdmin]
public class RulePackController : ControllerBase
{
    private readonly IRulePackService _packs;
    private readonly RulePackEngine _engine;
    private readonly RulePackConflictAnalyzer _conflicts;
    private readonly IAgentSetupAssistant _assistant;

    public RulePackController(
        IRulePackService packs,
        RulePackEngine engine,
        RulePackConflictAnalyzer conflicts,
        IAgentSetupAssistant assistant)
    {
        _packs = packs;
        _engine = engine;
        _conflicts = conflicts;
        _assistant = assistant;
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── Pack CRUD ─────────────────────────────────────────────────────────────

    // GET /api/admin/rule-packs?tenantId=1
    [HttpGet]
    public async Task<IActionResult> GetPacks(
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
        => Ok(await _packs.GetPacksAsync(EffectiveTenantId(tenantId), ct));

    // GET /api/admin/rule-packs/starters
    [HttpGet("starters")]
    public async Task<IActionResult> GetStarterPacks(CancellationToken ct = default)
        => Ok(await _packs.GetStarterPacksAsync(ct));

    // GET /api/admin/rule-packs/{id}?tenantId=1
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetPack(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var pack = await _packs.GetPackWithRulesAsync(EffectiveTenantId(tenantId), id, ct);
        return pack is null ? NotFound() : Ok(pack);
    }

    // POST /api/admin/rule-packs?tenantId=1
    [HttpPost]
    public async Task<IActionResult> CreatePack(
        [FromBody] CreateRulePackDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var entity = await _packs.CreatePackAsync(tid, dto, ct);
        return CreatedAtAction(nameof(GetPack), new { id = entity.Id, tenantId = tid }, entity);
    }

    // PUT /api/admin/rule-packs/{id}?tenantId=1
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdatePack(
        int id,
        [FromBody] UpdateRulePackDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _packs.UpdatePackAsync(EffectiveTenantId(tenantId), id, dto, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/rule-packs/{id}?tenantId=1
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeletePack(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _packs.DeletePackAsync(EffectiveTenantId(tenantId), id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // POST /api/admin/rule-packs/{sourceId}/clone?tenantId=1
    [HttpPost("{sourceId:int}/clone")]
    public async Task<IActionResult> ClonePack(
        int sourceId,
        [FromBody] ClonePackRequest body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var tid = EffectiveTenantId(tenantId);
            var entity = await _packs.ClonePackAsync(tid, sourceId, body.NewName, ct);
            return CreatedAtAction(nameof(GetPack), new { id = entity.Id, tenantId = tid }, entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Rule CRUD ─────────────────────────────────────────────────────────────

    // POST /api/admin/rule-packs/{packId}/rules?tenantId=1
    [HttpPost("{packId:int}/rules")]
    public async Task<IActionResult> AddRule(
        int packId,
        [FromBody] CreateHookRuleDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _packs.AddRuleAsync(EffectiveTenantId(tenantId), packId, dto, ct);
            return Created($"/api/admin/rule-packs/{packId}/rules/{entity.Id}", entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // PUT /api/admin/rule-packs/{packId}/rules/{ruleId}?tenantId=1
    [HttpPut("{packId:int}/rules/{ruleId:int}")]
    public async Task<IActionResult> UpdateRule(
        int packId,
        int ruleId,
        [FromBody] UpdateHookRuleDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            var entity = await _packs.UpdateRuleAsync(EffectiveTenantId(tenantId), packId, ruleId, dto, ct);
            return Ok(entity);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // DELETE /api/admin/rule-packs/{packId}/rules/{ruleId}?tenantId=1
    [HttpDelete("{packId:int}/rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteRule(
        int packId,
        int ruleId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        try
        {
            await _packs.DeleteRuleAsync(EffectiveTenantId(tenantId), packId, ruleId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // POST /api/admin/rule-packs/{packId}/reorder?tenantId=1
    [HttpPost("{packId:int}/reorder")]
    public async Task<IActionResult> ReorderRules(
        int packId,
        [FromBody] int[] ruleIds,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _packs.ReorderRulesAsync(EffectiveTenantId(tenantId), packId, ruleIds, ct);
        return NoContent();
    }

    // ── Conflict Analysis ─────────────────────────────────────────────────────

    // GET /api/admin/rule-packs/{id}/conflicts?tenantId=1
    [HttpGet("{id:int}/conflicts")]
    public async Task<IActionResult> AnalyzeConflicts(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var pack = await _packs.GetPackWithRulesAsync(tid, id, ct);
        if (pack is null) return NotFound();

        var internalConflicts = _conflicts.AnalyzePack(pack);

        // Also check cross-pack conflicts
        var allPacks = await _packs.GetPacksAsync(tid, ct);
        var crossConflicts = _conflicts.AnalyzeCrossPack(allPacks);

        return Ok(new
        {
            packId = id,
            @internal = internalConflicts,
            crossPack = crossConflicts,
        });
    }

    // ── Dry Run / Test ────────────────────────────────────────────────────────

    // POST /api/admin/rule-packs/{id}/test?tenantId=1
    [HttpPost("{id:int}/test")]
    public async Task<IActionResult> TestPack(
        int id,
        [FromBody] RulePackTestRequest body,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var pack = await _packs.GetPackWithRulesAsync(EffectiveTenantId(tenantId), id, ct);
        if (pack is null) return NotFound();

        var result = _engine.DryRun(pack, body.SampleQuery, body.SampleResponse);
        return Ok(result);
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    // GET /api/admin/rule-packs/{id}/export?tenantId=1
    [HttpGet("{id:int}/export")]
    public async Task<IActionResult> ExportPack(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var pack = await _packs.GetPackWithRulesAsync(EffectiveTenantId(tenantId), id, ct);
        if (pack is null) return NotFound();

        var export = new RulePackExport
        {
            Name = pack.Name,
            Description = pack.Description,
            Version = pack.Version,
            Priority = pack.Priority,
            IsMandatory = pack.IsMandatory,
            AppliesToJson = pack.AppliesToJson,
            ActivationCondition = pack.ActivationCondition,
            MaxEvaluationMs = pack.MaxEvaluationMs,
            Rules = pack.Rules.OrderBy(r => r.OrderInPack).Select(r => new RulePackExport.RuleExport
            {
                OrderInPack = r.OrderInPack,
                HookPoint = r.HookPoint,
                RuleType = r.RuleType,
                Pattern = r.Pattern,
                Instruction = r.Instruction,
                Replacement = r.Replacement,
                ToolName = r.ToolName,
                StopOnMatch = r.StopOnMatch,
                MaxEvaluationMs = r.MaxEvaluationMs,
                MatchTarget = r.MatchTarget,
            }).ToList(),
        };

        return Ok(export);
    }

    // POST /api/admin/rule-packs/import?tenantId=1
    [HttpPost("import")]
    public async Task<IActionResult> ImportPack(
        [FromBody] RulePackExport import,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var dto = new CreateRulePackDto(
            import.Name, import.Description, null,
            import.Priority, import.IsMandatory,
            import.AppliesToJson, import.ActivationCondition,
            null, import.MaxEvaluationMs);

        var pack = await _packs.CreatePackAsync(tid, dto, ct);

        foreach (var rule in import.Rules)
        {
            var ruleDto = new CreateHookRuleDto(
                rule.HookPoint, rule.RuleType, rule.Pattern,
                rule.Instruction, rule.Replacement, rule.ToolName,
                rule.OrderInPack, rule.StopOnMatch, rule.MaxEvaluationMs,
                rule.MatchTarget);
            await _packs.AddRuleAsync(tid, pack.Id, ruleDto, ct);
        }

        return CreatedAtAction(nameof(GetPack), new { id = pack.Id, tenantId = tid }, pack);
    }

    // ── Phase 17: AI Suggestions ──────────────────────────────────────────────

    // POST /api/admin/rule-packs/suggest-regex
    [HttpPost("suggest-regex")]
    public async Task<IActionResult> SuggestRegex(
        [FromBody] RegexSuggestionRequestDto request,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        if (request is null) return BadRequest("Request body required.");
        if (request.IntentDescription?.Length > 500)
            return BadRequest("IntentDescription exceeds 500 character limit.");
        if (request.SampleMatches?.Length > 20)
            return BadRequest("Too many sample matches (max 20).");
        if (request.SampleNonMatches?.Length > 20)
            return BadRequest("Too many sample non-matches (max 20).");

        var ctx = HttpContext.TryGetTenantContext();
        var tid = ctx is { TenantId: > 0 } ? ctx.TenantId : EffectiveTenantId(tenantId);

        var suggestion = await _assistant.SuggestRegexAsync(request, tid, ct);
        return Ok(suggestion);
    }

    // GET /api/admin/rule-packs/{packId}/history?tenantId=1
    [HttpGet("{packId:int}/history")]
    public async Task<IActionResult> GetPackHistory(
        int packId,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var history = await _assistant.GetRulePackHistoryAsync(packId, tid, ct);
        return Ok(history);
    }

    // POST /api/admin/rule-packs/{packId}/history/{version}/restore?tenantId=1
    [HttpPost("{packId:int}/history/{version:int}/restore")]
    public async Task<IActionResult> RestorePackVersion(
        int packId,
        int version,
        [FromBody] RestoreRulePackVersionRequestDto request,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var ctx = HttpContext.TryGetTenantContext();
        var actor = ctx?.UserEmail ?? "unknown";

        var restored = await _assistant.RestoreRulePackVersionAsync(packId, tid, version, request.Reason, actor, ct);
        if (restored is null) return NotFound($"Rule pack history version {version} not found for pack {packId}.");
        return Ok(restored);
    }

    // GET /api/admin/rule-packs/meta — matrix used by UI dropdowns
    [HttpGet("meta")]
    public IActionResult GetMeta()
        => Ok(new
        {
            hookPoints = RulePackRuleCompatibility.Allowed.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderBy(rt => rt).ToArray()),
            matrixMarkdown = RulePackRuleCompatibility.AsMarkdownTable(),
        });
}

// ── Request/Response DTOs ─────────────────────────────────────────────────────

public record ClonePackRequest(string NewName);

public class RulePackExport
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Version { get; set; } = "1.0";
    public int Priority { get; set; } = 100;
    public bool IsMandatory { get; set; }
    public string? AppliesToJson { get; set; }
    public string? ActivationCondition { get; set; }
    public int MaxEvaluationMs { get; set; } = 500;
    public List<RuleExport> Rules { get; set; } = [];

    public class RuleExport
    {
        public int OrderInPack { get; set; }
        public string HookPoint { get; set; } = "";
        public string RuleType { get; set; } = "";
        public string? Pattern { get; set; }
        public string? Instruction { get; set; }
        public string? Replacement { get; set; }
        public string? ToolName { get; set; }
        public bool StopOnMatch { get; set; }
        public int MaxEvaluationMs { get; set; } = 100;
        public string MatchTarget { get; set; } = "query";
    }
}
