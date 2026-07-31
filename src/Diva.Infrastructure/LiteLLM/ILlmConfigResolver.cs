namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Resolves the effective LLM configuration for a given tenant + agent definition
/// by merging the hierarchy bottom-up:
///
/// Default path (agentLlmConfigId is null):
///   1. PlatformLlmConfigEntity (global DB defaults, fallback to IOptions&lt;LlmOptions&gt; if not seeded)
///   2. GroupLlmConfigEntity (unnamed default, first group ordered by GroupId ASC)
///   3. TenantLlmConfigEntity (unnamed default per-tenant)
///   4. agentModelId (per-agent model-only override)
///
/// Named config path (agentLlmConfigId is not null):
///   1. PlatformLlmConfigEntity (baseline defaults)
///   2. Named config looked up by ID — first in TenantLlmConfigs, then GroupLlmConfigs
///   3. agentModelId (per-agent model-only override on top of named config)
/// </summary>
public interface ILlmConfigResolver
{
    /// <param name="agentLlmConfigId">When set, use a specific named config by ID (bypasses group→tenant default chain).</param>
    /// <param name="environmentId">The requesting TenantContext's environment (Phase E). Required —
    /// deliberately not optional, so every call site must be updated explicitly rather than silently
    /// defaulting and risking a wrong-environment secret. 0 = wildcard/no environment scoping (system/
    /// background contexts, or call sites not yet environment-aware — see LlmRuleExtractor's platform-
    /// baseline branch). When a named config resolves, its row is re-looked-up by (Name, environmentId)
    /// so promotion "just works" — the agent's stored LlmConfigId is only used to discover the Name;
    /// the actual key returned always matches the CALLER's own environment, never a different one.</param>
    Task<ResolvedLlmConfig> ResolveAsync(int tenantId, int? agentLlmConfigId, string? agentModelId, int environmentId, CancellationToken ct);

    /// <summary>Evicts the cached config for the given tenant (call after updating any level).</summary>
    void InvalidateForTenant(int tenantId);

    /// <summary>Evicts the cached platform config (call after updating PlatformLlmConfig).</summary>
    void InvalidatePlatform();
}

public sealed record ResolvedLlmConfig(
    string Provider,
    string ApiKey,
    string Model,
    string? Endpoint,
    string? DeploymentName,
    IReadOnlyList<string> AvailableModels);
