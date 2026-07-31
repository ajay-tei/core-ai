using System.Text.Json;
using Diva.Core.Models;
using Diva.Core.Models.Widgets;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diva.TenantAdmin.Services;

public sealed class WidgetConfigService : IWidgetConfigService
{
    private readonly IDatabaseProviderFactory _db;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WidgetConfigService(IDatabaseProviderFactory db) => _db = db;

    public async Task<List<WidgetConfigDto>> GetForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entities = await db.WidgetConfigs
            .AsNoTracking()
            .Where(w => w.TenantId == tenantId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(ToDto).ToList();
    }

    public async Task<WidgetConfigEntity?> GetByIdAsync(string widgetId, CancellationToken ct = default)
    {
        // CreateDbContext(null) → tenantId = 0 → bypasses EF query filter
        using var db = _db.CreateDbContext();
        return await db.WidgetConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == widgetId, ct);
    }

    public async Task<WidgetConfigDto> CreateAsync(int tenantId, CreateWidgetRequest request, CancellationToken ct = default)
    {
        var entity = new WidgetConfigEntity
        {
            TenantId = tenantId,
            AgentId = request.AgentId,
            Name = request.Name,
            AllowedOriginsJson = SerializeOrigins(request.AllowedOrigins),
            SsoConfigId = request.SsoConfigId,
            AllowAnonymous = request.AllowAnonymous,
            WelcomeMessage = request.WelcomeMessage,
            PlaceholderText = request.PlaceholderText,
            ThemeJson = request.Theme is null ? null : JsonSerializer.Serialize(request.Theme, _jsonOpts),
            RespectSystemTheme = request.RespectSystemTheme,
            ShowBranding = request.ShowBranding,
            ExpiresAt = request.ExpiresAt,
            EnvironmentId = request.EnvironmentId,
        };

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        db.WidgetConfigs.Add(entity);
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task<WidgetConfigDto> UpdateAsync(int tenantId, string id, CreateWidgetRequest request, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.WidgetConfigs.FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new KeyNotFoundException($"Widget {id} not found.");

        entity.AgentId = request.AgentId;
        entity.Name = request.Name;
        entity.AllowedOriginsJson = SerializeOrigins(request.AllowedOrigins);
        entity.SsoConfigId = request.SsoConfigId;
        entity.AllowAnonymous = request.AllowAnonymous;
        entity.WelcomeMessage = request.WelcomeMessage;
        entity.PlaceholderText = request.PlaceholderText;
        entity.ThemeJson = request.Theme is null ? null : JsonSerializer.Serialize(request.Theme, _jsonOpts);
        entity.RespectSystemTheme = request.RespectSystemTheme;
        entity.ShowBranding = request.ShowBranding;
        entity.ExpiresAt = request.ExpiresAt;
        entity.EnvironmentId = request.EnvironmentId;

        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task DeleteAsync(int tenantId, string id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.WidgetConfigs.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (entity is null) return;
        db.WidgetConfigs.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WidgetConfigDto ToDto(WidgetConfigEntity e) => new(
        e.Id,
        e.TenantId,
        e.AgentId,
        e.Name,
        DeserializeOrigins(e.AllowedOriginsJson),
        e.SsoConfigId,
        e.AllowAnonymous,
        e.WelcomeMessage,
        e.PlaceholderText,
        DeserializeTheme(e.ThemeJson),
        e.RespectSystemTheme,
        e.ShowBranding,
        e.IsActive,
        e.CreatedAt,
        e.ExpiresAt,
        e.EnvironmentId);

    private static string? SerializeOrigins(string[]? origins) =>
        origins is { Length: > 0 } ? JsonSerializer.Serialize(origins) : null;

    private static string[] DeserializeOrigins(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<string[]>(json) ?? [];

    private static WidgetTheme DeserializeTheme(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return WidgetTheme.Light;
        return JsonSerializer.Deserialize<WidgetTheme>(json, _jsonOpts) ?? WidgetTheme.Light;
    }
}
