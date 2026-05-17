using Diva.Agents.Registry;
using Diva.Agents.Workers;
using Diva.Core.Models;
using Diva.Infrastructure.Sessions;
using Diva.Rag.Abstractions;

namespace Diva.Agents.Supervisor;

/// <summary>
/// Mutable state bag passed through the supervisor pipeline stages.
/// </summary>
public sealed class SupervisorState
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public AgentRequest Request { get; init; } = null!;
    public TenantContext TenantContext { get; init; } = null!;

    // Session (loaded by SupervisorAgent before pipeline starts)
    public string SessionId { get; set; } = "";
    public List<ConversationTurn> SessionHistory { get; set; } = [];

    /// <summary>
    /// Pre-fetched by AgentContextStage. Eliminates duplicate DB round trips between
    /// DecomposeStage (LLM context) and CapabilityMatchStage (routing). Empty = not yet fetched.
    /// </summary>
    public List<IWorkerAgent> AvailableAgents { get; set; } = [];

    /// <summary>
    /// Set by OrchestratorAgent (Phase 19) to restrict the pipeline to a specific sub-agent set.
    /// When non-null, AgentContextStage uses this instead of the global registry.
    /// </summary>
    public IReadableAgentRegistry? ScopedRegistry { get; set; }

    /// <summary>
    /// Resolved LLM provider context for supervisor-level calls (decompose, synthesis).
    /// Set by OrchestratorAgent (Phase 19) from the coordinator agent's resolved LlmConfig.
    /// Null = use global platform defaults — backward compatible.
    /// </summary>
    public SupervisorLlmOverride? LlmOverride { get; set; }

    // Set by DecomposeStage
    public List<SubTask> SubTasks { get; set; } = [];

    // Set by CapabilityMatchStage
    public List<(SubTask Task, IWorkerAgent Agent)> DispatchPlan { get; set; } = [];

    // Set by DispatchStage / MonitorStage
    public List<AgentResponse> WorkerResults { get; set; } = [];

    // Set by IntegrateStage
    public string IntegratedResult { get; set; } = "";

    // Accumulated by DispatchStage from WorkerResults (passed to VerifyStage)
    public string ToolEvidence { get; set; } = "";

    // Set by VerifyStage (Phase 13)
    public VerificationResult? Verification { get; set; }

    // Set by DeliverStage
    public bool DeliveryComplete { get; set; }

    public SupervisorStatus Status { get; set; } = SupervisorStatus.Running;
    public string? ErrorMessage { get; set; }

    /// <summary>Instructions from the caller/API to propagate to all sub-tasks and worker agents.</summary>
    public string? SupervisorInstructions { get; set; }

    // ── Phase 26: RAG Context ────────────────────────────────────────────
    /// <summary>Assembled context from vector search, injected into decompose/dispatch.</summary>
    public string? RetrievedContext { get; set; }
    /// <summary>Raw retrieved chunks for grounding and citation.</summary>
    public IReadOnlyList<RetrievedChunk>? RetrievedChunks { get; set; }
}

/// <summary>A unit of work to dispatch to a single worker agent.</summary>
public sealed record SubTask(
    string Description,
    string[] RequiredCapabilities,
    int SiteId,
    int TenantId,
    string? Instructions = null);

public enum SupervisorStatus { Running, Completed, Failed }
