using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Context;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Input parameters required to create a new provider strategy during a model switch.
/// Passed per-call so the runner's retry closures are captured correctly.
/// </summary>
internal sealed record ModelSwitchParameters(
    Func<Func<Task<MessageResponse>>, CancellationToken, Task<MessageResponse>> AnthropicRetry,
    Func<Func<Task<ChatResponse>>,    CancellationToken, Task<ChatResponse>>    OpenAiRetry,
    int  MaxOutputTokens,
    bool EnableHistoryCaching = true);

/// <summary>
/// The outcome of a model switch attempt. When <see cref="Switched"/> is true,
/// the runner MUST replace its current <c>strategy</c> with <see cref="NewStrategy"/>
/// and update the current/fallback model/provider/endpoint locals.
/// When <see cref="Switched"/> is false, everything else is unchanged.
/// </summary>
internal sealed record ModelSwitchResult(
    bool                 Switched,
    ILlmProviderStrategy NewStrategy,
    string               CurrentModel,
    string               CurrentProvider,
    string               CurrentEndpoint,
    string               FallbackModel,
    string               FallbackProvider,
    string               FallbackEndpoint,
    string?              SwitchedToModel,
    string?              SwitchedToProvider,
    string?              SwitchReason);

/// <summary>
/// Isolates LlmConfig resolution, cross-provider export/import, and same-provider model swap
/// from the main iteration loop.
///
/// Created by <see cref="AnthropicAgentRunner"/> when provider deps are available.
/// Stateless — all context is passed per call.
/// </summary>
internal sealed class ModelSwitchCoordinator(
    IAnthropicProvider       anthropic,
    IOpenAiProvider          openAi,
    IContextWindowManager    ctx,
    ILlmConfigResolver?      resolver,
    ILogger                  logger)
{
    /// <summary>
    /// Tries to apply the model overrides set by OnBeforeIteration hooks.
    /// Returns null when no switch was requested (hookCtx overrides are empty).
    /// Always clears the hookCtx override properties before returning.
    /// </summary>
    public async Task<ModelSwitchResult?> TryApplyAsync(
        AgentHookContext        hookCtx,
        ILlmProviderStrategy    strategy,
        string                  currentModel,
        string                  currentProvider,
        string                  currentEndpoint,
        string                  fallbackModel,
        string                  fallbackProvider,
        string                  fallbackEndpoint,
        string                  systemPrompt,
        List<McpClientTool>     allMcpTools,
        ModelSwitchParameters   p,
        CancellationToken       ct)
    {
        if (!hookCtx.LlmConfigIdOverride.HasValue && string.IsNullOrEmpty(hookCtx.ModelOverride))
            return null;

        var fromModel    = currentModel;
        var fromProvider = currentProvider;
        var fromEndpoint = currentEndpoint;
        string? switchedToModel    = null;
        string? switchedToProvider = null;
        string? switchReason       = hookCtx.ModelSwitchReason;

        if (hookCtx.LlmConfigIdOverride.HasValue && resolver is not null)
        {
            ResolvedLlmConfig? newCfg = null;
            Exception? resolveEx = null;
            var modelHint = !string.IsNullOrEmpty(hookCtx.ModelOverride) ? hookCtx.ModelOverride : null;
                try { newCfg = await resolver.ResolveAsync(hookCtx.Tenant.TenantId, hookCtx.LlmConfigIdOverride, modelHint, hookCtx.Tenant.EnvironmentId, ct); }
            catch (Exception ex) { resolveEx = ex; }

            if (resolveEx is not null)
                logger.LogWarning(resolveEx, "Model switch: failed to resolve LlmConfigId {Id} — keeping {Model}",
                    hookCtx.LlmConfigIdOverride.Value, currentModel);
            else if (newCfg is null)
                logger.LogWarning("Model switch: LlmConfigId {Id} resolved to null — keeping {Model}",
                    hookCtx.LlmConfigIdOverride.Value, currentModel);
            else
            {
                bool providerChanges = !newCfg.Provider.Equals(currentProvider, StringComparison.OrdinalIgnoreCase);
                bool endpointChanges = !string.Equals(newCfg.Endpoint, currentEndpoint, StringComparison.OrdinalIgnoreCase);

                if (providerChanges || endpointChanges)
                {
                    // Cross-provider swap: export history, create new strategy, import
                    List<UnifiedHistoryEntry>? exportedHistory = null;
                    Exception? exportEx = null;
                    try { exportedHistory = strategy.ExportHistory(); }
                    catch (Exception ex) { exportEx = ex; }

                    if (exportEx is not null)
                        logger.LogWarning(exportEx, "Model switch: ExportHistory failed — keeping {Model}", currentModel);
                    else
                    {
                        ILlmProviderStrategy? newStrategy = null;
                        Exception? swapEx = null;
                        bool isNewAnthropic = newCfg.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
                        try
                        {
                            newStrategy = isNewAnthropic
                                // After a cross-provider switch the coordinator only has the combined
                                // systemPrompt. Pass it as the static block with an empty dynamic block;
                                // hooks will re-inject per-iteration content at OnBeforeIteration.
                                ? new AnthropicProviderStrategy(anthropic, ctx, newCfg.Model, p.MaxOutputTokens,
                                    systemPrompt, string.Empty,
                                    p.AnthropicRetry,
                                    enableHistoryCaching: p.EnableHistoryCaching,
                                    apiKeyOverride: newCfg.ApiKey)
                                : new OpenAiProviderStrategy(openAi, ctx, newCfg.Model,
                                    p.OpenAiRetry, maxOutputTokens: p.MaxOutputTokens,
                                    apiKeyOverride: newCfg.ApiKey, endpointOverride: newCfg.Endpoint);
                            newStrategy.ImportHistory(exportedHistory!, systemPrompt, allMcpTools);
                        }
                        catch (Exception ex) { swapEx = ex; newStrategy = null; }

                        if (swapEx is not null || newStrategy is null)
                            logger.LogWarning(swapEx, "Model switch: ImportHistory/create strategy failed — keeping {Model}", currentModel);
                        else
                        {
                            strategy        = newStrategy;
                            currentProvider = newCfg.Provider;
                            currentEndpoint = newCfg.Endpoint ?? string.Empty;
                            currentModel    = newCfg.Model;
                            switchedToModel    = currentModel;
                            switchedToProvider = currentProvider;
                        }
                    }
                }
                else
                {
                    // Same provider, same endpoint — update model/key only
                    strategy.SetModel(newCfg.Model, null, newCfg.ApiKey, newCfg.Endpoint);
                    currentModel       = newCfg.Model;
                    switchedToModel    = currentModel;
                    switchedToProvider = currentProvider;
                }
            }
        }
        else if (!string.IsNullOrEmpty(hookCtx.ModelOverride))
        {
            // Same-provider model-only switch
            strategy.SetModel(hookCtx.ModelOverride!, hookCtx.MaxTokensOverride, hookCtx.ApiKeyOverride);
            currentModel       = hookCtx.ModelOverride!;
            switchedToModel    = currentModel;
            switchedToProvider = currentProvider;
        }

        // Update fallback reference when switch was to a different model
        if (switchedToModel is not null && !string.Equals(switchedToModel, fromModel, StringComparison.OrdinalIgnoreCase))
        {
            fallbackModel    = fromModel;
            fallbackProvider = fromProvider;
            fallbackEndpoint = fromEndpoint;
        }

        // Always clear hook overrides
        hookCtx.LlmConfigIdOverride = null;
        hookCtx.ModelOverride       = null;
        hookCtx.MaxTokensOverride   = null;
        hookCtx.ApiKeyOverride      = null;
        hookCtx.ModelSwitchReason   = null;

        return new ModelSwitchResult(
            Switched:          switchedToModel is not null,
            NewStrategy:       strategy,
            CurrentModel:      currentModel,
            CurrentProvider:   currentProvider,
            CurrentEndpoint:   currentEndpoint,
            FallbackModel:     fallbackModel,
            FallbackProvider:  fallbackProvider,
            FallbackEndpoint:  fallbackEndpoint,
            SwitchedToModel:   switchedToModel,
            SwitchedToProvider: switchedToProvider,
            SwitchReason:      switchReason);
    }
}
