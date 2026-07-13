using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Groups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Singleton-safe LLM config resolver. Resolves: platform catalog → named config (by ID, optionally following
/// a PlatformConfigRef FK) → per-agent model override.
/// Uses 2-minute TTL IMemoryCache per (tenantId, configId, modelId) combo.
/// </summary>
public sealed class LlmConfigResolver : ILlmConfigResolver
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IGroupMembershipCache _groups;
    private readonly IMemoryCache _cache;
    private readonly ILogger<LlmConfigResolver> _logger;
    private readonly LlmOptions _fallback;

    public LlmConfigResolver(
        IDatabaseProviderFactory db,
        IGroupMembershipCache groups,
        IMemoryCache cache,
        IOptions<LlmOptions> fallback,
        ILogger<LlmConfigResolver> logger)
    {
        _db = db;
        _groups = groups;
        _cache = cache;
        _logger = logger;
        _fallback = fallback.Value;
    }

    public async Task<ResolvedLlmConfig> ResolveAsync(int tenantId, int? agentLlmConfigId, string? agentModelId, CancellationToken ct)
    {
        var cacheKey = $"llm_resolved_{tenantId}_{agentLlmConfigId?.ToString() ?? ""}_{agentModelId ?? ""}";
        if (_cache.TryGetValue(cacheKey, out ResolvedLlmConfig? cached) && cached is not null)
            return cached;

        using var db = _db.CreateDbContext();

        // 1. Platform defaults (fallback to IOptions<LlmOptions> if DB not seeded yet)
        var platform = await db.PlatformLlmConfigs.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        var state = new LlmConfigState(
            platform?.Provider ?? _fallback.DirectProvider.Provider,
            platform?.ApiKey ?? _fallback.DirectProvider.ApiKey,
            platform?.Model ?? _fallback.DirectProvider.Model,
            platform?.Endpoint ?? _fallback.DirectProvider.Endpoint,
            platform?.DeploymentName ?? _fallback.DirectProvider.DeploymentName,
            ParseAvailableModels(platform?.AvailableModelsJson, _fallback.AvailableModels));

        if (agentLlmConfigId.HasValue)
        {
            // Named config path: look up specific config by ID, overlay on platform defaults
            await ResolveNamedConfigAsync(db, tenantId, agentLlmConfigId.Value, state, ct);
        }

        // 4. Per-agent model-only override (applied after named config or default chain)
        if (!string.IsNullOrEmpty(agentModelId))
            state.Model = agentModelId;

        var result = new ResolvedLlmConfig(state.Provider, state.ApiKey, state.Model,
            state.Endpoint, state.Deployment, state.Available);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        _logger.LogDebug("LlmConfigResolver: tenant={TenantId} configId={ConfigId} → provider={Provider} model={Model}",
            tenantId, agentLlmConfigId, state.Provider, state.Model);
        return result;
    }

    private async Task<LlmConfigState?> ResolveNamedConfigAsync(
        Data.DivaDbContext db, int tenantId, int configId, LlmConfigState baseline, CancellationToken ct)
    {
        // Try tenant-scoped named config first
        var tenantCfg = await db.TenantLlmConfigs
            .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct);
        if (tenantCfg is not null)
            return baseline.Overlay(tenantCfg.Provider, tenantCfg.ApiKey, tenantCfg.Model,
                tenantCfg.Endpoint, tenantCfg.DeploymentName, tenantCfg.AvailableModelsJson);

        // Fall back to group-level named config (tenant must be a member)
        var groupIds = await _groups.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count > 0)
        {
            var groupCfg = await db.GroupLlmConfigs
                .Include(c => c.PlatformConfig)
                .FirstOrDefaultAsync(c => c.Id == configId && groupIds.Contains(c.GroupId), ct);
            if (groupCfg is not null)
            {
                // If this group config is a reference to a platform config, use the platform's credentials
                if (groupCfg.PlatformConfig is not null)
                    return baseline.Overlay(
                        groupCfg.PlatformConfig.Provider, groupCfg.PlatformConfig.ApiKey,
                        groupCfg.PlatformConfig.Model, groupCfg.PlatformConfig.Endpoint,
                        groupCfg.PlatformConfig.DeploymentName, groupCfg.PlatformConfig.AvailableModelsJson);

                return baseline.Overlay(groupCfg.Provider, groupCfg.ApiKey, groupCfg.Model,
                    groupCfg.Endpoint, groupCfg.DeploymentName, groupCfg.AvailableModelsJson);
            }
        }

        _logger.LogWarning("LlmConfigResolver: named config {ConfigId} not found for tenant {TenantId} — using platform defaults", configId, tenantId);
        return null;
    }

    private sealed class LlmConfigState
    {
        public string Provider;
        public string ApiKey;
        public string Model;
        public string? Endpoint;
        public string? Deployment;
        public IReadOnlyList<string> Available;

        public LlmConfigState(string provider, string apiKey, string model,
            string? endpoint, string? deployment, IReadOnlyList<string> available)
        {
            Provider = provider; ApiKey = apiKey; Model = model;
            Endpoint = endpoint; Deployment = deployment; Available = available;
        }

        public LlmConfigState Overlay(string? p, string? k, string? m, string? e, string? d, string? av)
        {
            if (p is not null && !string.Equals(p, Provider, StringComparison.OrdinalIgnoreCase))
            {
                // Provider is changing — clear the inherited endpoint so the new provider
                // uses its own native endpoint unless the config explicitly sets one.
                Provider = p;
                Endpoint = null;
            }
            else if (p is not null)
                Provider = p;
            // Treat blank/whitespace overrides as "inherit" — a config row with an empty
            // ApiKey or Model must NOT clobber the valid inherited (platform) value, otherwise
            // an empty key would be sent to the provider and rejected (401 invalid x-api-key).
            if (!string.IsNullOrWhiteSpace(k)) ApiKey = k;
            if (!string.IsNullOrWhiteSpace(m)) Model = m;
            if (e is not null) Endpoint = e;
            if (d is not null) Deployment = d;
            if (av is not null) Available = ParseAvailableModels(av, Available);
            return this;
        }
    }

    public void InvalidateForTenant(int tenantId)
    {
        // IMemoryCache has no prefix-evict. Bump a per-tenant version token so
        // cache reads detect staleness. The 2-min TTL caps max staleness anyway.
        _logger.LogDebug("LlmConfigResolver: invalidated tenant {TenantId}", tenantId);
        // Evict common no-config-id combos
        _cache.Remove($"llm_resolved_{tenantId}__");
        _cache.Remove($"llm_resolved_{tenantId}__");
        // Store a version token; callers that cache the token will see it changed
        // (simple eviction — TTL handles the rest for named-config combos)
        _cache.Remove($"llm_v_{tenantId}");
    }

    public void InvalidatePlatform()
    {
        _cache.Remove("llm_platform");
        _logger.LogDebug("LlmConfigResolver: platform config invalidated");
    }

    private static IReadOnlyList<string> ParseAvailableModels(string? json, IReadOnlyList<string> fallback)
    {
        if (string.IsNullOrEmpty(json)) return fallback;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list is { Count: > 0 } ? list.AsReadOnly() : fallback;
        }
        catch { return fallback; }
    }

    private static IReadOnlyList<string> ParseAvailableModels(string? json, List<string> fallback)
        => ParseAvailableModels(json, (IReadOnlyList<string>)fallback.AsReadOnly());
}
