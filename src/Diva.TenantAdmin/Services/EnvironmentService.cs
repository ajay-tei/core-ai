using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>Request payload for creating/updating a tenant environment.</summary>
public sealed record EnvironmentDto(string Slug, string DisplayName, int Rank, bool IsDefault);

public interface IEnvironmentService
{
    Task<List<TenantEnvironmentEntity>> ListAsync(int tenantId, CancellationToken ct);
    Task<TenantEnvironmentEntity?> GetAsync(int tenantId, int id, CancellationToken ct);

    /// <summary>Returns the tenant's IsDefault environment, or null if the tenant has none configured
    /// (e.g. a brand-new tenant before Program.cs's backfill has run). Used by TenantContextMiddleware
    /// (Phase E) to resolve EnvironmentId for JWT/SSO callers with no explicit X-Environment header.</summary>
    Task<TenantEnvironmentEntity?> GetDefaultAsync(int tenantId, CancellationToken ct);

    Task<(TenantEnvironmentEntity? Entity, string? Error)> CreateAsync(int tenantId, EnvironmentDto dto, CancellationToken ct);
    Task<(TenantEnvironmentEntity? Entity, string? Error)> UpdateAsync(int tenantId, int id, EnvironmentDto dto, CancellationToken ct);
    Task<(bool Success, string? Error)> DeleteAsync(int tenantId, int id, CancellationToken ct);
}

/// <summary>
/// Basic CRUD for a tenant's environment pipeline (foundation only — promotion/versioning,
/// draft isolation, and runtime routing are separate, not-yet-built phases). Singleton-safe —
/// creates a new DbContext per call via <see cref="IDatabaseProviderFactory"/>.
/// </summary>
public sealed class EnvironmentService : IEnvironmentService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<EnvironmentService> _logger;

    public EnvironmentService(IDatabaseProviderFactory db, ILogger<EnvironmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<TenantEnvironmentEntity>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantEnvironments
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Rank)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<TenantEnvironmentEntity?> GetAsync(int tenantId, int id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantEnvironments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
    }

    public async Task<TenantEnvironmentEntity?> GetDefaultAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantEnvironments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsDefault, ct);
    }

    public async Task<(TenantEnvironmentEntity? Entity, string? Error)> CreateAsync(int tenantId, EnvironmentDto dto, CancellationToken ct)
    {
        var slug = dto.Slug.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug)) return (null, "Slug is required.");

        using var db = _db.CreateDbContext();
        var slugTaken = await db.TenantEnvironments.AnyAsync(e => e.TenantId == tenantId && e.Slug == slug, ct);
        if (slugTaken) return (null, $"An environment with slug \"{slug}\" already exists for this tenant.");

        if (dto.IsDefault)
            await ClearOtherDefaultsAsync(db, tenantId, currentId: null, ct);

        var entity = new TenantEnvironmentEntity
        {
            TenantId = tenantId,
            Slug = slug,
            DisplayName = dto.DisplayName.Trim(),
            Rank = dto.Rank,
            IsDefault = dto.IsDefault,
            CreatedAt = DateTime.UtcNow,
        };
        db.TenantEnvironments.Add(entity);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Environment created: {Slug} ({Id}) for tenant {TenantId}", entity.Slug, entity.Id, tenantId);
        return (entity, null);
    }

    public async Task<(TenantEnvironmentEntity? Entity, string? Error)> UpdateAsync(int tenantId, int id, EnvironmentDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.TenantEnvironments.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (entity is null) return (null, "Environment not found.");

        var slug = dto.Slug.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug)) return (null, "Slug is required.");
        var slugTaken = await db.TenantEnvironments.AnyAsync(e => e.TenantId == tenantId && e.Slug == slug && e.Id != id, ct);
        if (slugTaken) return (null, $"An environment with slug \"{slug}\" already exists for this tenant.");

        // Guard: can't un-default the tenant's only/last default environment without another
        // environment already set as default — every tenant must always have exactly one.
        if (entity.IsDefault && !dto.IsDefault)
        {
            var hasOtherDefault = await db.TenantEnvironments.AnyAsync(e => e.TenantId == tenantId && e.Id != id && e.IsDefault, ct);
            if (!hasOtherDefault)
                return (null, "At least one environment must be marked as default — set another environment as default first.");
        }
        if (dto.IsDefault)
            await ClearOtherDefaultsAsync(db, tenantId, currentId: id, ct);

        entity.Slug = slug;
        entity.DisplayName = dto.DisplayName.Trim();
        entity.Rank = dto.Rank;
        entity.IsDefault = dto.IsDefault;
        await db.SaveChangesAsync(ct);
        return (entity, null);
    }

    public async Task<(bool Success, string? Error)> DeleteAsync(int tenantId, int id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.TenantEnvironments.FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
        if (entity is null) return (false, null);

        var isOnlyEnvironment = await db.TenantEnvironments.CountAsync(e => e.TenantId == tenantId, ct) == 1;
        if (isOnlyEnvironment)
            return (false, "Cannot delete a tenant's only environment.");

        db.TenantEnvironments.Remove(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // FK is Restrict on all 4 promotable entity types — translate the DB-level
            // constraint violation into an actionable message rather than a raw 500.
            return (false, "This environment still has agents, MCP servers, scheduled tasks, or agent groups tagged to it. Reassign or remove them first.");
        }
        return (true, null);
    }

    /// <summary>Clears IsDefault on every other environment for the tenant (only one default at a time).</summary>
    private static async Task ClearOtherDefaultsAsync(DivaDbContext db, int tenantId, int? currentId, CancellationToken ct)
    {
        var others = await db.TenantEnvironments
            .Where(e => e.TenantId == tenantId && e.IsDefault && e.Id != (currentId ?? -1))
            .ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
    }
}
