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

    public async Task<ResolvedLlmConfig> ResolveAsync(int tenantId, int? agentLlmConfigId, string? agentModelId, int environmentId, CancellationToken ct)
    {
        // Version token makes InvalidateForTenant actually take effect immediately: bumping it
        // changes every cache key for this tenant at once, so a stale entry (e.g. someone just
        // edited this tenant's named config) is never read again — it just ages out on its own
        // 2-minute TTL instead of leaking. Without this, InvalidateForTenant only ever cleared the
        // no-config-id combo, so any named-config resolution kept serving the pre-edit value for up
        // to 2 minutes after a save.
        var tenantVersion = _cache.Get<string>($"llm_v_{tenantId}") ?? "0";
        var cacheKey = $"llm_resolved_{tenantId}_{agentLlmConfigId?.ToString() ?? ""}_{environmentId}_{agentModelId ?? ""}_{tenantVersion}";
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
            await ResolveNamedConfigAsync(db, tenantId, agentLlmConfigId.Value, environmentId, state, ct);
        }

        // 4. Per-agent model-only override (applied after named config or default chain)
        if (!string.IsNullOrEmpty(agentModelId))
            state.Model = agentModelId;

        var result = new ResolvedLlmConfig(state.Provider, state.ApiKey, state.Model,
            state.Endpoint, state.Deployment, state.Available, state.ApiKeys);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(2));
        _logger.LogDebug("LlmConfigResolver: tenant={TenantId} configId={ConfigId} environment={EnvironmentId} → provider={Provider} model={Model}",
            tenantId, agentLlmConfigId, environmentId, state.Provider, state.Model);
        return result;
    }

    private async Task<LlmConfigState?> ResolveNamedConfigAsync(
        Data.DivaDbContext db, int tenantId, int configId, int environmentId, LlmConfigState baseline, CancellationToken ct)
    {
        // Try tenant-scoped named config first
        var tenantCfg = await db.TenantLlmConfigs
            .FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct);
        if (tenantCfg is not null)
        {
            // Re-look-up by (Name, environmentId) so the resolved key always matches the CALLER's
            // own environment rather than whichever environment's row the stored Id happens to be —
            // this is what makes promotion "just work": the Id only tells us WHICH named config,
            // the Name+environment lookup decides WHICH KEY.
            var effective = await ResolveTenantConfigByNameAsync(db, tenantId, tenantCfg.Name, environmentId, ct) ?? tenantCfg;
            return baseline.Overlay(effective.Provider, effective.ApiKey, effective.Model,
                effective.Endpoint, effective.DeploymentName, effective.AvailableModelsJson, effective.AdditionalApiKeysJson);
        }

        // Fall back to group-level named config (tenant must be a member)
        var groupIds = await _groups.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count > 0)
        {
            var groupCfg = await db.GroupLlmConfigs
                .Include(c => c.PlatformConfig)
                .FirstOrDefaultAsync(c => c.Id == configId && groupIds.Contains(c.GroupId), ct);
            if (groupCfg is not null)
            {
                // If this group config is a reference to a platform config, use the platform's
                // credentials as-is — PlatformLlmConfigEntity is not environment-scoped (Phase G
                // decision: only Tenant + Group tiers get per-environment keys).
                if (groupCfg.PlatformConfig is not null)
                    return baseline.Overlay(
                        groupCfg.PlatformConfig.Provider, groupCfg.PlatformConfig.ApiKey,
                        groupCfg.PlatformConfig.Model, groupCfg.PlatformConfig.Endpoint,
                        groupCfg.PlatformConfig.DeploymentName, groupCfg.PlatformConfig.AvailableModelsJson, null);

                var effective = await ResolveGroupConfigByNameAsync(db, groupCfg.GroupId, groupCfg.Name, environmentId, ct) ?? groupCfg;
                return baseline.Overlay(effective.Provider, effective.ApiKey, effective.Model,
                    effective.Endpoint, effective.DeploymentName, effective.AvailableModelsJson, null);
            }
        }

        _logger.LogWarning("LlmConfigResolver: named config {ConfigId} not found for tenant {TenantId} — using platform defaults", configId, tenantId);
        return null;
    }

    /// <summary>
    /// Resolves the effective row for (tenantId, name) scoped to environmentId. Falls back to an
    /// untagged row (EnvironmentId == null — pre-Phase-G data, or a config that simply hasn't been
    /// tagged yet) rather than a DIFFERENT tagged environment's row, so a Staging key can never leak
    /// into a Production resolution. Returns null (caller falls back to the by-Id row) if neither exists.
    /// </summary>
    private static async Task<Data.Entities.TenantLlmConfigEntity?> ResolveTenantConfigByNameAsync(
        Data.DivaDbContext db, int tenantId, string? name, int environmentId, CancellationToken ct)
    {
        if (environmentId > 0)
        {
            var scoped = await db.TenantLlmConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == name && c.EnvironmentId == environmentId, ct);
            if (scoped is not null) return scoped;
        }

        return await db.TenantLlmConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == name && c.EnvironmentId == null, ct);
    }

    /// <summary>Group-level equivalent of <see cref="ResolveTenantConfigByNameAsync"/>.</summary>
    private static async Task<Data.Entities.GroupLlmConfigEntity?> ResolveGroupConfigByNameAsync(
        Data.DivaDbContext db, int groupId, string? name, int environmentId, CancellationToken ct)
    {
        if (environmentId > 0)
        {
            var scoped = await db.GroupLlmConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.GroupId == groupId && c.Name == name && c.EnvironmentId == environmentId, ct);
            if (scoped is not null) return scoped;
        }

        return await db.GroupLlmConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GroupId == groupId && c.Name == name && c.EnvironmentId == null, ct);
    }

    private sealed class LlmConfigState
    {
        public string Provider;
        public string ApiKey;
        public string Model;
        public string? Endpoint;
        public string? Deployment;
        public IReadOnlyList<string> Available;
        public IReadOnlyList<string>? ApiKeys;

        public LlmConfigState(string provider, string apiKey, string model,
            string? endpoint, string? deployment, IReadOnlyList<string> available)
        {
            Provider = provider; ApiKey = apiKey; Model = model;
            Endpoint = endpoint; Deployment = deployment; Available = available;
            ApiKeys = null;
        }

        public LlmConfigState Overlay(string? p, string? k, string? m, string? e, string? d, string? av, string? additionalKeysJson)
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
            if (!string.IsNullOrWhiteSpace(k))
            {
                ApiKey = k;
                // Additional keys are only meaningful alongside the SAME row's own primary key —
                // when a more specific level overrides ApiKey, its own additional-keys pool (if
                // any) replaces whatever pool an earlier/less-specific level may have set.
                ApiKeys = BuildPool(k, additionalKeysJson);
            }
            if (!string.IsNullOrWhiteSpace(m)) Model = m;
            if (e is not null) Endpoint = e;
            if (d is not null) Deployment = d;
            if (av is not null) Available = ParseAvailableModels(av, Available);
            return this;
        }

        private static IReadOnlyList<string>? BuildPool(string primaryKey, string? additionalKeysJson)
        {
            if (string.IsNullOrWhiteSpace(additionalKeysJson)) return null;
            List<string>? additional;
            try { additional = JsonSerializer.Deserialize<List<string>>(additionalKeysJson); }
            catch { return null; }
            if (additional is null or { Count: 0 }) return null;

            var pool = new List<string> { primaryKey };
            foreach (var key in additional)
                if (!string.IsNullOrWhiteSpace(key) && !pool.Contains(key, StringComparer.Ordinal))
                    pool.Add(key);
            return pool.Count > 1 ? pool : null;
        }
    }

    public void InvalidateForTenant(int tenantId)
    {
        // Bump the version token baked into every ResolveAsync cache key for this tenant — every
        // previously-cached entry (regardless of which agentLlmConfigId/environmentId/model combo
        // it was keyed under) instantly becomes unreachable, forcing a fresh DB read on next use.
        // No prefix-evict needed, and no risk of missing a specific combo.
        _cache.Set($"llm_v_{tenantId}", Guid.NewGuid().ToString("N"));
        _logger.LogDebug("LlmConfigResolver: invalidated tenant {TenantId}", tenantId);
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
