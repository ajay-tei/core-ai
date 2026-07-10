using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// CRUD for tenant-scoped user groups. Member and role rows are replaced wholesale on
/// update. After every write the shared <see cref="IUserGroupResolver"/> membership cache
/// is invalidated so access/credential decisions pick up the change. Singleton-safe via
/// <see cref="IDatabaseProviderFactory"/>.
/// </summary>
public sealed class UserGroupService : IUserGroupService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IUserGroupResolver _resolver;
    private readonly ILogger<UserGroupService> _logger;

    public UserGroupService(
        IDatabaseProviderFactory db,
        IUserGroupResolver resolver,
        ILogger<UserGroupService> logger)
    {
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<List<UserGroupEntity>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.UserGroups
            .Where(g => g.TenantId == tenantId)
            .Include(g => g.Members)
            .Include(g => g.Roles)
            .OrderBy(g => g.Name)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<UserGroupEntity?> GetAsync(int tenantId, int id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.UserGroups
            .Where(g => g.Id == id && g.TenantId == tenantId)
            .Include(g => g.Members)
            .Include(g => g.Roles)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
    }

    public async Task<UserGroupEntity> CreateAsync(int tenantId, UserGroupDto dto, string? createdByUserId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new UserGroupEntity
        {
            TenantId = tenantId,
            Name = dto.Name,
            Description = dto.Description,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = createdByUserId,
            Members = BuildMembers(tenantId, dto.Members),
            Roles = BuildRoles(tenantId, dto.Roles),
        };
        db.UserGroups.Add(entity);
        await db.SaveChangesAsync(ct);
        _resolver.Invalidate(tenantId);
        _logger.LogInformation("User group created: {Name} ({Id}) for tenant {TenantId}", entity.Name, entity.Id, tenantId);
        return entity;
    }

    public async Task<UserGroupEntity?> UpdateAsync(int tenantId, int id, UserGroupDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.UserGroups
            .Where(g => g.Id == id && g.TenantId == tenantId)
            .Include(g => g.Members)
            .Include(g => g.Roles)
            .FirstOrDefaultAsync(ct);
        if (entity is null) return null;

        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.UpdatedAt = DateTime.UtcNow;

        // Replace children wholesale (cascade delete removes the old rows).
        db.UserGroupMembers.RemoveRange(entity.Members);
        db.UserGroupRoles.RemoveRange(entity.Roles);
        entity.Members = BuildMembers(tenantId, dto.Members);
        entity.Roles = BuildRoles(tenantId, dto.Roles);

        await db.SaveChangesAsync(ct);
        _resolver.Invalidate(tenantId);
        return entity;
    }

    public async Task<bool> DeleteAsync(int tenantId, int id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id && g.TenantId == tenantId, ct);
        if (entity is null) return false;

        db.UserGroups.Remove(entity);   // cascade removes members/roles/junction rows
        await db.SaveChangesAsync(ct);
        _resolver.Invalidate(tenantId);
        return true;
    }

    private static List<UserGroupMemberEntity> BuildMembers(int tenantId, IReadOnlyList<UserGroupMemberDto> members)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UserGroupMemberEntity>();
        foreach (var m in members)
        {
            var userId = (m.UserId ?? string.Empty).Trim();
            var email = m.Email?.Trim();
            if (string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(email)) continue;

            // Dedupe on the identity key (prefer user id, fall back to email).
            var dedupeKey = !string.IsNullOrEmpty(userId) ? "u:" + userId : "e:" + email;
            if (!seen.Add(dedupeKey)) continue;

            result.Add(new UserGroupMemberEntity { TenantId = tenantId, UserId = userId, Email = email });
        }
        return result;
    }

    private static List<UserGroupRoleEntity> BuildRoles(int tenantId, IReadOnlyList<string> roles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UserGroupRoleEntity>();
        foreach (var role in roles)
        {
            var r = (role ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(r) || !seen.Add(r)) continue;
            result.Add(new UserGroupRoleEntity { TenantId = tenantId, Role = r });
        }
        return result;
    }
}
