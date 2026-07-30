using System.Collections.Concurrent;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Extensions;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IAgentSetupAssistant = Diva.Core.Models.IAgentSetupAssistant;

namespace Diva.Infrastructure.Optimization;

public sealed class AgentOptimizationService : IAgentOptimizationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionAnalyzer _analyzer;
    private readonly IOptimizationLlmAnalyzer _llmAnalyzer;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly AgentOptions _opts;
    private readonly ILogger<AgentOptimizationService> _logger;
    private readonly ConcurrentDictionary<string, int> _activeRuns = new();

    public AgentOptimizationService(
        IServiceScopeFactory scopeFactory,
        ISessionAnalyzer analyzer,
        IOptimizationLlmAnalyzer llmAnalyzer,
        IHostApplicationLifetime appLifetime,
        IOptions<AgentOptions> opts,
        ILogger<AgentOptimizationService> logger)
    {
        _scopeFactory = scopeFactory;
        _analyzer = analyzer;
        _llmAnalyzer = llmAnalyzer;
        _appLifetime = appLifetime;
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<int> StartRunAsync(
        string agentId, int tenantId, TriggerOptimizationRequest request, string triggeredBy, CancellationToken ct)
    {
        var key = $"{tenantId}:{agentId}";
        if (!_activeRuns.TryAdd(key, -1))
            throw new InvalidOperationException("An optimization run is already in progress for this agent.");

        var from = request.From ?? DateTime.UtcNow.AddDays(-30);
        var to = request.To ?? DateTime.UtcNow;

        int runId;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
            var run = new AgentOptimizationRunEntity
            {
                TenantId = tenantId,
                AgentId = agentId,
                SessionId = request.SessionId,
                Status = "running",
                TriggerSource = triggeredBy,
                FromDate = from,
                ToDate = to
            };
            db.OptimizationRuns.Add(run);
            await db.SaveChangesAsync(ct);
            runId = run.Id;
            _activeRuns[key] = runId;
        }

        _ = Task.Run(
            async () => await RunPipelineAsync(runId, agentId, tenantId, request, from, to, _appLifetime.ApplicationStopping),
            CancellationToken.None);

        return runId;
    }

    private async Task RunPipelineAsync(
        int runId, string agentId, int tenantId, TriggerOptimizationRequest request,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var key = $"{tenantId}:{agentId}";
        try
        {
            SessionAnalysisReport report = request.SessionId is not null
                ? await _analyzer.AnalyzeSessionAsync(request.SessionId, agentId, tenantId, ct)
                : await _analyzer.AnalyzeAggregateAsync(agentId, tenantId, from, to, ct);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

            var agentDef = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.Id == agentId && a.TenantId == tenantId, ct);

            List<OptimizationSuggestionDto> suggestions = [];
            if (agentDef is not null)
                suggestions = await _llmAnalyzer.AnalyzeAsync(report, agentDef, request.UserContext, ct);

            var run = await db.OptimizationRuns.FirstAsync(r => r.Id == runId, ct);
            run.Status = "completed";
            run.CompletedAt = DateTime.UtcNow;
            run.SessionsAnalyzed = report.TotalSessions;
            run.TurnsAnalyzed = report.TotalTurns;
            run.ReportJson = JsonSerializer.Serialize(report);

            foreach (var s in suggestions)
            {
                db.OptimizationSuggestions.Add(new AgentOptimizationSuggestionEntity
                {
                    TenantId = tenantId,
                    RunId = runId,
                    AgentId = agentId,
                    Type = s.Type,
                    FieldName = s.FieldName,
                    CurrentValue = s.CurrentValue,
                    SuggestedValue = s.SuggestedValue,
                    Confidence = s.Confidence,
                    Reasoning = s.Reasoning,
                    Status = "Pending"
                });
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Optimization run {RunId} completed: {Suggestions} suggestions", runId, suggestions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Optimization run {RunId} failed", runId);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
                var run = await db.OptimizationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
                if (run is not null)
                {
                    run.Status = "failed";
                    run.CompletedAt = DateTime.UtcNow;
                    run.ErrorMessage = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception inner) { _logger.LogWarning(inner, "Failed to update run {RunId} status to failed", runId); }
        }
        finally
        {
            _activeRuns.TryRemove(key, out _);
        }
    }

    public int? GetActiveRunId(string agentId, int tenantId)
    {
        var key = $"{tenantId}:{agentId}";
        return _activeRuns.TryGetValue(key, out var id) && id > 0 ? id : null;
    }

    // ── Query methods ─────────────────────────────────────────────────────────

    public async Task<List<OptimizationRunSummary>> GetRunsAsync(string agentId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        return await db.OptimizationRuns
            .Where(r => r.AgentId == agentId && r.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new OptimizationRunSummary
            {
                Id = r.Id,
                AgentId = r.AgentId,
                SessionId = r.SessionId,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                TriggerSource = r.TriggerSource,
                SessionsAnalyzed = r.SessionsAnalyzed,
                TurnsAnalyzed = r.TurnsAnalyzed,
                SuggestionCount = r.Suggestions.Count
            })
            .ToListAsync(ct);
    }

    public async Task<PagedResult<OptimizationRunSummary>> GetRunsPagedAsync(string agentId, int tenantId, int page, int pageSize, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        return await db.OptimizationRuns
            .Where(r => r.AgentId == agentId && r.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new OptimizationRunSummary
            {
                Id = r.Id,
                AgentId = r.AgentId,
                SessionId = r.SessionId,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                TriggerSource = r.TriggerSource,
                SessionsAnalyzed = r.SessionsAnalyzed,
                TurnsAnalyzed = r.TurnsAnalyzed,
                SuggestionCount = r.Suggestions.Count
            })
            .ToPagedResultAsync(page, pageSize, ct);
    }

    public async Task<List<OptimizationRunSummary>> GetRunsBySessionAsync(string sessionId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        return await db.OptimizationRuns
            .Where(r => r.SessionId == sessionId && r.TenantId == tenantId)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new OptimizationRunSummary
            {
                Id = r.Id,
                AgentId = r.AgentId,
                SessionId = r.SessionId,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                Status = r.Status,
                TriggerSource = r.TriggerSource,
                SessionsAnalyzed = r.SessionsAnalyzed,
                TurnsAnalyzed = r.TurnsAnalyzed,
                SuggestionCount = r.Suggestions.Count
            })
            .ToListAsync(ct);
    }

    public async Task<OptimizationRunDetail?> GetRunDetailAsync(int runId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var run = await db.OptimizationRuns
            .Include(r => r.Suggestions)
            .FirstOrDefaultAsync(r => r.Id == runId && r.TenantId == tenantId, ct);
        if (run is null) return null;

        SessionAnalysisReport? report = null;
        if (run.ReportJson is not null)
        {
            try { report = JsonSerializer.Deserialize<SessionAnalysisReport>(run.ReportJson); }
            catch { /* ignore deserialize errors */ }
        }

        return new OptimizationRunDetail
        {
            Id = run.Id,
            AgentId = run.AgentId,
            SessionId = run.SessionId,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Status = run.Status,
            TriggerSource = run.TriggerSource,
            SessionsAnalyzed = run.SessionsAnalyzed,
            TurnsAnalyzed = run.TurnsAnalyzed,
            SuggestionCount = run.Suggestions.Count,
            Report = report,
            ErrorMessage = run.ErrorMessage,
            Suggestions = run.Suggestions.Select(MapSuggestion).ToList()
        };
    }

    public async Task<PagedResult<OptimizationSuggestionDto>> GetSuggestionsAsync(
        string agentId, int tenantId,
        string? status = null, string? type = null,
        int? runId = null, float minConfidence = 0f,
        int page = 1, int pageSize = 25,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var q = db.OptimizationSuggestions.Where(s => s.AgentId == agentId && s.TenantId == tenantId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(s => s.Status == status);
        if (!string.IsNullOrEmpty(type)) q = q.Where(s => s.Type == type);
        if (runId.HasValue) q = q.Where(s => s.RunId == runId.Value);
        if (minConfidence > 0f) q = q.Where(s => s.Confidence >= minConfidence);
        var paged = await q.OrderByDescending(s => s.CreatedAt).ToPagedResultAsync(page, pageSize, ct);
        return paged.MapItems(MapSuggestion);
    }

    public async Task<string> MergePromptAsync(
        string agentId, int tenantId, int[] suggestionIds, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

        var agentDef = await db.AgentDefinitions
            .FirstOrDefaultAsync(a => a.Id == agentId && a.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Agent {agentId} not found");

        var suggestions = await db.OptimizationSuggestions
            .Where(s => suggestionIds.Contains(s.Id) && s.TenantId == tenantId
                && (s.Type == "SystemPromptImprovement" || s.Type == "ToolStrategyHint"))
            .ToListAsync(ct);

        if (suggestions.Count == 0) return agentDef.SystemPrompt ?? "";

        return await _llmAnalyzer.MergePromptAsync(
            agentDef.SystemPrompt ?? "",
            suggestions.Select(s => s.SuggestedValue).ToList(),
            agentDef,
            ct);
    }

    public async Task ApplyMergedAsync(
        string agentId, int tenantId, string mergedPrompt, int[] suggestionIds, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var assistant = scope.ServiceProvider.GetRequiredService<IAgentSetupAssistant>();

        var agentDef = await db.AgentDefinitions
            .FirstOrDefaultAsync(a => a.Id == agentId && a.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Agent {agentId} not found");

        agentDef.SystemPrompt = mergedPrompt;

        var suggestions = await db.OptimizationSuggestions
            .Where(s => suggestionIds.Contains(s.Id) && s.TenantId == tenantId)
            .ToListAsync(ct);
        foreach (var s in suggestions) s.Status = "Applied";

        await db.SaveChangesAsync(ct);
        await assistant.SavePromptVersionAsync(
            agentId, tenantId, mergedPrompt,
            source: "optimization", reason: null, createdBy: "optimization", ct);
    }

    // ── Review actions ────────────────────────────────────────────────────────

    public async Task ApproveSuggestionAsync(int suggestionId, int tenantId, string reviewedBy, string? notes, CancellationToken ct)
        => await SetSuggestionStatusAsync(suggestionId, tenantId, "Approved", reviewedBy, notes, ct);

    public async Task RejectSuggestionAsync(int suggestionId, int tenantId, string reviewedBy, string? notes, CancellationToken ct)
        => await SetSuggestionStatusAsync(suggestionId, tenantId, "Rejected", reviewedBy, notes, ct);

    private async Task SetSuggestionStatusAsync(
        int id, int tenantId, string status, string reviewedBy, string? notes, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var s = await db.OptimizationSuggestions
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Suggestion {id} not found");
        s.Status = status;
        s.ReviewedBy = reviewedBy;
        s.ReviewNotes = notes;
        s.ReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ApplySuggestionAsync(int suggestionId, int tenantId, string applyMode, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var applicator = scope.ServiceProvider.GetRequiredService<OptimizationApplicator>();

        var suggestion = await db.OptimizationSuggestions
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Suggestion {suggestionId} not found");

        if (suggestion.Status != "Approved")
            throw new InvalidOperationException("Only Approved suggestions can be applied.");

        var agentDef = await db.AgentDefinitions
            .FirstOrDefaultAsync(a => a.Id == suggestion.AgentId && a.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Agent {suggestion.AgentId} not found");

        await applicator.ApplyAsync(suggestion, agentDef, db, applyMode, ct);
    }

    // ── Schedule ──────────────────────────────────────────────────────────────

    public async Task<OptimizationScheduleConfig?> GetScheduleAsync(string agentId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var config = await db.OptimizationConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId && c.TenantId == tenantId, ct);
        if (config is null) return null;
        return MapSchedule(config);
    }

    public async Task SaveScheduleAsync(string agentId, int tenantId, OptimizationScheduleConfig dto, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var config = await db.OptimizationConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId && c.TenantId == tenantId, ct);
        if (config is null)
        {
            config = new AgentOptimizationConfigEntity { TenantId = tenantId, AgentId = agentId };
            db.OptimizationConfigs.Add(config);
        }
        config.ScheduleType = dto.ScheduleType;
        config.RunAtTime = dto.RunAtTime;
        config.RunOnDayOfWeek = dto.RunOnDayOfWeek;
        config.Timezone = dto.Timezone;
        config.IsEnabled = dto.IsEnabled;
        config.NextRunAt = ComputeNextRunAt(dto);
        await db.SaveChangesAsync(ct);
    }

    // ── Few-shot examples ─────────────────────────────────────────────────────

    public async Task<List<FewShotExampleDto>> GetFewShotExamplesAsync(string agentId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        return await db.FewShotExamples
            .Where(e => e.AgentId == agentId && e.TenantId == tenantId)
            .OrderBy(e => e.SortOrder)
            .Select(e => MapExample(e))
            .ToListAsync(ct);
    }

    public async Task<int> AddFewShotExampleAsync(string agentId, int tenantId, FewShotExampleDto dto, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var entity = new FewShotExampleEntity
        {
            TenantId = tenantId,
            AgentId = agentId,
            SourceSessionId = dto.SourceSessionId,
            SourceTurnNumber = dto.SourceTurnNumber,
            UserMessage = dto.UserMessage,
            AssistantMessage = dto.AssistantMessage,
            Description = dto.Description,
            SortOrder = dto.SortOrder,
            IsEnabled = dto.IsEnabled,
            CreatedBy = dto.CreatedBy
        };
        db.FewShotExamples.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task DeleteFewShotExampleAsync(int exampleId, int tenantId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var entity = await db.FewShotExamples
            .FirstOrDefaultAsync(e => e.Id == exampleId && e.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Example {exampleId} not found");
        db.FewShotExamples.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderFewShotExamplesAsync(string agentId, int tenantId, int[] orderedIds, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
        var examples = await db.FewShotExamples
            .Where(e => e.AgentId == agentId && e.TenantId == tenantId)
            .ToListAsync(ct);
        for (var i = 0; i < orderedIds.Length; i++)
        {
            var ex = examples.FirstOrDefault(e => e.Id == orderedIds[i]);
            if (ex is not null) ex.SortOrder = i;
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static OptimizationSuggestionDto MapSuggestion(AgentOptimizationSuggestionEntity s) => new()
    {
        Id = s.Id,
        RunId = s.RunId,
        AgentId = s.AgentId,
        Type = s.Type,
        FieldName = s.FieldName,
        CurrentValue = s.CurrentValue,
        SuggestedValue = s.SuggestedValue,
        Confidence = s.Confidence,
        Reasoning = s.Reasoning,
        Status = s.Status,
        ReviewedBy = s.ReviewedBy,
        ReviewNotes = s.ReviewNotes,
        ReviewedAt = s.ReviewedAt,
        CreatedAt = s.CreatedAt
    };

    private static FewShotExampleDto MapExample(FewShotExampleEntity e) => new()
    {
        Id = e.Id,
        AgentId = e.AgentId,
        SourceSessionId = e.SourceSessionId,
        SourceTurnNumber = e.SourceTurnNumber,
        UserMessage = e.UserMessage,
        AssistantMessage = e.AssistantMessage,
        Description = e.Description,
        SortOrder = e.SortOrder,
        IsEnabled = e.IsEnabled,
        CreatedAt = e.CreatedAt,
        CreatedBy = e.CreatedBy
    };

    private static OptimizationScheduleConfig MapSchedule(AgentOptimizationConfigEntity c) => new()
    {
        ScheduleType = c.ScheduleType,
        RunAtTime = c.RunAtTime,
        RunOnDayOfWeek = c.RunOnDayOfWeek,
        Timezone = c.Timezone,
        IsEnabled = c.IsEnabled,
        NextRunAt = c.NextRunAt,
        LastScheduledRunAt = c.LastScheduledRunAt
    };

    internal static DateTime? ComputeNextRunAt(OptimizationScheduleConfig config)
    {
        if (config.ScheduleType == "manual" || !config.IsEnabled) return null;
        if (string.IsNullOrEmpty(config.RunAtTime)) return null;

        if (!TimeOnly.TryParse(config.RunAtTime, out var runTime)) return null;

        var tz = TryGetTimeZone(config.Timezone);
        var now = tz is not null ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz) : DateTime.UtcNow;
        var candidate = now.Date.Add(runTime.ToTimeSpan());
        if (candidate <= now) candidate = candidate.AddDays(1);

        if (config.ScheduleType == "weekly" && config.RunOnDayOfWeek.HasValue)
        {
            var target = (DayOfWeek)config.RunOnDayOfWeek.Value;
            while (candidate.DayOfWeek != target)
                candidate = candidate.AddDays(1);
        }

        return tz is not null
            ? TimeZoneInfo.ConvertTimeToUtc(candidate, tz)
            : DateTime.SpecifyKind(candidate, DateTimeKind.Utc);
    }

    private static TimeZoneInfo? TryGetTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }
}
