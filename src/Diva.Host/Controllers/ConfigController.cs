using Diva.Core.Configuration;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.LiteLLM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diva.Host.Controllers;

[ApiController]
[Route("api/config")]
public class ConfigController : ControllerBase
{
    private readonly LlmOptions _llm;
    private readonly AgentOptions _agent;
    private readonly VerificationOptions _verification;
    private readonly ILlmConfigResolver _resolver;

    public ConfigController(
        IOptions<LlmOptions> llmOptions,
        IOptions<AgentOptions> agentOptions,
        IOptions<VerificationOptions> verificationOptions,
        ILlmConfigResolver resolver)
    {
        _llm = llmOptions.Value;
        _agent = agentOptions.Value;
        _verification = verificationOptions.Value;
        _resolver = resolver;
    }

    // GET /api/config/llm?llmConfigId=N
    // When llmConfigId is provided, resolves that specific named config so the
    // test-agent model picker shows the correct provider's models.
    [HttpGet("llm")]
    public async Task<IActionResult> GetLlmConfig(
        [FromQuery] int? llmConfigId = null,
        CancellationToken ct = default)
    {
        if (llmConfigId is > 0)
        {
            var tenantCtx = HttpContext.TryGetTenantContext();
            var tenantId = tenantCtx?.TenantId ?? 0;
            var resolved = await _resolver.ResolveAsync(tenantId, llmConfigId, null, ct);
            var resolvedModels = resolved.AvailableModels.Count > 0
                ? resolved.AvailableModels
                : (resolved.Model is not null ? [resolved.Model] : (IReadOnlyList<string>)[]);
            return Ok(new
            {
                availableModels = resolvedModels,
                currentProvider = resolved.Provider,
                defaultModel = resolved.Model,
            });
        }

        // No specific config requested — resolve the platform default (first platform config or appsettings fallback)
        var tenantCtx2 = HttpContext.TryGetTenantContext();
        var tid2 = tenantCtx2?.TenantId ?? 0;
        var defaultResolved = await _resolver.ResolveAsync(tid2, null, null, ct);
        var defaultModels = defaultResolved.AvailableModels.Count > 0
            ? defaultResolved.AvailableModels
            : (defaultResolved.Model is not null ? [defaultResolved.Model] : (IReadOnlyList<string>)[]);

        return Ok(new
        {
            availableModels = defaultModels,
            currentProvider = defaultResolved.Provider,
            defaultModel = defaultResolved.Model,
        });
    }

    // GET /api/config/agent-defaults
    [HttpGet("agent-defaults")]
    public IActionResult GetAgentDefaults()
    {
        return Ok(new
        {
            maxIterations = _agent.MaxIterations,
            maxContinuations = _agent.MaxContinuations,
            defaultTemperature = _agent.DefaultTemperature,
            maxToolResultChars = _agent.MaxToolResultChars,
            maxOutputTokens = _agent.MaxOutputTokens,
            enableHistoryCaching = _agent.EnableHistoryCaching,
            thinkingBudgetTokens = _agent.ThinkingBudgetTokens,
            injectToolStrategy = _agent.InjectToolStrategy,
            verificationMode = _verification.Mode,
            confidenceThreshold = _verification.ConfidenceThreshold,
            maxVerificationRetries = _verification.MaxVerificationRetries,
            contextWindow = new
            {
                budgetTokens = _agent.ContextWindow.BudgetTokens,
                compactionThreshold = _agent.ContextWindow.CompactionThreshold,
                keepLastRawMessages = _agent.ContextWindow.KeepLastRawMessages,
                maxHistoryTurns = _agent.ContextWindow.MaxHistoryTurns,
            },
            retry = new
            {
                maxRetries = _agent.Retry.MaxRetries,
                baseDelayMs = _agent.Retry.BaseDelayMs,
            },
        });
    }
}
