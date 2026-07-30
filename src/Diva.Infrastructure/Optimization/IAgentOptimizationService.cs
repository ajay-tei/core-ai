using Diva.Core.Models;

namespace Diva.Infrastructure.Optimization;

public interface IAgentOptimizationService
{
    Task<int> StartRunAsync(string agentId, int tenantId, TriggerOptimizationRequest request, string triggeredBy, CancellationToken ct);
    int? GetActiveRunId(string agentId, int tenantId);
    Task<List<OptimizationRunSummary>> GetRunsAsync(string agentId, int tenantId, CancellationToken ct);
    Task<PagedResult<OptimizationRunSummary>> GetRunsPagedAsync(string agentId, int tenantId, int page, int pageSize, CancellationToken ct);
    Task<List<OptimizationRunSummary>> GetRunsBySessionAsync(string sessionId, int tenantId, CancellationToken ct);
    Task<OptimizationRunDetail?> GetRunDetailAsync(int runId, int tenantId, CancellationToken ct);
    Task<PagedResult<OptimizationSuggestionDto>> GetSuggestionsAsync(
        string agentId, int tenantId,
        string? status = null, string? type = null,
        int? runId = null, float minConfidence = 0f,
        int page = 1, int pageSize = 25,
        CancellationToken ct = default);
    Task ApproveSuggestionAsync(int suggestionId, int tenantId, string reviewedBy, string? notes, CancellationToken ct);
    Task RejectSuggestionAsync(int suggestionId, int tenantId, string reviewedBy, string? notes, CancellationToken ct);
    Task ApplySuggestionAsync(int suggestionId, int tenantId, string applyMode, CancellationToken ct);
    Task<string> MergePromptAsync(string agentId, int tenantId, int[] suggestionIds, CancellationToken ct);
    Task ApplyMergedAsync(string agentId, int tenantId, string mergedPrompt, int[] suggestionIds, CancellationToken ct);
    Task<OptimizationScheduleConfig?> GetScheduleAsync(string agentId, int tenantId, CancellationToken ct);
    Task SaveScheduleAsync(string agentId, int tenantId, OptimizationScheduleConfig config, CancellationToken ct);

    Task<List<FewShotExampleDto>> GetFewShotExamplesAsync(string agentId, int tenantId, CancellationToken ct);
    Task<int> AddFewShotExampleAsync(string agentId, int tenantId, FewShotExampleDto example, CancellationToken ct);
    Task DeleteFewShotExampleAsync(int exampleId, int tenantId, CancellationToken ct);
    Task ReorderFewShotExamplesAsync(string agentId, int tenantId, int[] orderedIds, CancellationToken ct);
}
