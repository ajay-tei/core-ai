using Diva.Infrastructure.Auth;
using Diva.Infrastructure.LiteLLM;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;

namespace Diva.Host.Controllers;

/// <summary>
/// Manages LLM configuration at platform (global) and per-tenant levels.
/// Platform endpoints: master admin only.
/// Tenant endpoints: master admin (any tenant via ?tenantId=N) or tenant admin (own tenant only).
/// </summary>
[ApiController]
public class LlmConfigController : ControllerBase
{
    private readonly ITenantGroupService _groups;
    private readonly ILlmConfigResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;

    public LlmConfigController(
        ITenantGroupService groups,
        ILlmConfigResolver resolver,
        IHttpClientFactory httpClientFactory)
    {
        _groups = groups;
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
    }

    private IActionResult? RequireMasterAdmin()
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx?.IsMasterAdmin == true ? null : StatusCode(403, "Master admin access required.");
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── Platform (global) LLM config ──────────────────────────────────────────

    // GET /api/platform/llm-config
    [HttpGet("api/platform/llm-config")]
    public async Task<IActionResult> GetPlatformLlmConfig(CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.GetPlatformLlmConfigAsync(ct);
        if (config is null) return Ok(new { seededFromAppSettings = true });
        return Ok(new
        {
            config.Id, config.Provider,
            apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
            config.Model, config.Endpoint, config.DeploymentName,
            config.AvailableModelsJson, config.UpdatedAt,
            seededFromAppSettings = false,
        });
    }

    // PUT /api/platform/llm-config
    [HttpPut("api/platform/llm-config")]
    public async Task<IActionResult> UpsertPlatformLlmConfig([FromBody] UpsertLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.UpsertPlatformLlmConfigAsync(dto, ct);
        return Ok(new
        {
            config.Id, config.Provider,
            apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
            config.Model, config.Endpoint, config.DeploymentName,
            config.AvailableModelsJson, config.UpdatedAt,
        });
    }

    // ── Platform LLM config catalog (multiple named configs) ──────────────────

    // GET /api/platform/llm-configs
    [HttpGet("api/platform/llm-configs")]
    public async Task<IActionResult> ListPlatformLlmConfigs(CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var configs = await _groups.ListPlatformLlmConfigsAsync(ct);
        return Ok(configs.Select(MapPlatformConfig));
    }

    // POST /api/platform/llm-configs
    [HttpPost("api/platform/llm-configs")]
    public async Task<IActionResult> CreatePlatformLlmConfig([FromBody] CreatePlatformLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        var config = await _groups.CreatePlatformLlmConfigAsync(dto, ct);
        return Ok(MapPlatformConfig(config));
    }

    // PUT /api/platform/llm-configs/{id}
    [HttpPut("api/platform/llm-configs/{id:int}")]
    public async Task<IActionResult> UpdatePlatformLlmConfig(int id, [FromBody] UpsertLlmConfigDto dto, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try
        {
            var config = await _groups.UpdatePlatformLlmConfigAsync(id, dto, ct);
            return Ok(MapPlatformConfig(config));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/platform/llm-configs/{id}
    [HttpDelete("api/platform/llm-configs/{id:int}")]
    public async Task<IActionResult> DeletePlatformLlmConfig(int id, CancellationToken ct)
    {
        if (RequireMasterAdmin() is { } err) return err;
        try { await _groups.DeletePlatformLlmConfigAsync(id, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private static object MapPlatformConfig(Diva.Infrastructure.Data.Entities.PlatformLlmConfigEntity c) => new
    {
        c.Id, c.Name, c.Provider,
        apiKey = !string.IsNullOrEmpty(c.ApiKey) ? "••••••••" : (string?)null,
        c.Model, c.Endpoint, c.DeploymentName, c.AvailableModelsJson, c.UpdatedAt,
    };

    // ── Per-tenant LLM config ─────────────────────────────────────────────────

    // GET /api/admin/llm-config?tenantId=N
    [HttpGet("api/admin/llm-config")]
    public async Task<IActionResult> GetTenantLlmConfig(
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var config = await _groups.GetTenantLlmConfigAsync(tid, ct);
        if (config is null) return Ok(null);
        return Ok(new
        {
            config.Id, config.TenantId, config.Provider,
            apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
            config.Model, config.Endpoint, config.DeploymentName,
            config.AvailableModelsJson, config.UpdatedAt,
        });
    }

    // PUT /api/admin/llm-config?tenantId=N
    [HttpPut("api/admin/llm-config")]
    public async Task<IActionResult> UpsertTenantLlmConfig(
        [FromBody] UpsertLlmConfigDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var config = await _groups.UpsertTenantLlmConfigAsync(tid, dto, ct);
        return Ok(new
        {
            config.Id, config.TenantId, config.Provider,
            apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
            config.Model, config.Endpoint, config.DeploymentName,
            config.AvailableModelsJson, config.UpdatedAt,
        });
    }

    // DELETE /api/admin/llm-config?tenantId=N
    [HttpDelete("api/admin/llm-config")]
    public async Task<IActionResult> DeleteTenantLlmConfig(
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        await _groups.DeleteTenantLlmConfigAsync(tid, ct);
        return NoContent();
    }

    // ── Named LLM configs for agent picker ────────────────────────────────────

    // GET /api/admin/llm-configs?tenantId=N  — list all named configs for a tenant
    [HttpGet("api/admin/llm-configs")]
    public async Task<IActionResult> ListTenantLlmConfigs(
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var configs = await _groups.ListTenantLlmConfigsAsync(tid, ct);
        return Ok(configs.Select(c => new
        {
            c.Id, c.TenantId, c.Name, c.Provider,
            apiKey = c.ApiKey is not null ? "••••••••" : (string?)null,
            c.Model, c.Endpoint, c.DeploymentName, c.AvailableModelsJson, c.UpdatedAt,
        }));
    }

    // POST /api/admin/llm-configs?tenantId=N  — create a named config
    [HttpPost("api/admin/llm-configs")]
    public async Task<IActionResult> CreateTenantLlmConfig(
        [FromBody] CreateNamedLlmConfigDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var config = await _groups.CreateTenantLlmConfigAsync(tid, dto, ct);
        return Ok(new
        {
            config.Id, config.TenantId, config.Name, config.Provider,
            apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
            config.Model, config.Endpoint, config.DeploymentName, config.AvailableModelsJson, config.UpdatedAt,
        });
    }

    // PUT /api/admin/llm-configs/{id}?tenantId=N  — update a named config
    [HttpPut("api/admin/llm-configs/{id:int}")]
    public async Task<IActionResult> UpdateTenantLlmConfig(
        int id, [FromBody] UpsertLlmConfigDto dto,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        try
        {
            var config = await _groups.UpdateTenantLlmConfigByIdAsync(id, tid, dto, ct);
            return Ok(new
            {
                config.Id, config.TenantId, config.Name, config.Provider,
                apiKey = config.ApiKey is not null ? "••••••••" : (string?)null,
                config.Model, config.Endpoint, config.DeploymentName, config.AvailableModelsJson, config.UpdatedAt,
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // DELETE /api/admin/llm-configs/{id}?tenantId=N  — delete a named config
    [HttpDelete("api/admin/llm-configs/{id:int}")]
    public async Task<IActionResult> DeleteTenantLlmConfigById(
        int id, [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        try { await _groups.DeleteTenantLlmConfigByIdAsync(id, tid, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // GET /api/admin/llm-configs/available?tenantId=N  — all named configs for agent picker (tenant + groups)
    [HttpGet("api/admin/llm-configs/available")]
    public async Task<IActionResult> ListAvailableLlmConfigs(
        [FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        var configs = await _groups.ListAvailableLlmConfigsForTenantAsync(tid, ct);
        return Ok(configs);
    }

    // GET /api/admin/llm-configs/{id}/models?tenantId=N
    // Returns available model IDs from the provider's /v1/models API for the resolved config.
    // Respects tenant → group → platform credential hierarchy via ILlmConfigResolver.
    [HttpGet("api/admin/llm-configs/{id:int}/models")]
    public async Task<IActionResult> GetLlmConfigModels(
        int id, [FromQuery] int tenantId = 1, [FromQuery] int environmentId = 0, CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);
        ResolvedLlmConfig resolved;
        try { resolved = await _resolver.ResolveAsync(tid, id, null, environmentId, ct); }
        catch { return Ok(Array.Empty<string>()); }

        var models = await FetchProviderModelsAsync(resolved, ct);

        // Ensure the configured default model is always present
        if (!string.IsNullOrEmpty(resolved.Model) && !models.Contains(resolved.Model))
            models.Insert(0, resolved.Model);

        return Ok(models);
    }

    private async Task<List<string>> FetchProviderModelsAsync(ResolvedLlmConfig config, CancellationToken ct)
    {
        // Fall back to DB-configured list immediately if we have one
        var dbModels = config.AvailableModels.ToList();

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            string url;
            if (config.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://api.anthropic.com/v1/models?limit=100";
                http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                var endpoint = config.Endpoint?.TrimEnd('/') ?? "https://api.openai.com";
                url = $"{endpoint}/v1/models";
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.ApiKey);
            }

            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return dbModels;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data))
                return dbModels;

            var fetched = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idProp))
                {
                    var modelId = idProp.GetString();
                    if (!string.IsNullOrEmpty(modelId))
                        fetched.Add(modelId);
                }
            }

            return fetched.Count > 0 ? fetched : dbModels;
        }
        catch
        {
            return dbModels;
        }
    }
}
