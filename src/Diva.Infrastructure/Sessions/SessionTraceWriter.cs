using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Optimization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Sessions;

/// <summary>
/// Writes agent execution trace data to the dedicated sessions-trace.db database.
/// One instance per HTTP request (Scoped).
///
/// Usage pattern:
///   1. EnsureSessionAsync()        — on request start
///   2. CaptureChunk()              — for each SSE chunk (in-memory only, no DB writes)
///   3. FlushTurnAsync()            — after SaveTurnAsync completes (bulk DB write)
///   4. CompleteSessionAsync()      — when HTTP request ends
///
/// All DB operations are wrapped in try/catch — failures are logged but never propagate
/// to the main execution path.
/// </summary>
public sealed class SessionTraceWriter
{
    private readonly SessionTraceDbContext _db;
    private readonly ILogger<SessionTraceWriter> _logger;

    // ── Per-turn in-memory buffer ─────────────────────────────────────────────
    private readonly List<IterationBuffer> _iterations = [];
    private IterationBuffer? _currentIteration;
    private int _continuationWindow = 1;
    private bool _lastWasCorrection;
    private string? _verificationMode;
    private bool? _verificationPassed;
    private long _executionTimeMs;
    private int _totalCacheRead;
    private int _totalCacheCreation;
    private int _totalInputTokens;
    private int _totalOutputTokens;
    private string? _activeModelId;
    private string? _activeProvider;

    // Pending delegations from a2a_delegation_start events (keyed by tool name)
    private readonly Dictionary<string, DelegationBuffer> _pendingDelegations = [];

    // Session-level info (set by EnsureSessionAsync)
    private string _sessionId = string.Empty;
    private string _agentId = string.Empty;
    private string _agentName = string.Empty;

    private readonly ITurnScoringService _scoringService;
    private readonly AgentOptions _opts;
    private readonly IHostApplicationLifetime _appLifetime;

    public SessionTraceWriter(
        SessionTraceDbContext db,
        ITurnScoringService scoringService,
        IOptions<AgentOptions> opts,
        IHostApplicationLifetime appLifetime,
        ILogger<SessionTraceWriter> logger)
    {
        _db = db;
        _scoringService = scoringService;
        _opts = opts.Value;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures a TraceSession row exists for this session. Idempotent — safe to call multiple times.
    /// </summary>
    public async Task EnsureSessionAsync(
        string sessionId,
        string? parentSessionId,
        TenantContext tenant,
        string agentId,
        string agentName,
        bool isSupervisor,
        CancellationToken ct)
    {
        _sessionId = sessionId;
        _agentId = agentId;
        _agentName = agentName;

        try
        {
            var exists = await _db.TraceSessions.AnyAsync(s => s.SessionId == sessionId, ct);
            if (!exists)
            {
                _db.TraceSessions.Add(new TraceSessionEntity
                {
                    SessionId = sessionId,
                    ParentSessionId = parentSessionId,
                    TenantId = tenant.TenantId,
                    AgentId = agentId,
                    AgentName = agentName,
                    UserId = tenant.UserId ?? "anonymous",
                    IsSupervisor = isSupervisor,
                    Status = "active",
                });
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionTraceWriter.EnsureSessionAsync failed for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Captures an SSE chunk into the in-memory buffer. No DB writes here.
    /// </summary>
    public void CaptureChunk(AgentStreamChunk chunk)
    {
        try
        {
            switch (chunk.Type)
            {
                case "iteration_start":
                    FinalizeCurrentIteration();
                    _currentIteration = new IterationBuffer
                    {
                        IterationNumber = chunk.Iteration ?? (_iterations.Count + 1),
                        ContinuationWindow = _continuationWindow,
                        IsCorrection = _lastWasCorrection,
                        ModelId = _activeModelId,
                        Provider = _activeProvider,
                    };
                    _lastWasCorrection = false;
                    break;

                case "thinking":
                    if (_currentIteration is not null)
                        _currentIteration.ThinkingText = chunk.Content;
                    break;

                case "plan":
                case "plan_revised":
                    if (_currentIteration is not null)
                        _currentIteration.PlanText = chunk.PlanText;
                    break;

                case "tool_call":
                    if (_currentIteration is not null && !string.IsNullOrEmpty(chunk.ToolName))
                    {
                        var toolBuf = new ToolCallBuffer
                        {
                            ToolName = chunk.ToolName,
                            ToolInput = chunk.ToolInput,
                            Sequence = _currentIteration.ToolCalls.Count + 1,
                        };
                        _currentIteration.ToolCalls.Add(toolBuf);
                        _currentIteration.HadToolCalls = true;
                    }
                    break;

                case "a2a_delegation_start":
                    if (!string.IsNullOrEmpty(chunk.ToolName) && !string.IsNullOrEmpty(chunk.A2ATaskId))
                    {
                        _pendingDelegations[chunk.ToolName] = new DelegationBuffer
                        {
                            A2ATaskId = chunk.A2ATaskId,
                            AgentId = chunk.DelegatedAgentId,
                            AgentName = chunk.DelegatedAgentName,
                            IterationId = 0, // filled in at flush time
                        };
                        // Update the current pending tool call buffer with delegation info
                        var pendingTool = _currentIteration?.ToolCalls.LastOrDefault(t => t.ToolName == chunk.ToolName);
                        if (pendingTool is not null)
                        {
                            pendingTool.A2ATaskId = chunk.A2ATaskId;
                            pendingTool.DelegatedAgentId = chunk.DelegatedAgentId;
                            pendingTool.DelegatedAgentName = chunk.DelegatedAgentName;
                        }
                    }
                    break;

                case "tool_result":
                    if (_currentIteration is not null && !string.IsNullOrEmpty(chunk.ToolName))
                    {
                        var tool = _currentIteration.ToolCalls.LastOrDefault(t => t.ToolName == chunk.ToolName && t.ToolOutput is null);
                        if (tool is not null)
                            tool.ToolOutput = chunk.ToolOutput;
                    }
                    break;

                case "model_switch":
                    if (_currentIteration is not null)
                    {
                        _currentIteration.HadModelSwitch = true;
                        _currentIteration.FromModel = chunk.FromModel;
                        _currentIteration.ToModel = chunk.ToModel;
                        _currentIteration.ModelSwitchReason = chunk.Reason;
                    }
                    _activeModelId = chunk.ToModel;
                    _activeProvider = chunk.ToProvider;
                    break;

                case "token_usage":
                    if (_currentIteration is not null)
                    {
                        _currentIteration.InputTokens = chunk.IterationInputTokens ?? 0;
                        _currentIteration.OutputTokens = chunk.IterationOutputTokens ?? 0;
                        _currentIteration.CacheReadTokens = chunk.IterationCacheRead ?? 0;
                        _currentIteration.CacheCreationTokens = chunk.IterationCacheCreation ?? 0;
                    }
                    _totalInputTokens = chunk.TotalInputTokens ?? _totalInputTokens;
                    _totalOutputTokens = chunk.TotalOutputTokens ?? _totalOutputTokens;
                    _totalCacheRead = chunk.TotalCacheRead ?? _totalCacheRead;
                    _totalCacheCreation = chunk.TotalCacheCreation ?? _totalCacheCreation;
                    break;

                case "correction":
                    _lastWasCorrection = true;
                    break;

                case "continuation_start":
                    _continuationWindow++;
                    break;

                case "verification":
                    if (chunk.Verification is not null)
                    {
                        _verificationMode = chunk.Verification.Mode;
                        _verificationPassed = chunk.Verification.IsVerified;
                    }
                    break;

                case "done":
                    // Parse execution time — format is e.g. "4.8s" or "1m 2.3s"
                    if (!string.IsNullOrEmpty(chunk.ExecutionTime))
                        _executionTimeMs = ParseExecutionTimeMs(chunk.ExecutionTime);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionTraceWriter.CaptureChunk failed for type {Type}", chunk.Type);
        }
    }

    /// <summary>
    /// Bulk-writes all buffered iteration/tool data for a completed turn.
    /// Called immediately after AgentSessionService.SaveTurnAsync.
    /// </summary>
    public async Task FlushTurnAsync(
        string sessionId,
        int turnNumber,
        string userMessage,
        string assistantMessage,
        long executionTimeMs,
        string agentId,
        string modelId,
        string provider,
        CancellationToken ct)
    {
        try
        {
            FinalizeCurrentIteration();

            var effectiveExecMs = executionTimeMs > 0 ? executionTimeMs : _executionTimeMs;

            // Compute turn-level totals
            var turnInputTokens = _iterations.Sum(i => i.InputTokens);
            var turnOutputTokens = _iterations.Sum(i => i.OutputTokens);
            var turnCacheRead = _iterations.Sum(i => i.CacheReadTokens);
            var turnCacheCreation = _iterations.Sum(i => i.CacheCreationTokens);
            var totalToolCalls = _iterations.Sum(i => i.ToolCalls.Count);
            var delegations = _iterations.SelectMany(i => i.ToolCalls).Count(t => t.IsAgentDelegation);

            // Insert turn
            var turn = new TraceSessionTurnEntity
            {
                SessionId = sessionId,
                TurnNumber = turnNumber,
                UserMessage = userMessage,
                AssistantMessage = assistantMessage,
                AgentId = agentId,
                ModelId = modelId,
                Provider = provider,
                TotalIterations = _iterations.Count,
                TotalToolCalls = totalToolCalls,
                ContinuationWindows = _continuationWindow,
                VerificationMode = _verificationMode,
                VerificationPassed = _verificationPassed,
                ExecutionTimeMs = effectiveExecMs,
                TotalInputTokens = turnInputTokens,
                TotalOutputTokens = turnOutputTokens,
                CacheReadTokens = turnCacheRead,
                CacheCreationTokens = turnCacheCreation,
                CompletedAt = DateTime.UtcNow,
            };
            _db.TraceSessionTurns.Add(turn);
            await _db.SaveChangesAsync(ct);  // need TurnId before inserting iterations

            // Insert iterations + tool calls
            var delegationRows = new List<TraceDelegationChainEntity>();
            foreach (var itBuf in _iterations)
            {
                var iteration = new TraceIterationEntity
                {
                    SessionId = sessionId,
                    TurnId = turn.Id,
                    TurnNumber = turnNumber,
                    IterationNumber = itBuf.IterationNumber,
                    ContinuationWindow = itBuf.ContinuationWindow,
                    ThinkingText = itBuf.ThinkingText,
                    PlanText = itBuf.PlanText,
                    ModelId = itBuf.ModelId,
                    Provider = itBuf.Provider,
                    HadToolCalls = itBuf.HadToolCalls,
                    HadModelSwitch = itBuf.HadModelSwitch,
                    FromModel = itBuf.FromModel,
                    ToModel = itBuf.ToModel,
                    ModelSwitchReason = itBuf.ModelSwitchReason,
                    InputTokens = itBuf.InputTokens,
                    OutputTokens = itBuf.OutputTokens,
                    CacheReadTokens = itBuf.CacheReadTokens,
                    CacheCreationTokens = itBuf.CacheCreationTokens,
                    IsCorrection = itBuf.IsCorrection,
                };
                _db.TraceIterations.Add(iteration);
                await _db.SaveChangesAsync(ct);  // need IterationId before tool calls

                foreach (var tcBuf in itBuf.ToolCalls)
                {
                    var isDelegation = tcBuf.IsAgentDelegation ||
                        (tcBuf.ToolName?.StartsWith("call_agent_", StringComparison.OrdinalIgnoreCase) ?? false);

                    _db.TraceToolCalls.Add(new TraceToolCallEntity
                    {
                        SessionId = sessionId,
                        IterationId = iteration.Id,
                        TurnId = turn.Id,
                        TurnNumber = turnNumber,
                        IterationNumber = itBuf.IterationNumber,
                        Sequence = tcBuf.Sequence,
                        ToolName = tcBuf.ToolName ?? "",
                        ToolInput = tcBuf.ToolInput ?? "",
                        ToolOutput = tcBuf.ToolOutput ?? "",
                        IsAgentDelegation = isDelegation,
                        DelegatedAgentId = tcBuf.DelegatedAgentId,
                        DelegatedAgentName = tcBuf.DelegatedAgentName,
                        LinkedA2ATaskId = tcBuf.A2ATaskId,
                    });

                    if (isDelegation && !string.IsNullOrEmpty(tcBuf.A2ATaskId))
                    {
                        // Parse agent info from tool name "call_agent_{name}_{id}" if not already set
                        var (parsedId, parsedName) = ParseAgentFromToolName(tcBuf.ToolName ?? "");
                        delegationRows.Add(new TraceDelegationChainEntity
                        {
                            CallerSessionId = sessionId,
                            CallerIterationId = iteration.Id,
                            ChildA2ATaskId = tcBuf.A2ATaskId,
                            ChildAgentId = tcBuf.DelegatedAgentId ?? parsedId,
                            ChildAgentName = tcBuf.DelegatedAgentName ?? parsedName,
                            Depth = 1, // TODO: propagate actual depth
                            Query = ExtractDelegationQuery(tcBuf.ToolInput),
                            Status = "working",
                        });
                    }
                }
            }

            if (delegationRows.Count > 0)
                _db.TraceDelegationChain.AddRange(delegationRows);

            // Update TraceSessions aggregate counts
            var session = await _db.TraceSessions.FindAsync([sessionId], ct);
            if (session is not null)
            {
                // Derive a human-readable title from the first user question so the session
                // is identifiable in history lists. Only set once (on the first turn).
                if (string.IsNullOrWhiteSpace(session.Title))
                    session.Title = DeriveSessionTitle(userMessage);

                session.TotalTurns++;
                session.TotalIterations += _iterations.Count;
                session.TotalToolCalls += totalToolCalls;
                session.TotalDelegations += delegations;
                session.TotalInputTokens += turnInputTokens;
                session.TotalOutputTokens += turnOutputTokens;
                session.TotalCacheReadTokens += turnCacheRead;
                session.TotalCacheCreationTokens += turnCacheCreation;
                session.LastActivityAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            // Fire-and-forget per-turn LLM scoring (Phase 24)
            if (_opts.Optimization.EnablePerTurnScoring)
            {
                var toolEvidence = BuildToolEvidence(_iterations);
                var capturedSessionId = sessionId;
                var capturedAgentId = agentId;
                var capturedTurn = turnNumber;
                var capturedUser = userMessage;
                var capturedAssistant = assistantMessage;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _scoringService.ScoreTurnAsync(
                            capturedSessionId, capturedTurn, capturedAgentId,
                            capturedUser, capturedAssistant, toolEvidence,
                            _appLifetime.ApplicationStopping);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Turn scoring failed for session {S}", capturedSessionId);
                    }
                }, CancellationToken.None);
            }

            ResetBuffer();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionTraceWriter.FlushTurnAsync failed for session {SessionId}", sessionId);
            ResetBuffer();
        }
    }

    private static string BuildToolEvidence(List<IterationBuffer> iterations)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var iter in iterations)
        {
            foreach (var tc in iter.ToolCalls)
            {
                if (!string.IsNullOrEmpty(tc.ToolOutput))
                {
                    sb.Append(tc.ToolName).Append(": ").AppendLine(
                        tc.ToolOutput.Length > 200 ? tc.ToolOutput[..200] + "..." : tc.ToolOutput);
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Updates the session status to "completed" or "failed" when the HTTP request ends.
    /// Does NOT set completed after each turn — session stays "active" between turns.
    /// </summary>
    public async Task CompleteSessionAsync(string sessionId, string status, CancellationToken ct)
    {
        try
        {
            var session = await _db.TraceSessions.FindAsync([sessionId], ct);
            if (session is not null && session.Status != "deleted")
            {
                session.Status = status;
                session.LastActivityAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionTraceWriter.CompleteSessionAsync failed for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Updates a delegation chain row when the A2A task finishes.
    /// Also sets ChildSessionId so the trace tree can link caller → worker session.
    /// </summary>
    public async Task UpdateDelegationStatusAsync(
        string childA2ATaskId,
        string status,
        string? childSessionId,
        CancellationToken ct)
    {
        try
        {
            var row = await _db.TraceDelegationChain
                .FirstOrDefaultAsync(d => d.ChildA2ATaskId == childA2ATaskId, ct);
            if (row is not null)
            {
                row.Status = status;
                row.ChildSessionId = childSessionId;
                if (status is "completed" or "failed")
                    row.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionTraceWriter.UpdateDelegationStatusAsync failed for task {TaskId}", childA2ATaskId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void FinalizeCurrentIteration()
    {
        if (_currentIteration is not null)
        {
            _iterations.Add(_currentIteration);
            _currentIteration = null;
        }
    }

    private void ResetBuffer()
    {
        _iterations.Clear();
        _currentIteration = null;
        _continuationWindow = 1;
        _lastWasCorrection = false;
        _verificationMode = null;
        _verificationPassed = null;
        _executionTimeMs = 0;
        _pendingDelegations.Clear();
    }

    private static string? Truncate(string? s, int maxLen) =>
        s is null ? null : (s.Length <= maxLen ? s : s[..maxLen]);

    private static long ParseExecutionTimeMs(string executionTime)
    {
        // Format examples: "4.8s", "1m 2.3s", "320ms"
        try
        {
            if (executionTime.EndsWith("ms"))
                return (long)double.Parse(executionTime[..^2]);
            if (executionTime.Contains('m'))
            {
                var parts = executionTime.Split('m', ' ');
                var mins = double.Parse(parts[0]);
                var secs = parts.Length > 1 ? double.Parse(parts[^1].TrimEnd('s')) : 0;
                return (long)((mins * 60 + secs) * 1000);
            }
            if (executionTime.EndsWith("s"))
                return (long)(double.Parse(executionTime[..^1]) * 1000);
        }
        catch { /* ignore parse errors */ }
        return 0;
    }

    private static (string Id, string Name) ParseAgentFromToolName(string toolName)
    {
        // Tool name format: "call_agent_{name}_{id}"
        if (!toolName.StartsWith("call_agent_", StringComparison.OrdinalIgnoreCase))
            return ("", toolName);
        var rest = toolName["call_agent_".Length..];
        var lastUnderscore = rest.LastIndexOf('_');
        if (lastUnderscore < 0) return (rest, rest);
        return (rest[(lastUnderscore + 1)..], rest[..lastUnderscore]);
    }

    private static string ExtractDelegationQuery(string? toolInput)
    {
        if (string.IsNullOrEmpty(toolInput)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(toolInput);
            if (doc.RootElement.TryGetProperty("query", out var q))
                return q.GetString() ?? "";
        }
        catch { /* not JSON */ }
        return Truncate(toolInput, 500) ?? "";
    }

    /// <summary>
    /// Builds a short, single-line session title from the first user question.
    /// Collapses whitespace and truncates to ~80 chars so it fits in history lists.
    /// </summary>
    private static string DeriveSessionTitle(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return "New conversation";
        var collapsed = System.Text.RegularExpressions.Regex.Replace(userMessage.Trim(), @"\s+", " ");
        return collapsed.Length > 80 ? collapsed[..80].TrimEnd() + "…" : collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Inner buffer types
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class IterationBuffer
    {
        public int IterationNumber { get; set; }
        public int ContinuationWindow { get; set; }
        public bool IsCorrection { get; set; }
        public string? ThinkingText { get; set; }
        public string? PlanText { get; set; }
        public string? ModelId { get; set; }
        public string? Provider { get; set; }
        public bool HadToolCalls { get; set; }
        public bool HadModelSwitch { get; set; }
        public string? FromModel { get; set; }
        public string? ToModel { get; set; }
        public string? ModelSwitchReason { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CacheReadTokens { get; set; }
        public int CacheCreationTokens { get; set; }
        public List<ToolCallBuffer> ToolCalls { get; set; } = [];
    }

    private sealed class ToolCallBuffer
    {
        public int Sequence { get; set; }
        public string? ToolName { get; set; }
        public string? ToolInput { get; set; }
        public string? ToolOutput { get; set; }
        public bool IsAgentDelegation { get; set; }
        public string? A2ATaskId { get; set; }
        public string? DelegatedAgentId { get; set; }
        public string? DelegatedAgentName { get; set; }
    }

    private sealed class DelegationBuffer
    {
        public string A2ATaskId { get; set; } = string.Empty;
        public string? AgentId { get; set; }
        public string? AgentName { get; set; }
        public int IterationId { get; set; }
    }
}
