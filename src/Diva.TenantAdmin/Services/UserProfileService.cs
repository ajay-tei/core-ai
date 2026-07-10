using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// Manages per-tenant user profiles.
/// Upserts a profile record on every authenticated request (login tracking).
/// Provides admin CRUD for display name, avatar, agent access overrides, and active status.
/// Singleton-safe: uses IDatabaseProviderFactory per call.
/// </summary>
public sealed class UserProfileService : IUserProfileService, IUserLoginTracker
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UserProfileService> _logger;

    private const string ActiveCachePrefix = "user:active:";
    private static readonly TimeSpan ActiveCacheTtl = TimeSpan.FromMinutes(5);

    public UserProfileService(
        IDatabaseProviderFactory db,
        IMemoryCache cache,
        ILogger<UserProfileService> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    // ── Login upsert ──────────────────────────────────────────────────────────

    public async Task UpsertOnLoginAsync(TenantContext tenant, CancellationToken ct = default)
    {
        if (tenant.TenantId <= 0 || string.IsNullOrEmpty(tenant.UserId)) return;

        using var db = _db.CreateDbContext(tenant);

        // Phase 1: exact match by UserId (SSO sub claim) — fast, covered by unique index
        var profile = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == tenant.UserId, ct);

        // Phase 2: email-based fallback — handles the case where the same person logs in
        // after an SSO provider change (new sub claim) or when a local user pre-exists
        // with the same email before their first SSO login.
        if (profile is null && !string.IsNullOrEmpty(tenant.UserEmail))
        {
            var emailLower = tenant.UserEmail.ToLowerInvariant();
            profile = await db.UserProfiles
                .FirstOrDefaultAsync(p => p.Email.ToLower() == emailLower, ct);

            if (profile is not null)
            {
                // Link the current SSO sub to the existing email-based record so future
                // logins will be found by the faster Phase 1 path.
                _logger.LogInformation(
                    "Linking SSO sub {NewUserId} to existing profile via email {Email} (tenant={TenantId})",
                    tenant.UserId, tenant.UserEmail, tenant.TenantId);
                profile.UserId = tenant.UserId;
            }
        }

        if (profile is null)
        {
            // First-ever login for this user in this tenant — create their profile.
            // Prefer the friendly name claim so the admin UI shows a human-readable label;
            // fall back to email, then the raw user id, and finally "Unknown".
            var displayName = !string.IsNullOrEmpty(tenant.UserName) ? tenant.UserName
                            : !string.IsNullOrEmpty(tenant.UserEmail) ? tenant.UserEmail
                            : !string.IsNullOrEmpty(tenant.UserId) ? tenant.UserId
                            : "Unknown";
            profile = new UserProfileEntity
            {
                TenantId = tenant.TenantId,
                UserId = tenant.UserId,
                Email = tenant.UserEmail,
                DisplayName = displayName,
                Roles = tenant.UserRoles,
                AgentAccess = tenant.AgentAccess,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            _logger.LogInformation("Created user profile for tenant={TenantId} user={UserId}", tenant.TenantId, tenant.UserId);
        }
        else
        {
            // Mirror latest claims from JWT on every login
            profile.Email = tenant.UserEmail;
            profile.Roles = tenant.UserRoles;
            profile.AgentAccess = tenant.AgentAccess;
            profile.LastLoginAt = DateTime.UtcNow;

            // Backfill the display name when a real name claim is now available and the
            // stored value is still a fallback (empty, or equal to the email/user id).
            // This never clobbers a name an admin edited manually.
            if (!string.IsNullOrEmpty(tenant.UserName) &&
                (string.IsNullOrEmpty(profile.DisplayName)
                 || profile.DisplayName == profile.UserId
                 || string.Equals(profile.DisplayName, tenant.UserEmail, StringComparison.OrdinalIgnoreCase)))
            {
                profile.DisplayName = tenant.UserName;
            }
        }

        await db.SaveChangesAsync(ct);

        // Refresh cached active status after each login upsert
        _cache.Set(ActiveCachePrefix + tenant.TenantId + ":" + tenant.UserId, profile.IsActive, ActiveCacheTtl);
    }

    // ── IsActive (cached) ─────────────────────────────────────────────────────

    public async Task<bool> IsActiveAsync(int tenantId, string userId, CancellationToken ct = default)
    {
        var key = ActiveCachePrefix + tenantId + ":" + userId;
        if (_cache.TryGetValue(key, out bool cached))
            return cached;

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var profile = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        // If no profile exists yet, treat as active (first login will create it)
        var isActive = profile?.IsActive ?? true;
        _cache.Set(key, isActive, ActiveCacheTtl);
        return isActive;
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    public async Task<List<UserProfileEntity>> GetForTenantAsync(
        int tenantId, string? search = null, string? role = null, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var query = db.UserProfiles.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(p =>
                p.DisplayName.Contains(search) || p.Email.Contains(search));

        return await query.OrderBy(p => p.DisplayName).ToListAsync(ct);
    }

    public async Task<UserProfileEntity?> GetByUserIdAsync(int tenantId, string userId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task<UserProfileEntity?> GetByIdAsync(int tenantId, int id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    // ── Admin mutations ───────────────────────────────────────────────────────

    public async Task UpdateAsync(int tenantId, int id, UpdateUserProfileDto dto, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"User profile {id} not found for tenant {tenantId}");

        profile.DisplayName = dto.DisplayName;
        profile.AvatarUrl = dto.AvatarUrl;
        profile.AgentAccessOverrides = dto.AgentAccessOverrides;
        profile.MetadataJson = dto.MetadataJson;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int tenantId, int id, bool isActive, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"User profile {id} not found for tenant {tenantId}");

        profile.IsActive = isActive;
        await db.SaveChangesAsync(ct);

        // Invalidate cached active status so next request picks up the change
        _cache.Remove(ActiveCachePrefix + tenantId + ":" + profile.UserId);
    }
}
