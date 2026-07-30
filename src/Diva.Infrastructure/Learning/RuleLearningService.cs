using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Learning;

/// <summary>
/// Orchestrates rule extraction, approval routing, and DB persistence.
/// Singleton-safe: uses IDatabaseProviderFactory for each DB operation.
/// </summary>
public sealed class RuleLearningService : IRuleLearningService
{
    private readonly LlmRuleExtractor _extractor;
    private readonly ISessionRuleManager _sessionRules;
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<RuleLearningService> _logger;

    public RuleLearningService(
        LlmRuleExtractor extractor,
        ISessionRuleManager sessionRules,
        IDatabaseProviderFactory db,
        ILogger<RuleLearningService> logger)
    {
        _extractor = extractor;
        _sessionRules = sessionRules;
        _db = db;
        _logger = logger;
    }

    public Task<List<SuggestedRule>> ExtractRulesFromConversationAsync(
        string sessionId, string conversationTranscript, CancellationToken ct,
        ResolvedLlmConfig? agentConfig = null)
        => _extractor.ExtractAsync(conversationTranscript, sessionId, ct, agentConfig);

    public async Task<RuleSaveResult> SaveLearnedRuleAsync(
        int tenantId, SuggestedRule rule, RuleApprovalMode mode, CancellationToken ct)
    {
        if (mode == RuleApprovalMode.SessionOnly)
        {
            await _sessionRules.AddRuleAsync(rule.SourceSessionId, rule, ct);
            _logger.LogInformation("Rule saved to session {SessionId}: {Key}", rule.SourceSessionId, rule.RuleKey);
            return new RuleSaveResult { Success = true, Mode = mode, Message = "Applied to current session only" };
        }

        using var db = _db.CreateDbContext();

        var entity = new LearnedRuleEntity
        {
            TenantId = tenantId,
            AgentType = rule.AgentType,
            RuleCategory = rule.RuleCategory,
            RuleKey = rule.RuleKey,
            PromptInjection = rule.PromptInjection,
            Confidence = rule.Confidence,
            Status = mode == RuleApprovalMode.AutoApprove ? "approved" : "pending",
            SourceSessionId = rule.SourceSessionId
        };

        db.LearnedRules.Add(entity);
        await db.SaveChangesAsync(ct);

        if (mode == RuleApprovalMode.AutoApprove)
        {
            await PromoteToBusinessRuleAsync(db, tenantId, entity, ct);
            _logger.LogInformation("Rule auto-approved and promoted: {Key} (tenant={TenantId})", rule.RuleKey, tenantId);
        }
        else
        {
            _logger.LogInformation("Rule saved for admin review: {Key} (tenant={TenantId})", rule.RuleKey, tenantId);
        }

        return new RuleSaveResult { Success = true, Mode = mode, RuleId = entity.Id };
    }

    public async Task<List<SuggestedRule>> GetPendingRulesAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.LearnedRules
            .Where(r => r.TenantId == tenantId && r.Status == "pending")
            .OrderByDescending(r => r.LearnedAt)
            .Select(r => new SuggestedRule
            {
                Id = r.Id,
                AgentType = r.AgentType,
                RuleCategory = r.RuleCategory ?? "",
                RuleKey = r.RuleKey ?? "",
                PromptInjection = r.PromptInjection ?? "",
                Confidence = (float)r.Confidence,
                SourceSessionId = r.SourceSessionId ?? "",
                SuggestedAt = r.LearnedAt
            })
            .ToListAsync(ct);
    }

    public async Task ApproveRuleAsync(int tenantId, int ruleId, string reviewedBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.LearnedRules
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ruleId, ct)
            ?? throw new InvalidOperationException($"Rule {ruleId} not found for tenant {tenantId}");

        entity.Status = "approved";
        entity.ReviewedBy = reviewedBy;
        entity.ReviewedAt = DateTime.UtcNow;

        await PromoteToBusinessRuleAsync(db, tenantId, entity, ct);
        _logger.LogInformation("Rule {RuleId} approved by {ReviewedBy}", ruleId, reviewedBy);
    }

    public async Task RejectRuleAsync(int tenantId, int ruleId, string reviewedBy, string notes, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.LearnedRules
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ruleId, ct)
            ?? throw new InvalidOperationException($"Rule {ruleId} not found for tenant {tenantId}");

        entity.Status = "rejected";
        entity.ReviewedBy = reviewedBy;
        entity.ReviewNotes = notes;
        entity.ReviewedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Rule {RuleId} rejected by {ReviewedBy}", ruleId, reviewedBy);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task PromoteToBusinessRuleAsync(
        DivaDbContext db, int tenantId, LearnedRuleEntity entity, CancellationToken ct)
    {
        db.BusinessRules.Add(new TenantBusinessRuleEntity
        {
            TenantId = tenantId,
            AgentType = entity.AgentType ?? "*",
            RuleCategory = entity.RuleCategory ?? "learned",
            RuleKey = entity.RuleKey ?? $"learned_{entity.Id}",
            PromptInjection = entity.PromptInjection,
            IsActive = true
        });
        await db.SaveChangesAsync(ct);
    }
}
