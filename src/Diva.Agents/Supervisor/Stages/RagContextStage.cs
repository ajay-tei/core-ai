using Diva.Rag;
using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Agents.Supervisor.Stages;

/// <summary>
/// Pre-fetches RAG context for the user query and populates state.RetrievedContext/RetrievedChunks.
/// Placed between AgentContextStage and DecomposeStage so downstream stages have context available.
/// Guard: only fires when RagOptions.Enabled = true.
/// </summary>
public sealed class RagContextStage(
    IKnowledgeRetriever retriever,
    IOptions<RagOptions> opts,
    ILogger<RagContextStage> logger) : ISupervisorPipelineStage
{
    private readonly RagOptions _opts = opts.Value;

    public async Task<SupervisorState> ExecuteAsync(SupervisorState state, CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            logger.LogDebug("RagContextStage: skipped (RAG disabled)");
            return state;
        }

        try
        {
            var filter = new KnowledgeFilter
            {
                TenantId = state.TenantContext.TenantId,
                TopK = _opts.DefaultTopK,
                MinScore = _opts.DefaultMinScore,
            };

            var result = await retriever.RetrieveAsync(state.Request.Query, filter, ct);

            if (result.Chunks.Count > 0)
            {
                state.RetrievedContext = result.AssembledContext;
                state.RetrievedChunks = result.Chunks;

                logger.LogInformation(
                    "RagContextStage: retrieved {Count} chunks for tenant {TenantId}",
                    result.Chunks.Count, state.TenantContext.TenantId);
            }
            else
            {
                logger.LogDebug("RagContextStage: no relevant chunks found");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — the pipeline continues without RAG context
            logger.LogWarning(ex, "RagContextStage: retrieval failed, continuing without RAG context");
        }

        return state;
    }
}
