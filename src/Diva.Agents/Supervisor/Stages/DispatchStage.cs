using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Agents.Supervisor.Stages;

/// <summary>
/// Executes each agent from the dispatch plan. Runs sub-tasks in parallel when there are multiple.
/// Collects WorkerResults on the state (used by IntegrateStage and future VerifyStage).
/// </summary>
public sealed class DispatchStage : ISupervisorPipelineStage
{
    private readonly ILogger<DispatchStage> _logger;
    private readonly AgentOptions _agentOptions;

    public DispatchStage(ILogger<DispatchStage> logger, IOptions<AgentOptions> agentOptions)
    {
        _logger = logger;
        _agentOptions = agentOptions.Value;
    }

    public async Task<SupervisorState> ExecuteAsync(SupervisorState state, CancellationToken ct)
    {
        var results = new List<AgentResponse>();
        var lockObj = new object();
        var timeoutSec = _agentOptions.SubAgentTimeoutSeconds;

        await Parallel.ForEachAsync(state.DispatchPlan, ct, async (plan, innerCt) =>
        {
            var (task, agent) = plan;
            var agentId = agent.GetCapability().AgentId;

            _logger.LogInformation("Dispatching to agent {AgentId}: {Query}",
                agentId, task.Description);

            // Pass a fresh request with no SessionId — supervisor owns the session.
            // ParentSessionId links the worker's trace session back to the supervisor.
            // ConversationContext gives the worker a condensed summary of the supervisor's
            // conversation history so it can access previously collected user inputs.
            var subRequest = new AgentRequest
            {
                Query = task.Description,
                TriggerType = state.Request.TriggerType,
                Metadata = state.Request.Metadata,
                Instructions = task.Instructions,
                ParentSessionId = state.SessionId,
                ConversationContext = BuildConversationContext(state),
            };

            AgentResponse result;
            try
            {
                using var subCts = timeoutSec > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(innerCt)
                    : null;
                subCts?.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                var effectiveCt = subCts?.Token ?? innerCt;

                result = await agent.ExecuteAsync(subRequest, state.TenantContext, effectiveCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Sub-agent timed out — treat as individual failure, let other agents continue
                _logger.LogWarning(
                    "Sub-agent {AgentId} timed out after {TimeoutSec}s — recording as failure",
                    agentId, timeoutSec);
                result = new AgentResponse
                {
                    Success = false,
                    Content = $"Sub-agent '{agentId}' timed out after {timeoutSec}s.",
                    AgentName = agentId,
                };
            }

            _logger.LogInformation("Agent {AgentId} completed: success={Success}, tools={Tools}",
                agentId, result.Success, string.Join(", ", result.ToolsUsed));

            lock (lockObj)
                results.Add(result);
        });

        state.WorkerResults = results;

        // Accumulate all tool evidence from worker results for the VerifyStage
        state.ToolEvidence = string.Join("\n\n", results
            .Where(r => !string.IsNullOrEmpty(r.ToolEvidence))
            .Select(r => $"[Agent: {r.AgentName}]\n{r.ToolEvidence}"));

        return state;
    }

    /// <summary>
    /// Builds a condensed, read-only conversation context string from the supervisor's
    /// session history. Caps the number of turns to avoid inflating the worker's context
    /// window. Only includes the last N user/assistant turns.
    /// </summary>
    private static string? BuildConversationContext(SupervisorState state)
    {
        if (state.SessionHistory.Count == 0)
            return null;

        const int maxTurns = 10; // last 10 messages (5 user+assistant pairs)
        var recent = state.SessionHistory.TakeLast(maxTurns).ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Conversation Context (from supervisor session)");
        sb.AppendLine("The following is a summary of the conversation so far. Use it to understand what the user has already provided.");
        sb.AppendLine();

        foreach (var turn in recent)
        {
            var role = turn.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
            sb.AppendLine($"**{role}:** {turn.Content}");
        }

        return sb.ToString().TrimEnd();
    }
}
