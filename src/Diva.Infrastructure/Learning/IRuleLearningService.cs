using Diva.Infrastructure.LiteLLM;

namespace Diva.Infrastructure.Learning;

public interface IRuleLearningService
{
    Task<List<SuggestedRule>> ExtractRulesFromConversationAsync(
        string sessionId, string conversationTranscript, CancellationToken ct,
        ResolvedLlmConfig? agentConfig = null);

    Task<RuleSaveResult> SaveLearnedRuleAsync(
        int tenantId, SuggestedRule rule, RuleApprovalMode mode, CancellationToken ct);

    Task<List<SuggestedRule>> GetPendingRulesAsync(int tenantId, CancellationToken ct);

    Task ApproveRuleAsync(int tenantId, int ruleId, string reviewedBy, CancellationToken ct);
    Task RejectRuleAsync(int tenantId, int ruleId, string reviewedBy, string notes, CancellationToken ct);
}

public sealed class SuggestedRule
{
    /// <summary>
    /// Real <c>LearnedRuleEntity.Id</c> once persisted. 0 for rules not yet saved (e.g. still
    /// being reviewed inside <see cref="IRuleLearningService.ExtractRulesFromConversationAsync"/>
    /// before a save decision is made).
    /// </summary>
    public int Id { get; init; }
    public string? AgentType { get; init; }
    public string RuleCategory { get; init; } = "";
    public string RuleKey { get; init; } = "";
    public string PromptInjection { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public float Confidence { get; init; }
    public DateTime SuggestedAt { get; init; } = DateTime.UtcNow;
}

public sealed class RuleSaveResult
{
    public bool Success { get; init; }
    public RuleApprovalMode Mode { get; init; }
    public int? RuleId { get; init; }   // null for SessionOnly
    public string Message { get; init; } = "";
}

public enum RuleApprovalMode { AutoApprove, RequireAdmin, SessionOnly }
