using System.Text;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Services;

/// <summary>
/// Structured key-value user preference service. Uses SQLite for fast, deterministic CRUD.
/// No vector search — preferences are retrieved by exact match on (tenant, user, category, key).
/// </summary>
public sealed class UserPreferenceService : IUserPreferenceService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<UserPreferenceService> _logger;

    public UserPreferenceService(IDatabaseProviderFactory db, ILogger<UserPreferenceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<UserPreferenceDto> SaveAsync(int tenantId, string userId, string category, string key, string value,
        CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var existing = await db.UserPreferences
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.UserId == userId
                                      && p.Category == category && p.Key == key, ct);

        if (existing is not null)
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new UserPreferenceEntity
            {
                TenantId = tenantId,
                UserId = userId,
                Category = category,
                Key = key,
                Value = value,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.UserPreferences.Add(existing);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Saved user preference {Category}/{Key} for user {UserId}", category, key, userId);
        return ToDto(existing);
    }

    public async Task<IReadOnlyList<UserPreferenceDto>> GetAsync(int tenantId, string userId, string? category = null,
        CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        IQueryable<UserPreferenceEntity> q = db.UserPreferences
            .Where(p => p.TenantId == tenantId && p.UserId == userId);

        if (!string.IsNullOrEmpty(category))
            q = q.Where(p => p.Category == category);

        var entities = await q.OrderBy(p => p.Category).ThenBy(p => p.Key).ToListAsync(ct);
        return entities.Select(ToDto).ToList();
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.UserPreferences.FindAsync([id], ct);
        if (entity is null) return false;

        db.UserPreferences.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteCategoryAsync(int tenantId, string userId, string category, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var entities = await db.UserPreferences
            .Where(p => p.TenantId == tenantId && p.UserId == userId && p.Category == category)
            .ToListAsync(ct);

        if (entities.Count == 0) return 0;

        db.UserPreferences.RemoveRange(entities);
        await db.SaveChangesAsync(ct);
        return entities.Count;
    }

    public async Task<string?> GetPromptContextAsync(int tenantId, string userId, CancellationToken ct = default)
    {
        var prefs = await GetAsync(tenantId, userId, ct: ct);
        if (prefs.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## User Preferences");
        var grouped = prefs.GroupBy(p => p.Category);
        foreach (var group in grouped)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var pref in group)
                sb.AppendLine($"- {pref.Key}: {pref.Value}");
        }
        return sb.ToString().TrimEnd();
    }

    private static UserPreferenceDto ToDto(UserPreferenceEntity e) =>
        new(e.Id, e.UserId, e.Category, e.Key, e.Value, e.CreatedAt, e.UpdatedAt);
}
