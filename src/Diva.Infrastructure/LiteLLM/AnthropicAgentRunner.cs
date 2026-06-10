using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Context;
using Diva.Infrastructure.Learning;
using Diva.Infrastructure.Sessions;
using Diva.Infrastructure.Verification;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

[assembly: InternalsVisibleTo("Diva.Agents.Tests")]

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Runs a dynamic agent from an AgentDefinitionEntity with MCP tool support.
///
/// Provider routing:
///   "Anthropic"    → AnthropicProviderStrategy (native SDK)
///   everything else → OpenAiProviderStrategy (raw IChatClient — LM Studio, Ollama, LiteLLM…)
///
/// Both providers share a single unified ReAct loop via <see cref="ILlmProviderStrategy"/>.
/// </summary>
public sealed class AnthropicAgentRunner : IAgentRunner
{
    private const string MaxTokensNudgePrompt =
        "Your previous response was cut off before completing. If you intended to call a tool, " +
        "please call it now — keep your preamble text short and emit the tool call directly.";

    private const string ToolStrategyBlock =
        "\n\n## Tool use\n" +
        "CRITICAL: You MUST call multiple independent tools in the SAME response turn — do NOT call one tool, wait for the result, then call the next. " +
        "Before calling any tool: (1) identify ALL data you need upfront, " +
        "(2) issue ALL independent tool calls together in a single response (parallel execution), " +
        "(3) only after receiving those results, call any dependent tools in the next turn, " +
        "(4) never repeat a tool call with identical parameters already in evidence, " +
        "(5) do not narrate or describe what you are about to do — just call the tools, " +
        "(6) if generating many tool calls would exceed your output limit, batch them 3-5 per turn and continue with the remainder in the next turn.";

    private const string MultiItemTaskBlock =
        "\n\n## Multi-item tasks\n" +
        "When the task contains a list of N items requiring the same action (locations, recipients, records, sites): " +
        "(1) do NOT declare the task complete after processing only one item — process EVERY item before emitting your final response, " +
        "(2) for independent items, call their tools together in a single turn (parallel execution), " +
        "(3) track progress explicitly: 'Processed 3/N… 5/N… N/N — all done', " +
        "(4) only emit your final response once ALL N items have been handled, " +
        "(5) if a subset fails, report which succeeded and which failed — do not stop the entire batch.";

    private readonly LlmOptions _llmOptions;
    private readonly AgentOptions _agentOpts;
    private readonly AgentSessionService _sessions;
    private readonly ResponseVerifier _verifier;
    private readonly IRuleLearningService _ruleLearner;
    private readonly RuleLearningOptions _ruleLearningOpts;
    private readonly VerificationOptions _verificationOpts;
    private readonly McpClientCache _mcpCache;
    private readonly IAnthropicProvider _anthropic;
    private readonly IOpenAiProvider _openAi;
    private readonly IContextWindowManager _ctx;
    private readonly ILlmConfigResolver? _resolver;
    private readonly IPromptBuilder? _promptBuilder;
    private readonly IAgentHookPipeline? _hookPipeline;
    private readonly IReActHookCoordinator? _hookCoordinator;
    private readonly ModelSwitchCoordinator? _modelSwitchCoordinator;
    private readonly IArchetypeRegistry? _archetypeRegistry;
    private readonly IMcpConnectionManager _mcpConnector;
    private readonly ToolExecutor _toolExecutor;
    private readonly AgentToolProvider? _agentToolProvider;
    private readonly AgentToolExecutor? _agentToolExecutor;
    private readonly ILogger<AnthropicAgentRunner> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IToolSelectionStrategy? _toolSelector;

    public AnthropicAgentRunner(
        IOptions<LlmOptions> llmOptions,
        IOptions<AgentOptions> agentOptions,
        IOptions<VerificationOptions> verificationOptions,
        AgentSessionService sessions,
        ResponseVerifier verifier,
        IRuleLearningService ruleLearner,
        McpClientCache mcpCache,
        IAnthropicProvider anthropic,
        IOpenAiProvider openAi,
        IContextWindowManager ctx,
        IHttpContextAccessor httpCtx,
        ToolExecutor toolExecutor,
        ILogger<AnthropicAgentRunner> logger,
        ILlmConfigResolver? resolver = null,
        ICredentialResolver? credentialResolver = null,
        IPromptBuilder? promptBuilder = null,
        IAgentHookPipeline? hookPipeline = null,
        IArchetypeRegistry? archetypeRegistry = null,
        IMcpConnectionManager? mcpConnector = null,
        IReActHookCoordinator? hookCoordinator = null,
        AgentToolProvider? agentToolProvider = null,
        AgentToolExecutor? agentToolExecutor = null,
        IServiceScopeFactory? scopeFactory = null,
        IToolSelectionStrategy? toolSelector = null)
    {
        _llmOptions = llmOptions.Value;
        _agentOpts = agentOptions.Value;
        _ruleLearningOpts = agentOptions.Value.RuleLearning;
        _verificationOpts = verificationOptions.Value;
        _sessions = sessions;
        _verifier = verifier;
        _ruleLearner = ruleLearner;
        _mcpCache = mcpCache;
        _anthropic = anthropic;
        _openAi = openAi;
        _ctx = ctx;
        _resolver = resolver;
        _toolExecutor = toolExecutor;
        _promptBuilder = promptBuilder;
        _hookPipeline = hookPipeline;
        _hookCoordinator = hookCoordinator ?? (hookPipeline is not null ? new ReActHookCoordinator(hookPipeline, NullLogger<ReActHookCoordinator>.Instance) : null);
        _modelSwitchCoordinator = new ModelSwitchCoordinator(anthropic, openAi, ctx, resolver, logger);
        _archetypeRegistry = archetypeRegistry;
        _mcpConnector = mcpConnector ?? new McpConnectionManager(httpCtx, credentialResolver, NullLogger<McpConnectionManager>.Instance);
        _agentToolProvider = agentToolProvider;
        _agentToolExecutor = agentToolExecutor;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _toolSelector = toolSelector;
    }

    public async Task<AgentResponse> RunAsync(
        AgentDefinitionEntity definition,
        AgentRequest request,
        TenantContext tenant,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Starting agent {AgentName} for tenant {TenantId}",
            definition.Name, tenant.TenantId);

        // Materialize InvokeStreamAsync — single code path for both streaming and non-streaming
        string content = "";
        string? errorMessage = null;
        string sessionId = request.SessionId ?? "";
        var toolsUsed = new List<string>();
        VerificationResult? verification = null;
        var evidenceParts = new List<string>();
        bool success = true;
        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int iterationCount = 0;

        await foreach (var chunk in InvokeStreamAsync(definition, request, tenant, ct))
        {
            switch (chunk.Type)
            {
                case "final_response":
                    content = chunk.Content ?? "";
                    sessionId = chunk.SessionId ?? sessionId;
                    break;
                case "verification":
                    verification = chunk.Verification;
                    break;
                case "tool_call":
                    if (!string.IsNullOrEmpty(chunk.ToolName))
                        toolsUsed.Add(chunk.ToolName);
                    break;
                case "tool_result":
                    if (!string.IsNullOrEmpty(chunk.ToolName) && !string.IsNullOrEmpty(chunk.ToolOutput))
                        evidenceParts.Add($"[Tool: {chunk.ToolName}]\n{chunk.ToolOutput}");
                    break;
                case "error":
                    success = false;
                    errorMessage = chunk.ErrorMessage;
                    break;
                case "done":
                    sessionId = chunk.SessionId ?? sessionId;
                    break;
                case "token_usage":
                    totalInputTokens = chunk.TotalInputTokens ?? totalInputTokens;
                    totalOutputTokens = chunk.TotalOutputTokens ?? totalOutputTokens;
                    break;
                case "iteration_start":
                    if (chunk.Iteration.HasValue && chunk.Iteration.Value > iterationCount)
                        iterationCount = chunk.Iteration.Value;
                    break;
            }
        }

        sw.Stop();
        return new AgentResponse
        {
            Success = success,
            Content = content,
            ErrorMessage = errorMessage,
            AgentName = definition.Name,
            SessionId = sessionId,
            ToolsUsed = toolsUsed.Distinct().ToList(),
            ExecutionTime = sw.Elapsed,
            ToolEvidence = string.Join("\n\n", evidenceParts),
            Verification = verification,
            InputTokens = totalInputTokens,
            OutputTokens = totalOutputTokens,
            IterationCount = iterationCount,
        };
    }

    // ── Streaming ReAct loop — yields AgentStreamChunk per event ─────────────
    public async IAsyncEnumerable<AgentStreamChunk> InvokeStreamAsync(
        AgentDefinitionEntity definition,
        AgentRequest request,
        TenantContext tenant,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Streaming agent {AgentName} for tenant {TenantId}",
            definition.Name, tenant.TenantId);

        var (sessionId, history) = await _sessions.GetOrCreateAsync(
            request.SessionId, definition.Id, tenant, ct);

        // Enrich tenant context with the resolved session ID so {{session_id}} resolves correctly.
        tenant = tenant.WithSession(sessionId);

        // ── Session trace setup (separate sessions-trace.db) ─────────────────
        await using var traceScope = _scopeFactory?.CreateAsyncScope();
        var traceWriter = traceScope?.ServiceProvider.GetService<SessionTraceWriter>();
        if (traceWriter is not null)
            await traceWriter.EnsureSessionAsync(
                sessionId, request.ParentSessionId, tenant,
                definition.Id, definition.Name, isSupervisor: false, ct);
        var turnState = new SessionTurnState();

        var basePrompt = definition.SystemPrompt
            ?? $"You are {definition.DisplayName}. {definition.Description}";

        // ── Resolve LLM config early (needed to know provider for caching split) ─
        // NOTE: full resolution happens below — this is just a quick provider check.
        bool useAnthropicEarly = (_llmOptions.DirectProvider.Provider)
            .Equals("Anthropic", StringComparison.OrdinalIgnoreCase);

        // Per-agent override wins; falls through to global AgentOptions default.
        bool enableHistoryCaching = definition.EnableHistoryCaching ?? _agentOpts.EnableHistoryCaching;

        // ── System prompt build — split static/dynamic for Anthropic caching ─────
        string staticSystemPrompt;
        string dynamicSystemPrompt = string.Empty;

        if (useAnthropicEarly && enableHistoryCaching && _promptBuilder is not null)
        {
            // Split build: static (stable, cached BP1) + dynamic (volatile per session).
            (staticSystemPrompt, dynamicSystemPrompt) = await _promptBuilder.BuildPartsAsync(
                basePrompt, definition.ArchetypeId ?? "general", tenant, ct,
                definition.CustomVariablesJson, agentId: definition.Id);
        }
        else
        {
            staticSystemPrompt = _promptBuilder is not null
                ? await _promptBuilder.BuildAsync(basePrompt, definition.ArchetypeId ?? "general", tenant, ct,
                    definition.CustomVariablesJson, agentId: definition.Id)
                : basePrompt;
        }

        // Combined prompt used for compaction, continuation windows, and non-Anthropic paths.
        var systemPrompt = string.IsNullOrEmpty(dynamicSystemPrompt)
            ? staticSystemPrompt
            : staticSystemPrompt + "\n\n" + dynamicSystemPrompt;

        // ── Caller-provided instructions (from supervisor or API) ────────────────
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            var instructions = $"\n\n## Caller Instructions\n{request.Instructions}";
            if (useAnthropicEarly && enableHistoryCaching)
                dynamicSystemPrompt += instructions;   // volatile: goes into dynamic block
            else
                staticSystemPrompt += instructions;
            systemPrompt += instructions;
        }

        // ── Supervisor conversation context (delegated worker requests only) ────
        if (!string.IsNullOrWhiteSpace(request.ConversationContext))
        {
            var ctxBlock = $"\n\n{request.ConversationContext}";
            if (useAnthropicEarly && enableHistoryCaching)
                dynamicSystemPrompt += ctxBlock;   // volatile: changes per turn
            else
                staticSystemPrompt += ctxBlock;
            systemPrompt += ctxBlock;
        }

        // ── Resolve LLM config (platform → group → tenant → per-agent) ────────────
        ResolvedLlmConfig? resolved = null;
        if (_resolver is not null && tenant.TenantId > 0)
        {
            Exception? resolveEx = null;
            // Runtime override (test mode) takes precedence over the agent definition's pinned config
            var configId = request.LlmConfigId ?? definition.LlmConfigId;
            try { resolved = await _resolver.ResolveAsync(tenant.TenantId, configId, request.ModelId ?? definition.ModelId, ct); }
            catch (Exception ex) { resolveEx = ex; }
            if (resolveEx is not null)
                _logger.LogWarning(resolveEx, "LlmConfigResolver failed for tenant {TenantId} — using appsettings fallback.", tenant.TenantId);
        }

        var opts = _llmOptions.DirectProvider;
        var resolvedProvider = resolved?.Provider ?? opts.Provider;
        var resolvedApiKey = resolved?.ApiKey ?? opts.ApiKey;
        // When a named config is resolved, use its endpoint exactly (null = provider's native endpoint,
        // which the Overlay already cleared when the provider changed). Fall back to opts only when
        // there is no resolved config at all (e.g. TenantId=0 or resolver not registered).
        var resolvedEndpoint = resolved is not null ? resolved.Endpoint : opts.Endpoint;
        var effectiveModel = !string.IsNullOrWhiteSpace(request.ModelId) ? request.ModelId
                              : resolved?.Model ?? (!string.IsNullOrWhiteSpace(definition.ModelId) ? definition.ModelId : opts.Model);

        // ── Connect to MCP servers (cached) ──────────────────────────────────────
        // ForwardSsoToMcp: override all HTTP/SSE MCP bindings to forward auth for this request.
        // Does not require tenant.AccessToken to be set — the McpConnectionManager handler reads
        // the Authorization header directly from the HttpContext per tool call.
        bool forwardSso = request.ForwardSsoToMcp;
        string? sslCacheSuffix = forwardSso ? ":sso" : null;
        var mcpClients = await _mcpCache.GetOrConnectAsync(
            definition, ct2 => _mcpConnector.ConnectAsync(definition, ct2, tenant, forwardSso), ct, sslCacheSuffix);

        // ── Inject tool planning strategy when tools are connected ───────────────
        // Guard on our own injected marker to avoid double-injection across continuation windows.
        if (_agentOpts.InjectToolStrategy && mcpClients.Count > 0
            && !staticSystemPrompt.Contains("## Tool use", StringComparison.OrdinalIgnoreCase))
        {
            staticSystemPrompt += ToolStrategyBlock;  // tool strategy is static (same per agent config)
            systemPrompt += ToolStrategyBlock;
            _logger.LogDebug("ToolStrategyBlock injected (agent={Agent})", definition.Name);
        }
        else if (_agentOpts.InjectToolStrategy && mcpClients.Count > 0)
        {
            _logger.LogDebug("ToolStrategyBlock skipped — already present (agent={Agent})", definition.Name);
        }

        // ── Inject multi-item task strategy (opt-out via __disable_multi_item variable) ──
        var earlyVars = AgentHookHelper.MergeVariables(null, definition.CustomVariablesJson);
        bool multiItemDisabled = earlyVars.TryGetValue("__disable_multi_item", out var disableVal)
            && disableVal.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (!multiItemDisabled && mcpClients.Count > 0
            && !staticSystemPrompt.Contains("## Multi-item tasks", StringComparison.OrdinalIgnoreCase))
        {
            staticSystemPrompt += MultiItemTaskBlock;
            systemPrompt += MultiItemTaskBlock;
            _logger.LogDebug("MultiItemTaskBlock injected (agent={Agent})", definition.Name);
        }

        // ── Point B: cross-run history compaction ─────────────────────────────
        var agentWindowOverride = ParseContextWindowOverride(definition, _logger);
        var (compactedHistoryS, historySummaryS) =
            await _ctx.CompactHistoryAsync(history, effectiveModel, agentWindowOverride, ct);
        if (historySummaryS != null)
        {
            var summary = $"\n\n## Earlier session context\n{historySummaryS}";
            if (useAnthropicEarly && enableHistoryCaching)
                dynamicSystemPrompt += summary;   // volatile: history summary changes per session
            else
                staticSystemPrompt += summary;
            systemPrompt += summary;
            history = compactedHistoryS;
        }

        {
            // Build merged tool lookup + raw tool list in a single parallel pass.
            // If a cached MCP session has expired on the server side ("Session ID not found"),
            // evict the dead entry and reconnect once before propagating the failure.
            (Dictionary<string, McpClient> toolClientMap, List<McpClientTool> allMcpTools) toolData;
            try
            {
                toolData = await _mcpConnector.BuildToolDataAsync(mcpClients, ct);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("Session ID not found"))
            {
                _logger.LogWarning(ex, "Stale MCP session detected — evicting cache and reconnecting (agent={Agent})", definition.Name);
                mcpClients = await _mcpCache.EvictAndReconnectAsync(
                    definition, ct2 => _mcpConnector.ConnectAsync(definition, ct2, tenant, forwardSso), ct, sslCacheSuffix);
                toolData = await _mcpConnector.BuildToolDataAsync(mcpClients, ct);
            }
            var (toolClientMap, allMcpTools) = toolData;

            // ── Tool filtering — per-agent allow/deny list ───────────────────────
            ReActToolHelper.FilterTools(definition.ToolFilterJson, toolClientMap, allMcpTools, _logger);

            // ── ExecutionMode enforcement ─────────────────────────────────────
            var executionMode = Enum.TryParse<AgentExecutionMode>(definition.ExecutionMode, true, out var em)
                ? em : AgentExecutionMode.Full;
            ReActToolHelper.ApplyExecutionModeFilter(executionMode, definition.ToolBindings, toolClientMap, allMcpTools, _logger);

            // ── Semantic tool pre-filtering (IToolSelectionStrategy) ──────────────
            if (_toolSelector is not null && allMcpTools.Count > 0)
            {
                var llmOverride = new SupervisorLlmOverride(resolvedProvider, effectiveModel, resolvedEndpoint);
                (allMcpTools, toolClientMap) = await _toolSelector.SelectAsync(
                    request.Query, allMcpTools, toolClientMap, llmOverride, ct);
            }

            _logger.LogInformation(
                "Streaming tools: {ToolCount} tool(s) from {ServerCount} MCP server(s): [{ToolNames}]",
                toolClientMap.Count, mcpClients.Count, string.Join(", ", toolClientMap.Keys));

            yield return new AgentStreamChunk
            {
                Type = "tools_available",
                ToolCount = toolClientMap.Count,
                ToolNames = toolClientMap.Count > 0 ? [.. toolClientMap.Keys] : null,
            };

            // ── Create provider strategy ─────────────────────────────────────────
            bool useAnthropic = resolvedProvider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
            var effectiveMaxOutputTokens = definition.MaxOutputTokens ?? _agentOpts.MaxOutputTokens;

            ILlmProviderStrategy strategy = useAnthropic
                ? new AnthropicProviderStrategy(_anthropic, _ctx, effectiveModel, effectiveMaxOutputTokens,
                    staticSystemPrompt,
                    dynamicSystemPrompt,
                    (call, retCt) => CallWithRetryAsync(call, retCt),
                    enableHistoryCaching: enableHistoryCaching,
                    apiKeyOverride: resolvedApiKey != opts.ApiKey ? resolvedApiKey : null)
                : new OpenAiProviderStrategy(_openAi, _ctx, effectiveModel,
                    (call, retCt) => CallWithRetryAsync(call, retCt),
                    maxOutputTokens: effectiveMaxOutputTokens,
                    apiKeyOverride: resolvedApiKey != opts.ApiKey ? resolvedApiKey : null,
                    endpointOverride: resolvedEndpoint);  // null = provider native; set = custom URL

            // OpenAI strategy receives the combined prompt; Anthropic strategy uses constructor fields.
            strategy.Initialize(systemPrompt, history, request.Query, allMcpTools, request.Attachments);

            // ── Inject agent delegation tools ────────────────────────────────────
            var agentDelegationTools = new List<AgentDelegationTool>();
            if (_agentToolProvider is not null &&
                !string.IsNullOrWhiteSpace(definition.DelegateAgentIdsJson) &&
                definition.DelegateAgentIdsJson.Trim() != "[]")
            {
                agentDelegationTools = await _agentToolProvider.BuildAgentToolsAsync(
                    definition.DelegateAgentIdsJson, tenant.TenantId, definition.Id, ct);
                if (agentDelegationTools.Count > 0)
                {
                    strategy.AddExtraTools(agentDelegationTools);
                    foreach (var aTool in agentDelegationTools)
                        toolClientMap[aTool.Name] = null!;  // sentinel — handled by AgentToolExecutor
                    _logger.LogInformation(
                        "Added {Count} agent delegation tool(s) for agent {Agent}: [{Names}]",
                        agentDelegationTools.Count, definition.Name,
                        string.Join(", ", agentDelegationTools.Select(t => t.Name)));
                }
            }

            // ── Resolve archetype + hooks ────────────────────────────────────────
            var archetype = _archetypeRegistry?.GetById(definition.ArchetypeId ?? "general");
            var customVars = AgentHookHelper.MergeVariables(archetype, definition.CustomVariablesJson, _logger);
            if (!string.IsNullOrWhiteSpace(definition.ModelSwitchingJson))
                customVars["__model_switching_json"] = definition.ModelSwitchingJson;
            var hookCtx = new AgentHookContext
            {
                Request = request,
                Tenant = tenant,
                AgentId = definition.Id,
                ArchetypeId = definition.ArchetypeId ?? "general",
                SessionId = sessionId,
                SystemPrompt = systemPrompt,
                Variables = customVars,
            };
            var mergedHookConfig = AgentHookHelper.MergeHookConfig(archetype?.DefaultHooks, definition.HooksJson, _logger);
            IDisposable? hookScope = null;
            List<IAgentLifecycleHook> hooks;
            if (_hookPipeline is not null && mergedHookConfig.Count > 0)
            {
                var hookResult = _hookPipeline.ResolveHooks(mergedHookConfig, hookCtx.ArchetypeId, tenant);
                hooks = hookResult.Hooks;
                hookScope = hookResult.Scope;
            }
            else
            {
                hooks = [];
            }

            // ── OnInit hooks ─────────────────────────────────────────────────────
            if (_hookCoordinator is not null && hooks.Count > 0)
            {
                var initResult = await _hookCoordinator.RunOnInitAsync(hooks, hookCtx, systemPrompt, sessionId, ct);
                foreach (var chunk in initResult.Chunks)
                    yield return chunk;

                if (initResult.AbortRun)
                {
                    hookScope?.Dispose();
                    hookScope = null;
                    yield break;
                }

                if (initResult.UpdatedSystemPrompt is not null)
                {
                    // Compute what the hook appended so we only update the dynamic block,
                    // preserving BP1 (static block cache key stays stable).
                    var preCombined = systemPrompt;
                    systemPrompt = initResult.UpdatedSystemPrompt;
                    if (useAnthropic && enableHistoryCaching)
                    {
                        dynamicSystemPrompt = systemPrompt.StartsWith(staticSystemPrompt, StringComparison.Ordinal)
                            ? systemPrompt[staticSystemPrompt.Length..].TrimStart('\n')
                            : systemPrompt;   // full replace — store as new dynamic content
                        strategy.UpdateSystemPrompt(dynamicSystemPrompt);
                    }
                    else
                    {
                        strategy.UpdateSystemPrompt(systemPrompt);
                    }
                }
            }

            try
            {
                bool traceError = false;
                await foreach (var chunk in ExecuteReActLoopAsync(
                    strategy, definition, sessionId, request.Query, systemPrompt,
                    staticSystemPrompt, dynamicSystemPrompt, effectiveModel,
                    resolvedProvider, resolvedEndpoint ?? string.Empty, allMcpTools,
                    toolClientMap, mcpClients, agentWindowOverride, hooks, hookCtx,
                    agentDelegationTools, tenant, sw, turnState, resolved, ct))
                {
                    traceWriter?.CaptureChunk(chunk);
                    if (chunk.Type == "error") traceError = true;
                    yield return chunk;
                }
                // Flush trace data for this turn after SaveTurnAsync completed inside the loop
                if (traceWriter is not null && turnState.WasSaved)
                    await traceWriter.FlushTurnAsync(
                        sessionId, turnState.TurnNumber, turnState.UserQuery,
                        turnState.AssistantContent, turnState.ExecutionTimeMs,
                        definition.Id, turnState.ModelId, turnState.Provider, ct);
                if (traceWriter is not null)
                    await traceWriter.CompleteSessionAsync(sessionId,
                        traceError ? "failed" : "completed", ct);
            }
            finally
            {
                hookScope?.Dispose();
            }
        }
    }

    // ── Unified ReAct loop — provider-agnostic via ILlmProviderStrategy ─────
    private async IAsyncEnumerable<AgentStreamChunk> ExecuteReActLoopAsync(
        ILlmProviderStrategy strategy,
        AgentDefinitionEntity definition,
        string sessionId,
        string userQuery,
        string systemPrompt,
        string staticSystemPrompt,
        string dynamicSystemPrompt,
        string effectiveModel,
        string resolvedProvider,
        string resolvedEndpoint,
        List<McpClientTool> allMcpTools,
        Dictionary<string, McpClient> toolClientMap,
        Dictionary<string, McpClient> mcpClients,
        ContextWindowOverrideOptions? agentWindowOverride,
        List<IAgentLifecycleHook> hooks,
        AgentHookContext hookCtx,
        List<AgentDelegationTool> agentDelegationTools,
        TenantContext tenant,
        System.Diagnostics.Stopwatch sw,
        SessionTurnState turnState,
        ResolvedLlmConfig? resolvedLlmConfig,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var toolsUsed = new List<string>();
        var toolEvidence = new List<string>();
        string finalResponse = string.Empty;
        bool streamError = false;

        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int totalCacheRead = 0;
        int totalCacheCreation = 0;

        int maxIterations = definition.MaxIterations > 0 ? definition.MaxIterations : _agentOpts.MaxIterations;
        int maxContinuations = definition.MaxContinuations ?? _agentOpts.MaxContinuations;
        bool enableHistoryCaching = definition.EnableHistoryCaching ?? _agentOpts.EnableHistoryCaching;
        bool completedNaturally = false;
        int iterationBase = 0;

        bool planEmitted = false;
        int consecutiveFailures = 0;
        bool hadToolErrors = false;
        IReadOnlyList<(string ToolName, string InputJson, bool Failed)>? lastToolBreakdown = null;
        int maxTokensNudgeRetries = 1;  // max nudge attempts per window before accepting partial output
        var executionLog = new List<(string ToolName, string InputJson, string Output, bool Success)>();
        VerificationResult? lastVerification = null;
        int verificationRetries = _verificationOpts.MaxVerificationRetries;

        // ── Model switching state ─────────────────────────────────────────────
        string currentModel = effectiveModel;
        string currentProvider = resolvedProvider;
        string currentEndpoint = resolvedEndpoint;

        ModelSwitchingOptions? modelSwitchingOpts = null;
        if (hookCtx.Variables.TryGetValue("__model_switching_json", out var msoJson) &&
            !string.IsNullOrWhiteSpace(msoJson))
        {
            try
            {
                modelSwitchingOpts = JsonSerializer.Deserialize<ModelSwitchingOptions>(
                msoJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception msoEx)
            {
                _logger.LogWarning(msoEx,
                    "Agent {AgentId}: ModelSwitchingJson is malformed — model switching disabled for this run",
                    definition.Id);
            }
        }
        bool fallbackToOriginalOnError = modelSwitchingOpts?.FallbackToOriginalOnError ?? true;

        // Fallback reference — updated on successful model switch, reset per continuation window
        string fallbackModel = currentModel;
        string fallbackProvider = currentProvider;
        string fallbackEndpoint = currentEndpoint;

        for (int window = 0; window <= maxContinuations && !streamError; window++)
        {
            if (window > 0)
            {
                var contCtx = ReActToolHelper.BuildContinuationContext(window, maxIterations, toolEvidence);
                strategy.PrepareNewWindow(contCtx, systemPrompt, agentWindowOverride);
                _logger.LogInformation("Continuation window {Window} started (agent={Agent}, session={Session})",
                    window + 1, definition.Name, sessionId);
                yield return new AgentStreamChunk { Type = "continuation_start", ContinuationWindow = window + 1 };

                iterationBase += maxIterations;
                consecutiveFailures = 0;
                hadToolErrors = false;
                maxTokensNudgeRetries = 1;
                executionLog.Clear();
                planEmitted = false;
                verificationRetries = _verificationOpts.MaxVerificationRetries;
                fallbackModel = currentModel;
                fallbackProvider = currentProvider;
                fallbackEndpoint = currentEndpoint;
            }
            completedNaturally = false;

            for (int i = 0; i < maxIterations && !streamError; i++)
            {
                _logger.LogInformation("Iteration {Iter} started (agent={Agent}, session={Session})",
                    iterationBase + i + 1, definition.Name, sessionId);
                yield return new AgentStreamChunk { Type = "iteration_start", Iteration = iterationBase + i + 1 };
                hookCtx.IsFinalIteration = false;
                hookCtx.LastHadToolCalls = false;

                // ── OnBeforeIteration hooks ──────────────────────────────────
                if (_hookCoordinator is not null && hooks.Count > 0)
                {
                    hookCtx.ConsecutiveFailures = consecutiveFailures;
                    // hookCtx.WasTruncated is already set when max_tokens fired last iteration
                    var beforeIterResult = await _hookCoordinator.RunOnBeforeIterationAsync(hooks, hookCtx, iterationBase + i + 1, systemPrompt, ct);
                    foreach (var chunk in beforeIterResult.Chunks)
                        yield return chunk;
                    if (beforeIterResult.HadError)
                    {
                        // Log so admins can see that a hook (e.g. PII redactor, injection guard) failed.
                        // Iteration continues — callers who need hard-abort should throw from the hook,
                        // which will propagate out of InvokeStreamAsync to the controller.
                        _logger.LogWarning(
                            "OnBeforeIteration hook failed (iter={Iter}, agent={Agent}) — iteration continues with potentially unmodified prompt",
                            iterationBase + i + 1, definition.Name);
                    }
                    if (beforeIterResult.UpdatedSystemPrompt is not null)
                    {
                        systemPrompt = beforeIterResult.UpdatedSystemPrompt;
                        if (enableHistoryCaching)
                        {
                            // Extract only the hook addition so BP1 (static block) stays stable.
                            dynamicSystemPrompt = systemPrompt.StartsWith(staticSystemPrompt, StringComparison.Ordinal)
                                ? systemPrompt[staticSystemPrompt.Length..].TrimStart('\n')
                                : systemPrompt;
                            strategy.UpdateSystemPrompt(dynamicSystemPrompt);
                        }
                        else
                        {
                            strategy.UpdateSystemPrompt(systemPrompt);
                        }
                    }
                }

                // ── Apply model override set by OnBeforeIteration hooks ────────
                if (_modelSwitchCoordinator is not null
                    && (hookCtx.LlmConfigIdOverride.HasValue || !string.IsNullOrEmpty(hookCtx.ModelOverride)))
                {
                    var fromModel = currentModel;
                    var fromProvider = currentProvider;
                    var switchParams = new ModelSwitchParameters(
                        AnthropicRetry: (call, retCt) => CallWithRetryAsync(call, retCt),
                        OpenAiRetry: (call, retCt) => CallWithRetryAsync(call, retCt),
                        MaxOutputTokens: definition.MaxOutputTokens ?? _agentOpts.MaxOutputTokens,
                        EnableHistoryCaching: enableHistoryCaching);

                    ModelSwitchResult? switchResult = null;
                    Exception? switchEx = null;
                    try
                    {
                        switchResult = await _modelSwitchCoordinator.TryApplyAsync(
                            hookCtx, strategy,
                            currentModel, currentProvider, currentEndpoint,
                            fallbackModel, fallbackProvider, fallbackEndpoint,
                            systemPrompt, allMcpTools, switchParams, ct);
                    }
                    catch (Exception ex) { switchEx = ex; }

                    if (switchEx is not null)
                        _logger.LogWarning(switchEx, "ModelSwitchCoordinator failed — keeping {Model}", currentModel);
                    else if (switchResult is not null)
                    {
                        strategy = switchResult.NewStrategy;
                        currentModel = switchResult.CurrentModel;
                        currentProvider = switchResult.CurrentProvider;
                        currentEndpoint = switchResult.CurrentEndpoint;
                        fallbackModel = switchResult.FallbackModel;
                        fallbackProvider = switchResult.FallbackProvider;
                        fallbackEndpoint = switchResult.FallbackEndpoint;

                        if (switchResult.Switched)
                        {
                            yield return new AgentStreamChunk
                            {
                                Type = "model_switch",
                                Iteration = iterationBase + i + 1,
                                FromModel = fromModel,
                                ToModel = switchResult.SwitchedToModel,
                                FromProvider = fromProvider,
                                ToProvider = switchResult.SwitchedToProvider,
                                Reason = switchResult.SwitchReason,
                            };
                        }
                    }
                }

                // ── Point A: in-run context compaction ─────────────────────────
                try
                {
                    strategy.CompactHistory(systemPrompt, agentWindowOverride);
                }
                catch (Exception compactEx)
                {
                    // Compaction failure must not crash the loop — continue with uncompacted history.
                    _logger.LogWarning(compactEx,
                        "Context compaction failed (iter={Iter}, agent={Agent}) — continuing with uncompacted history",
                        iterationBase + i + 1, definition.Name);
                }

                // ── Iteration model log ────────────────────────────────────────
                _logger.LogInformation(
                    "Iteration {Iteration} | agent={AgentId} tenant={TenantId} | model={Model} configId={ConfigId}",
                    iterationBase + i + 1,
                    definition.Id,
                    hookCtx.Tenant.TenantId,
                    currentModel,
                    hookCtx.LlmConfigIdOverride?.ToString() ?? "(default)");

                // ── LLM call (streaming with non-streaming fallback) ───────────
                var llmCall = await CallLlmForIterationAsync(strategy, hookCtx, iterationBase + i + 1, definition.Name, ct);
                foreach (var delta in llmCall.TextDeltas)
                    yield return delta;
                UnifiedLlmResponse? response = llmCall.Response;
                var llmEx = llmCall.Error;

                if (llmEx is not null)
                {
                    _logger.LogError(llmEx,
                        "LLM call failed (iter={Iter}, agent={Agent}): {Msg}",
                        iterationBase + i + 1, definition.Name, llmEx.Message);

                    var continueAfterError = false;
                    if (_hookCoordinator is not null && hooks.Count > 0)
                    {
                        var (action, errorResult) = await _hookCoordinator.RunOnErrorAsync(
                            hooks, hookCtx, null, llmEx, "OnError", ct);
                        foreach (var chunk in errorResult.Chunks)
                            yield return chunk;
                        if (!errorResult.HadError)
                        {
                            _logger.LogInformation("OnError hook action={Action} (iter={Iter}, agent={Agent})",
                                action, iterationBase + i + 1, definition.Name);
                            continueAfterError = action is ErrorRecoveryAction.Continue or ErrorRecoveryAction.Retry;
                        }
                    }

                    // Scenario 3: API failure on a switched model — restore original model/provider
                    if (fallbackToOriginalOnError && !string.Equals(fallbackModel, currentModel, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Model switch: API failure on {New} — falling back to {Original} (iter={Iter}, agent={Agent})",
                            currentModel, fallbackModel, iterationBase + i + 1, definition.Name);
                        strategy.SetModel(fallbackModel);
                        currentModel = fallbackModel;
                        currentProvider = fallbackProvider;
                        currentEndpoint = fallbackEndpoint;
                        fallbackModel = currentModel; // prevent repeated fallback loop
                    }

                    if (continueAfterError)
                    {
                        _logger.LogWarning(
                            "LLM error recovery: continuing to next iteration (iter={Iter}, agent={Agent})",
                            iterationBase + i + 1, definition.Name);
                        continue;
                    }

                    yield return new AgentStreamChunk { Type = "error", ErrorMessage = llmEx.Message };
                    streamError = true;
                    break;
                }

                // Guard: streaming can end without an IsDone event if the provider misbehaves,
                // leaving response null with no error. Treat this as a stream error rather than
                // dereferencing a null response below.
                if (response is null)
                {
                    _logger.LogWarning(
                        "LLM call returned neither a response nor an error (iter={Iter}, agent={Agent}) — treating as stream error",
                        iterationBase + i + 1, definition.Name);
                    yield return new AgentStreamChunk { Type = "error", ErrorMessage = "LLM produced no response" };
                    streamError = true;
                    break;
                }

                _logger.LogInformation(
                    "LLM stop reason: {StopReason} (iter={Iter}, agent={Agent})",
                    response.StopReason ?? "unknown", iterationBase + i + 1, definition.Name);

                // ── Per-iteration token usage ──────────────────────────────────
                var u = strategy.LastTokenUsage;
                _logger.LogInformation(
                    "Iteration {Iteration} tokens: input={Input} output={Output} cacheRead={CacheRead} cacheCreate={CacheCreate} (agent={Agent})",
                    iterationBase + i + 1, u.Input, u.Output, u.CacheRead, u.CacheCreation, definition.Name);
                totalInputTokens += u.Input;
                totalOutputTokens += u.Output;
                totalCacheRead += u.CacheRead;
                totalCacheCreation += u.CacheCreation;

                // Always emit token_usage — replaces the old conditional cache_stats event.
                // IterationInputTokens/OutputTokens are 0 for OpenAI streaming (ME.AI SDK limitation).
                yield return new AgentStreamChunk
                {
                    Type = "token_usage",
                    Iteration = iterationBase + i + 1,
                    IterationInputTokens = u.Input,
                    IterationOutputTokens = u.Output,
                    IterationCacheRead = u.CacheRead,
                    IterationCacheCreation = u.CacheCreation,
                    TotalInputTokens = totalInputTokens,
                    TotalOutputTokens = totalOutputTokens,
                    TotalCacheRead = totalCacheRead,
                    TotalCacheCreation = totalCacheCreation,
                };

                // ── Text extraction + plan detection ───────────────────────────
                if (!string.IsNullOrEmpty(response.Text))
                {
                    finalResponse = response.Text;
                    var planSteps = ReActPlanParser.ParsePlanSteps(response.Text);
                    if (i == 0 && !planEmitted && planSteps.Length >= 2)
                    {
                        _logger.LogInformation("Plan detected with {StepCount} steps (iter={Iter}, agent={Agent})",
                            planSteps.Length, iterationBase + i + 1, definition.Name);
                        yield return new AgentStreamChunk { Type = "plan", PlanText = response.Text, PlanSteps = planSteps };
                        planEmitted = true;
                    }
                    else
                    {
                        _logger.LogInformation("Thinking (iter={Iter}, agent={Agent}): {Preview}",
                            iterationBase + i + 1, definition.Name,
                            response.Text.Length > 120 ? response.Text[..120] + "…" : response.Text);
                        yield return new AgentStreamChunk { Type = "thinking", Iteration = iterationBase + i + 1, Content = response.Text };
                    }
                }

                if (response.HasToolCalls && toolClientMap.Count > 0)
                {
                    var pipelineResult = await ExecuteToolPipelineAsync(
                        strategy, response, toolClientMap, mcpClients, definition, hookCtx, hooks,
                        toolsUsed, toolEvidence, executionLog, agentWindowOverride, systemPrompt,
                        consecutiveFailures, iterationBase + i + 1,
                        agentDelegationTools, tenant, ct, sessionId: sessionId);
                    foreach (var chunk in pipelineResult.Chunks)
                        yield return chunk;
                    if (pipelineResult.StreamError) { streamError = true; break; }
                    consecutiveFailures = pipelineResult.ConsecutiveFailures;
                    hadToolErrors = pipelineResult.HadToolErrors;
                    lastToolBreakdown = pipelineResult.ToolResultBreakdown;
                    hookCtx.LastHadToolCalls = true;
                    hookCtx.LastIterationResponse = finalResponse;
                }
                else
                {
                    hookCtx.IsFinalIteration = true;

                    // Output truncation: model hit max_tokens mid-response
                    if (response.StopReason == "max_tokens")
                    {
                        hookCtx.WasTruncated = true;

                        // Give OnError hooks a chance to react (Abort = accept partial, Continue/Retry = nudge)
                        var truncationEx = new InvalidOperationException(
                            $"LLM output truncated at max_tokens (iter={iterationBase + i + 1})");
                        var truncationAction = ErrorRecoveryAction.Continue;
                        if (_hookCoordinator is not null && hooks.Count > 0)
                        {
                            var (action, maxTokResult) = await _hookCoordinator.RunOnErrorAsync(
                                hooks, hookCtx, null, truncationEx, "OnError(max_tokens)", ct);
                            truncationAction = action;
                            foreach (var chunk in maxTokResult.Chunks)
                                yield return chunk;
                        }

                        if (truncationAction == ErrorRecoveryAction.Abort || maxTokensNudgeRetries <= 0)
                        {
                            _logger.LogWarning(
                                "max_tokens: accepting partial response (action={Action}, retriesLeft={R}, iter={Iter}, agent={Agent})",
                                truncationAction, maxTokensNudgeRetries, iterationBase + i + 1, definition.Name);
                            hookCtx.WasTruncated = false;
                            completedNaturally = true;
                            break;
                        }

                        maxTokensNudgeRetries--;
                        _logger.LogWarning(
                            "Output truncated at max_tokens (iter={Iter}, agent={Agent}) — nudging ({R} retries left)",
                            iterationBase + i + 1, definition.Name, maxTokensNudgeRetries);
                        strategy.AddAssistantThenUser(finalResponse, MaxTokensNudgePrompt);
                        continue;
                    }

                    hookCtx.WasTruncated = false;

                    // Tool error retry: LLM acknowledged errors but didn't retry — nudge it
                    if (hadToolErrors)
                    {
                        var retryPrompt = lastToolBreakdown is { Count: > 0 }
                            ? ReActToolHelper.BuildSelectiveRetryPrompt(lastToolBreakdown)
                            : ReActToolHelper.ToolErrorRetryPrompt;
                        hadToolErrors = false;
                        lastToolBreakdown = null;
                        strategy.AddAssistantThenUser(finalResponse, retryPrompt);
                        continue;
                    }

                    // No tool calls — verify the response inline before accepting it
                    var inlineEvidence = string.Join("\n\n", toolEvidence);
                    lastVerification = await _verifier.VerifyAsync(finalResponse, toolsUsed, inlineEvidence, ct, currentModel, definition.VerificationMode);

                    if (!lastVerification.IsVerified
                        && lastVerification.UngroundedClaims.Count > 0
                        && verificationRetries > 0
                        && (lastVerification.WasBlocked || lastVerification.Mode == "ToolGrounded"))
                    {
                        verificationRetries--;
                        _logger.LogInformation("Correction retry triggered: {Claims} ungrounded claim(s), {Retries} retries left (agent={Agent})",
                            lastVerification.UngroundedClaims.Count, verificationRetries, definition.Name);
                        var correctionMsg = BuildCorrectionPrompt(lastVerification.UngroundedClaims);
                        yield return new AgentStreamChunk { Type = "correction", Content = correctionMsg };
                        strategy.AddAssistantThenUser(finalResponse, correctionMsg);
                        continue;
                    }

                    completedNaturally = true;
                    break;
                }
            } // end inner iteration loop
            if (completedNaturally) break;
        } // end outer continuation loop

        sw.Stop();

        if (!streamError)
        {
            // ── OnBeforeResponse hooks ───────────────────────────────────────
            if (_hookCoordinator is not null && hooks.Count > 0)
            {
                hookCtx.ToolEvidence = string.Join("\n\n", toolEvidence);
                var (response, beforeResponseResult) = await _hookCoordinator.RunOnBeforeResponseAsync(hooks, hookCtx, finalResponse, ct);
                finalResponse = response;
                foreach (var chunk in beforeResponseResult.Chunks)
                    yield return chunk;
            }

            var evidence = string.Join("\n\n", toolEvidence);
            var verification = lastVerification
                ?? await _verifier.VerifyAsync(finalResponse, toolsUsed, evidence, ct, currentModel, definition.VerificationMode);

            var content = verification.WasBlocked
                ? "I was unable to verify the accuracy of this response. Please try a more specific question."
                : finalResponse;

            _logger.LogInformation("Final response ready: {Len} chars, {ToolCount} tool(s) used (agent={Agent}, session={Session})",
                content.Length, toolsUsed.Count, definition.Name, sessionId);
            _logger.LogInformation(
                "Agent {Agent} token totals: input={Input} output={Output} cacheRead={CacheRead} cacheCreate={CacheCreate} effectiveInput={Effective} iterations={Iterations}",
                definition.Name, totalInputTokens, totalOutputTokens, totalCacheRead, totalCacheCreation,
                totalInputTokens + totalCacheRead, iterationBase + maxIterations);
            yield return new AgentStreamChunk { Type = "final_response", Content = content, SessionId = sessionId };

            if (verification.Mode != "Off")
            {
                _logger.LogInformation("Verification: mode={Mode}, confidence={Conf:F2}, verified={Ok}, blocked={Blocked} (agent={Agent})",
                    verification.Mode, verification.Confidence, verification.IsVerified, verification.WasBlocked, definition.Name);
                yield return new AgentStreamChunk { Type = "verification", Verification = verification };
            }

            if (!string.IsNullOrEmpty(finalResponse))
            {
                Exception? saveEx = null;
                try
                {
                    var turnNumber = await _sessions.SaveTurnAsync(sessionId, userQuery, content, ct);
                    // Populate turn state so InvokeStreamAsync can flush trace data
                    turnState.TurnNumber = turnNumber;
                    turnState.UserQuery = userQuery;
                    turnState.AssistantContent = content;
                    turnState.ModelId = effectiveModel;
                    turnState.Provider = resolvedProvider;
                    turnState.ExecutionTimeMs = sw.ElapsedMilliseconds;
                    turnState.WasSaved = true;
                }
                catch (Exception ex)
                {
                    saveEx = ex;
                    _logger.LogError(ex,
                        "Session save failed (session={SessionId}) — response was delivered but turn history is incomplete",
                        sessionId);
                }

                if (saveEx is not null)
                    yield return new AgentStreamChunk
                    {
                        Type = "session_save_error",
                        ErrorMessage = "Your response was delivered but could not be saved to history. Reload if you need to continue this conversation.",
                        SessionId = sessionId,
                    };

                var transcript = $"User: {userQuery}\nAgent: {finalResponse}";
                var capturedSessionId = sessionId;
                var capturedLlmConfig = resolvedLlmConfig;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _ruleLearner.ExtractRulesFromConversationAsync(capturedSessionId, transcript, CancellationToken.None, capturedLlmConfig);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background rule extraction failed (session={SessionId})", capturedSessionId);
                    }
                }, CancellationToken.None);
            }
        }

        // ── OnAfterResponse hooks ────────────────────────────────────────────
        if (_hookCoordinator is not null && hooks.Count > 0 && !streamError)
        {
            var afterResponse = new AgentResponse
            {
                Success = !streamError,
                Content = finalResponse,
                AgentName = definition.Name,
                SessionId = sessionId,
                ToolsUsed = toolsUsed.Distinct().ToList(),
                ExecutionTime = sw.Elapsed,
                ToolEvidence = string.Join("\n\n", toolEvidence),
            };
            var afterResult = await _hookCoordinator!.RunOnAfterResponseAsync(hooks, hookCtx, afterResponse, ct);
            foreach (var chunk in afterResult.Chunks)
                yield return chunk;
        }

        _logger.LogInformation("Agent completed in {Time}s: {ToolCount} tool(s) [{Tools}] (agent={Agent}, session={Session})",
            sw.Elapsed.TotalSeconds.ToString("F1"), toolsUsed.Count,
            string.Join(", ", toolsUsed.Distinct()), definition.Name, sessionId);
        yield return new AgentStreamChunk { Type = "done", ExecutionTime = $"{sw.Elapsed.TotalSeconds:F1}s", SessionId = sessionId };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ContextWindowOverrideOptions? ParseContextWindowOverride(
        AgentDefinitionEntity definition, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(definition.ContextWindowJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ContextWindowOverrideOptions>(definition.ContextWindowJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Invalid ContextWindowJson for agent {Id} — using global defaults", definition.Id);
            return null;
        }
    }

    private static string BuildCorrectionPrompt(IReadOnlyList<string> ungroundedClaims)
    {
        var sb = new StringBuilder("Your response contained claims that could not be verified against the tool results:\n");
        foreach (var c in ungroundedClaims) sb.AppendLine($"- {c}");
        sb.AppendLine("\nPlease call the appropriate tools to retrieve evidence for these specific claims, then revise your answer based on what the tools return.");
        sb.AppendLine("If the tools cannot provide this information, omit or qualify the unverified claims rather than asserting them as facts.");
        return sb.ToString();
    }

    // MCP connection: see McpConnectionManager.cs / IMcpConnectionManager.cs
    // Tool filtering: see ReActToolHelper.FilterTools
    // Tool deduplication: see ReActToolHelper.DeduplicateCalls
    // ExecutionMode enforcement: see ReActToolHelper.ApplyExecutionModeFilter

    // ── LLM call extraction ───────────────────────────────────────────────────

    /// <summary>
    /// Runs one LLM call iteration, using the streaming path when enabled and
    /// falling back to a single buffered call when streaming fails or is disabled.
    /// Returns text-delta chunks collected during streaming so the caller can yield them.
    /// Applies per-iteration timeouts: absolute for buffered calls, idle-reset for streaming.
    /// Falls back to buffered CallLlmAsync (with retry) on both start and mid-stream failures.
    /// </summary>
    private async Task<LlmCallResult> CallLlmForIterationAsync(
        ILlmProviderStrategy strategy,
        AgentHookContext hookCtx,
        int iteration,
        string agentName,
        CancellationToken ct)
    {
        var textDeltas = new List<AgentStreamChunk>();
        UnifiedLlmResponse? response = null;
        Exception? llmEx = null;
        bool needBufferedFallback = false;

        bool streamingEnabled = _agentOpts.EnableResponseStreaming
            && hookCtx.Variables.GetValueOrDefault("__disable_streaming") is not "true";

        if (streamingEnabled)
        {
            // Streaming path: idle timeout resets on each received chunk
            var idleTimeoutSec = _agentOpts.LlmStreamIdleTimeoutSeconds;
            using var streamCts = idleTimeoutSec > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            var streamToken = streamCts?.Token ?? ct;
            if (streamCts is not null) streamCts.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec));

            IAsyncEnumerator<UnifiedStreamDelta>? streamEnumerator = null;
            bool hasMoreDeltas = false;
            Exception? startEx = null;
            try
            {
                streamEnumerator = strategy.StreamLlmAsync(streamToken).GetAsyncEnumerator(streamToken);
                hasMoreDeltas = await streamEnumerator.MoveNextAsync();
                streamCts?.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec)); // reset after first chunk
            }
            catch (Exception ex) { startEx = ex; streamEnumerator = null; }

            if (startEx is not null)
            {
                _logger.LogWarning(startEx,
                    "StreamLlmAsync failed at start (iter={Iter}, agent={Agent}) — falling back to CallLlmAsync{Detail}",
                    iteration, agentName, GetLlmErrorDetail(startEx));
                needBufferedFallback = true;
            }

            if (streamEnumerator is not null)
            {
                while (hasMoreDeltas)
                {
                    var delta = streamEnumerator.Current;
                    if (delta.IsDone) { response = delta.Final; break; }
                    if (delta.TextDelta is not null)
                        textDeltas.Add(new AgentStreamChunk
                        {
                            Type = "text_delta",
                            Iteration = iteration,
                            Content = delta.TextDelta,
                        });
                    Exception? iterEx = null;
                    try
                    {
                        streamCts?.CancelAfter(TimeSpan.FromSeconds(idleTimeoutSec)); // reset idle timer
                        hasMoreDeltas = await streamEnumerator.MoveNextAsync();
                    }
                    catch (Exception ex) { iterEx = ex; }
                    if (iterEx is not null)
                    {
                        // Mid-stream failure (connection drop, idle timeout, etc.)
                        // Discard partial deltas and fall back to buffered call with retry.
                        _logger.LogWarning(iterEx,
                            "StreamLlmAsync failed mid-stream (iter={Iter}, agent={Agent}) — falling back to CallLlmAsync{Detail}",
                            iteration, agentName, GetLlmErrorDetail(iterEx));
                        textDeltas.Clear();
                        needBufferedFallback = true;
                        break;
                    }
                }
                await streamEnumerator.DisposeAsync();
            }
        }
        else
        {
            needBufferedFallback = true;
        }

        // Buffered fallback: used when streaming is disabled, failed at start, or failed mid-stream.
        // CallLlmAsync goes through CallWithRetryAsync (3 retries with exponential backoff).
        if (needBufferedFallback && response is null)
        {
            var absTimeoutSec = _agentOpts.LlmTimeoutSeconds;
            using var bufCts = absTimeoutSec > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (bufCts is not null) bufCts.CancelAfter(TimeSpan.FromSeconds(absTimeoutSec));
            var bufToken = bufCts?.Token ?? ct;

            try { response = await strategy.CallLlmAsync(bufToken); }
            catch (Exception ex) { llmEx = ex; }
        }

        return new LlmCallResult(response, llmEx, textDeltas);
    }

    // ── Tool pipeline extraction ──────────────────────────────────────────────

    /// <summary>
    /// Executes the full tool call pipeline for one ReAct iteration:
    /// Phase 1 (announce), Phase 2 (dedup/execute/error), Phase 3 (results/emit),
    /// and adaptive re-planning after consecutive failures.
    /// Chunks that would otherwise be yielded are returned in <see cref="ToolPipelineOutcome.Chunks"/>.
    /// <paramref name="toolsUsed"/>, <paramref name="toolEvidence"/>, and <paramref name="executionLog"/>
    /// are mutated in place.
    /// </summary>
    private async Task<ToolPipelineOutcome> ExecuteToolPipelineAsync(
        ILlmProviderStrategy strategy,
        UnifiedLlmResponse response,
        Dictionary<string, McpClient> toolClientMap,
        Dictionary<string, McpClient> mcpClients,
        AgentDefinitionEntity definition,
        AgentHookContext hookCtx,
        List<IAgentLifecycleHook> hooks,
        List<string> toolsUsed,
        List<string> toolEvidence,
        List<(string ToolName, string InputJson, string Output, bool Success)> executionLog,
        ContextWindowOverrideOptions? agentWindowOverride,
        string systemPrompt,
        int consecutiveFailures,
        int iteration,
        List<AgentDelegationTool> agentDelegationTools,
        TenantContext tenant,
        CancellationToken ct,
        string? sessionId = null)
    {
        var chunks = new List<AgentStreamChunk>();

        strategy.CommitAssistantResponse();

        // ── Hook filter ──────────────────────────────────────────────────────
        var filteredToolCalls = response.ToolCalls
            .Select(tc => new UnifiedToolCallRef { Id = tc.Id, Name = tc.Name, InputJson = tc.InputJson })
            .ToList();

        if (_hookCoordinator is not null && hooks.Count > 0)
        {
            var (filtered, filterResult) = await _hookCoordinator.RunOnToolFilterAsync(hooks, hookCtx, filteredToolCalls, ct);
            filteredToolCalls = filtered;
            chunks.AddRange(filterResult.Chunks);
        }

        var filteredById = filteredToolCalls.ToDictionary(tc => tc.Id, StringComparer.Ordinal);
        var plannedToolCalls = response.ToolCalls
            .Select(tc => filteredById.TryGetValue(tc.Id, out var filtered)
                ? new UnifiedToolCall(filtered.Id, filtered.Name, filtered.InputJson)
                : tc)
            .ToList();
        var executableToolCalls = plannedToolCalls
            .Where(tc => !filteredById.TryGetValue(tc.Id, out var filtered) || !filtered.Filtered)
            .ToList();
        var suppressedToolCalls = plannedToolCalls
            .Where(tc => filteredById.TryGetValue(tc.Id, out var filtered) && filtered.Filtered)
            .ToList();

        // ── Phase 1: announce all planned tool calls ─────────────────────────
        _logger.LogInformation("Tool calls: {Count} tool(s) requested (iter={Iter}, agent={Agent}): [{Tools}]",
            plannedToolCalls.Count, iteration, definition.Name,
            string.Join(", ", plannedToolCalls.Select(tc => tc.Name)));
        foreach (var tc in plannedToolCalls)
            chunks.Add(new AgentStreamChunk
            {
                Type = "tool_call",
                Iteration = iteration,
                ToolName = tc.Name,
                ToolInput = tc.InputJson,
            });

        // ── Phase 2: deduplicate + execute + per-group error hooks ───────────
        var dedupGroups = ReActToolHelper.DeduplicateCalls(
            executableToolCalls, tc => (tc.Name, tc.InputJson), _agentOpts.NeverDeduplicateToolPrefixes);
        foreach (var (gName, _, originals) in dedupGroups.Where(g => g.Originals.Count > 1))
            _logger.LogDebug("Deduplicating {Count} identical '{Tool}' calls into 1", originals.Count, gName);

        var effectiveMaxToolChars = definition.MaxToolResultChars ?? _agentOpts.MaxToolResultChars;
        var agentToolLookup = agentDelegationTools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var currentDelegationDepth = 0;
        if (hookCtx.Request.Metadata.TryGetValue("a2a_local_depth", out var depthObj) && depthObj is int pd)
            currentDelegationDepth = pd;
        else if (hookCtx.Variables.TryGetValue("a2a_local_depth", out var depthStr) && int.TryParse(depthStr, out var parsedDepth))
            currentDelegationDepth = parsedDepth;

        // Throttle concurrent tool calls to avoid overwhelming MCP servers
        var maxParallel = _agentOpts.MaxParallelToolCalls;
        using var throttle = maxParallel > 0 ? new SemaphoreSlim(maxParallel) : null;

        var groupOutputs = await Task.WhenAll(dedupGroups.Select(async group =>
        {
            if (throttle is not null) await throttle.WaitAsync(ct);
            try
            {
                var (gName, gInputJson, _) = group;

                // Route agent delegation tools to AgentToolExecutor
                if (_agentToolExecutor is not null && agentToolLookup.TryGetValue(gName, out var agentTool))
                {
                    var agentResult = await _agentToolExecutor.ExecuteAsync(
                        agentTool, gInputJson, tenant, currentDelegationDepth, effectiveMaxToolChars, ct,
                        hookCtx.Request.ForwardSsoToMcp, parentSessionId: sessionId);
                    return (Group: group, agentResult.Output, ContentParts: (IReadOnlyList<ContentPart>?)null, agentResult.Failed, agentResult.Error);
                }

                var toolResult = await _toolExecutor.ExecuteAsync(gName, gInputJson, toolClientMap, mcpClients, effectiveMaxToolChars, ct);
                return (Group: group, toolResult.Output, ContentParts: toolResult.ContentParts, toolResult.Failed, toolResult.Error);
            }
            finally { throttle?.Release(); }
        }));

        var finalGroupOutputs = new List<(string Name, string InputJson, List<UnifiedToolCall> Originals, string Output, IReadOnlyList<ContentPart>? ContentParts, bool Failed, Exception? Error)>();
        bool pipelineStreamError = false;
        foreach (var groupOutput in groupOutputs)
        {
            if (pipelineStreamError) break;
            var currentOutput = groupOutput;
            if (currentOutput.Failed && currentOutput.Error is not null && _hookCoordinator is not null && hooks.Count > 0)
            {
                var (action, errorResult) = await _hookCoordinator.RunOnErrorAsync(
                    hooks, hookCtx, groupOutput.Group.Name, currentOutput.Error, "OnError", ct);
                chunks.AddRange(errorResult.Chunks);

                if (!errorResult.HadError)
                {
                    if (action == ErrorRecoveryAction.Retry)
                    {
                        if (_agentToolExecutor is not null && agentToolLookup.TryGetValue(groupOutput.Group.Name, out var retryAgentTool))
                        {
                            var retryResult = await _agentToolExecutor.ExecuteAsync(
                                retryAgentTool, groupOutput.Group.InputJson, tenant,
                                currentDelegationDepth, effectiveMaxToolChars, ct,
                                hookCtx.Request.ForwardSsoToMcp, parentSessionId: sessionId);
                            currentOutput = (groupOutput.Group, retryResult.Output, ContentParts: (IReadOnlyList<ContentPart>?)null, retryResult.Failed, retryResult.Error);
                        }
                        else
                        {
                            var retryResult = await _toolExecutor.ExecuteAsync(
                                groupOutput.Group.Name, groupOutput.Group.InputJson,
                                toolClientMap, mcpClients, effectiveMaxToolChars, ct);
                            currentOutput = (groupOutput.Group, retryResult.Output, ContentParts: retryResult.ContentParts, retryResult.Failed, retryResult.Error);
                        }
                    }
                    else if (action == ErrorRecoveryAction.Abort)
                    {
                        chunks.Add(new AgentStreamChunk { Type = "error", ErrorMessage = currentOutput.Error.Message });
                        pipelineStreamError = true;
                        break;
                    }
                }
            }

            finalGroupOutputs.Add((
                currentOutput.Group.Name,
                currentOutput.Group.InputJson,
                currentOutput.Group.Originals,
                currentOutput.Output,
                currentOutput.ContentParts,
                currentOutput.Failed,
                currentOutput.Error));
        }

        if (pipelineStreamError)
            return new ToolPipelineOutcome(StreamError: true, consecutiveFailures, HadToolErrors: false, chunks);

        // ── Phase 3: emit results, update history, track failures ────────────
        var toolOutputs = finalGroupOutputs
            .SelectMany(go => go.Originals.Select(
                tc => (tc, inputJson: go.InputJson, output: go.Output, contentParts: go.ContentParts, failed: go.Failed, error: go.Error, filtered: false)))
            .Concat(suppressedToolCalls.Select(
                tc => (tc, inputJson: tc.InputJson, output: "Tool call filtered by rule pack policy.", contentParts: (IReadOnlyList<ContentPart>?)null, failed: false, error: (Exception?)null, filtered: true)))
            .ToList();

        var unifiedResults = new List<UnifiedToolResult>();
        foreach (var (tc, inputJson, toolOutput, contentParts, stepFailed, _, filtered) in toolOutputs)
        {
            var finalToolOutput = toolOutput;
            if (!filtered && _hookCoordinator is not null && hooks.Count > 0)
            {
                var (output, afterResult) = await _hookCoordinator.RunOnAfterToolCallAsync(hooks, hookCtx, tc.Name, toolOutput, stepFailed, ct);
                finalToolOutput = output;
                chunks.AddRange(afterResult.Chunks);
            }

            toolsUsed.Add(tc.Name);
            toolEvidence.Add($"[Tool: {tc.Name}]\n{finalToolOutput}");
            chunks.Add(new AgentStreamChunk
            {
                Type = "tool_result",
                Iteration = iteration,
                ToolName = tc.Name,
                ToolOutput = finalToolOutput,
            });

            // Pass content parts (e.g. image blocks from MCP tools) through to the LLM.
            unifiedResults.Add(new UnifiedToolResult(tc.Id, tc.Name, finalToolOutput, stepFailed,
                filtered ? null : contentParts));
            executionLog.Add((tc.Name, inputJson, finalToolOutput, !stepFailed));
            _logger.LogInformation("Tool result: {Tool} {Status} (iter={Iter}, agent={Agent}, output={Len} chars)",
                tc.Name, stepFailed ? "FAILED" : "OK", iteration, definition.Name, finalToolOutput.Length);
            consecutiveFailures = stepFailed ? consecutiveFailures + 1 : 0;
        }

        // For local LLM endpoints: summarise images via a clean single-turn vision call
        // (llama.cpp rejects images in follow-up user messages after tool calls).
        var totalImages = unifiedResults.Sum(r => r.ContentParts?.OfType<ImageContentPart>().Count() ?? 0);
        if (strategy.UseVisionSummarization && totalImages > 0)
        {
            var resolved = new List<UnifiedToolResult>(unifiedResults.Count);
            foreach (var r in unifiedResults)
            {
                var imgs = r.ContentParts?.OfType<ImageContentPart>().ToList();
                if (imgs is not { Count: > 0 })
                {
                    resolved.Add(r);
                    continue;
                }

                var descs = new List<string>();
                foreach (var img in imgs)
                {
                    try
                    {
                        _logger.LogDebug("Vision: calling SummarizeImageAsync (tool={Tool}, mime={Mime}, dataLen={Len})",
                            r.ToolName, img.MediaType, img.Data?.Length ?? 0);
                        var desc = await strategy.SummarizeImageAsync(img, ct);
                        if (!string.IsNullOrEmpty(desc)) descs.Add(desc);
                    }
                    catch (Exception ex)
                    {
                        var detail = GetLlmErrorDetail(ex);
                        _logger.LogWarning("Vision summarization failed (tool={Tool}): {Err}{Detail}",
                            r.ToolName, ex.Message, detail);
                    }
                }
                if (descs.Count > 0)
                {
                    var preview = string.Join("\n", descs);
                    if (preview.Length > 800) preview = preview[..800] + "…";
                    _logger.LogInformation(
                        "Vision: summarised {Count} image(s) via single-turn call (iter={Iter}, tool={Tool})\n{Preview}",
                        descs.Count, iteration, r.ToolName, preview);
                }
                var combined = descs.Count > 0
                    ? (string.IsNullOrEmpty(r.Output) ? string.Join("\n", descs)
                       : r.Output + "\n\nVisual content:\n" + string.Join("\n", descs))
                    : r.Output;
                resolved.Add(new UnifiedToolResult(r.ToolCallId, r.ToolName, combined, r.IsError, null));
            }
            unifiedResults = resolved;
        }
        else if (totalImages > 0)
        {
            _logger.LogInformation(
                "Vision: injecting {Count} image(s) into LLM context (iter={Iter}, agent={Agent})",
                totalImages, iteration, definition.Name);
        }
        strategy.AddToolResults(unifiedResults);

        bool hadToolErrors = toolOutputs.Any(t => t.failed);

        // ── Adaptive re-planning after consecutive failures ───────────────────
        if (consecutiveFailures >= 2)
        {
            var sb = new StringBuilder("Execution state so far:\n");
            foreach (var (eTool, _, eOut, eOk) in executionLog)
                sb.AppendLine($"{(eOk ? "✓" : "✗")} [{eTool}] → {eOut[..Math.Min(150, eOut.Length)]}");
            sb.AppendLine("\nRevise your remaining plan with a concrete alternative approach.");
            strategy.AddUserMessage(sb.ToString());
            strategy.CompactHistory(systemPrompt, agentWindowOverride);

            if (hookCtx.ReplanConfigId.HasValue && _resolver is not null)
            {
                var replanCfgId = hookCtx.ReplanConfigId.Value;
                ResolvedLlmConfig? replanCfg = null;
                try { replanCfg = await _resolver.ResolveAsync(hookCtx.Tenant.TenantId, replanCfgId, null, ct); }
                catch (Exception replanCfgEx)
                {
                    _logger.LogWarning(replanCfgEx,
                        "Agent {AgentId}: replan config {ConfigId} could not be resolved — continuing with current model",
                        definition.Id, replanCfgId);
                }
                if (replanCfg is not null) strategy.SetModel(replanCfg.Model, null, replanCfg.ApiKey, replanCfg.Endpoint);
                hookCtx.ReplanConfigId = null;
            }
            else if (!string.IsNullOrEmpty(hookCtx.ReplanModel))
            {
                strategy.SetModel(hookCtx.ReplanModel);
                hookCtx.ReplanModel = null;
            }

            string? revisedText = null;
            Exception? replanEx = null;
            try { revisedText = await strategy.CallReplanAsync(ct); }
            catch (Exception ex) { replanEx = ex; }
            if (replanEx is not null) _logger.LogWarning(replanEx, "Re-planning call failed");

            if (!string.IsNullOrEmpty(revisedText))
            {
                _logger.LogInformation("Plan revised after {Failures} consecutive failures (iter={Iter}, agent={Agent})",
                    2, iteration, definition.Name);
                chunks.Add(new AgentStreamChunk
                {
                    Type = "plan_revised",
                    PlanText = revisedText,
                    PlanSteps = ReActPlanParser.ParsePlanSteps(revisedText),
                });
                strategy.AddAssistantThenUser(revisedText, "Good revised plan. Continue executing it.");
            }

            executionLog.Clear();
            consecutiveFailures = 0;
            hadToolErrors = false;
        }

        var toolBreakdown = toolOutputs
            .Select(t => (t.tc.Name, t.inputJson, t.failed))
            .ToList();
        return new ToolPipelineOutcome(StreamError: false, consecutiveFailures, hadToolErrors, chunks, toolBreakdown);
    }

    // ── Tool output/result helpers → ReActToolHelper ─────────────────────────
    // IsToolOutputError, TruncateResult, BuildContinuationContext, ToolErrorRetryPrompt
    // were extracted to ReActToolHelper.cs (Phase A refactor).

    // ── LLM retry with exponential backoff ───────────────────────────────────

    private async Task<T> CallWithRetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            Exception? lastEx = null;
            try { return await call(); }
            catch (Exception ex) when (IsTransientLlmError(ex)
                                       && attempt < _agentOpts.Retry.MaxRetries
                                       && !ct.IsCancellationRequested)
            {
                lastEx = ex;
                attempt++;
                var delayMs = _agentOpts.Retry.BaseDelayMs * (1 << attempt);  // 2s, 4s, 8s
                _logger.LogWarning(
                    "LLM transient error (attempt {A}/{Max}): {Msg}. Retrying in {D}ms",
                    attempt, _agentOpts.Retry.MaxRetries, ex.Message, delayMs);
                await Task.Delay(delayMs, ct);
            }
            catch (Exception ex) when (IsTransientLlmError(ex) && attempt >= _agentOpts.Retry.MaxRetries)
            {
                _logger.LogError(ex,
                    "LLM call failed after {Max} retries — giving up: {Msg}{Detail}",
                    _agentOpts.Retry.MaxRetries, ex.Message, GetLlmErrorDetail(ex));
                throw;
            }
        }
    }

    private static string GetLlmErrorDetail(Exception ex)
    {
        if (ex is not ClientResultException cre) return "";
        var body = cre.GetRawResponse()?.Content?.ToString() ?? "";
        if (body.Length > 800) body = body[..800] + "…";
        return $" [HTTP {cre.Status}: {body}]";
    }

    private static bool IsTransientLlmError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("429") || msg.Contains("503") || msg.Contains("502") ||
               ex is TimeoutException ||
               (ex is TaskCanceledException tce && !tce.CancellationToken.IsCancellationRequested);
    }

    // Hook config merging: see AgentHookHelper.MergeVariables / AgentHookHelper.MergeHookConfig
}

// ── McpToolBinding ────────────────────────────────────────────────────────────

/// <summary>
/// MCP server binding — matches the Claude Desktop / MCP standard config schema.
/// </summary>
public sealed class McpToolBinding
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Executable, e.g. "docker" or "npx"</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>CLI args array, e.g. ["run", "-i", "--rm", "-e", "OWM_API_KEY", "mcp/openweather"]</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>Environment variables injected into the process</summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>HTTP/SSE endpoint for container-exposed ports</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>"stdio" (default), "http", or "sse"</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>
    /// Inject the current user's SSO Bearer token + tenant headers into every HTTP/SSE MCP call.
    /// Only applies to http/sse transport; ignored for stdio.
    /// </summary>
    public bool PassSsoToken { get; set; } = false;

    /// <summary>
    /// Inject tenant context headers (X-Tenant-ID, X-Site-ID) without the Bearer token.
    /// Use when the MCP server needs tenant routing but not user authentication.
    /// Only applies to http/sse transport; ignored for stdio.
    /// </summary>
    public bool PassTenantHeaders { get; set; } = false;

    /// <summary>
    /// Access level classification for ExecutionMode enforcement.
    /// "ReadOnly" = safe in ReadOnly mode; "ReadWrite" = blocked in ReadOnly (default); "Destructive" = blocked in ReadOnly.
    /// </summary>
    public string Access { get; set; } = "ReadWrite";

    /// <summary>
    /// References a named credential in the tenant's credential vault (McpCredentialEntity.Name).
    /// When set on http/sse transports, the resolved API key is injected as an auth header.
    /// For stdio transports, the key is injected as the MCP_API_KEY environment variable.
    /// </summary>
    public string? CredentialRef { get; set; }
}

// ── Extracted method result records ──────────────────────────────────────────

/// <summary>
/// Result of a single LLM call iteration: the response (or error) plus any
/// text-delta chunks accumulated during streaming.
/// </summary>
internal sealed record LlmCallResult(
    UnifiedLlmResponse? Response,
    Exception? Error,
    IReadOnlyList<AgentStreamChunk> TextDeltas);

/// <summary>
/// Result of the tool pipeline for one ReAct iteration: updated loop state
/// and all SSE chunks that would otherwise have been yielded directly.
/// </summary>
internal sealed record ToolPipelineOutcome(
    bool StreamError,
    int ConsecutiveFailures,
    bool HadToolErrors,
    IReadOnlyList<AgentStreamChunk> Chunks,
    IReadOnlyList<(string ToolName, string InputJson, bool Failed)>? ToolResultBreakdown = null);

/// <summary>
/// Mutable state bag shared between ExecuteReActLoopAsync and InvokeStreamAsync
/// so the caller can flush trace data after SaveTurnAsync completes.
/// </summary>
internal sealed class SessionTurnState
{
    public int TurnNumber { get; set; }
    public string UserQuery { get; set; } = string.Empty;
    public string AssistantContent { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public long ExecutionTimeMs { get; set; }
    public bool WasSaved { get; set; }
}
