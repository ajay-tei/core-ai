using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Manages platform API keys for non-SSO authentication.
/// Keys are stored as SHA-256 hashes — the raw key is returned exactly once on creation.
/// Key format: {slug}_{base64url(32 random bytes)}
/// </summary>
public sealed class PlatformApiKeyService : IPlatformApiKeyService
{
    private readonly IDatabaseProviderFactory _dbFactory;
    private readonly ILogger<PlatformApiKeyService> _logger;
    private readonly string _keyPrefix;

    public PlatformApiKeyService(
        IDatabaseProviderFactory dbFactory,
        ILogger<PlatformApiKeyService> logger,
        IOptions<AppBrandingOptions> branding)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _keyPrefix = $"{branding.Value.Slug}_";
    }

    public async Task<ApiKeyCreatedResult> CreateAsync(int tenantId, string userId, CreateApiKeyRequest request, CancellationToken ct)
    {
        ValidateScope(request.Scope);
        var rawKey = GenerateKey();
        var hash = HashKey(rawKey);

        var entity = new PlatformApiKeyEntity
        {
            TenantId = tenantId,
            Name = request.Name,
            KeyHash = hash,
            KeyPrefix = rawKey[..Math.Min(rawKey.Length, 12)], // "diva_" + first 7 chars of random part
            Scope = request.Scope,
            AllowedAgentIdsJson = request.AllowedAgentIds is { Length: > 0 }
                ? JsonSerializer.Serialize(request.AllowedAgentIds)
                : null,
            AllowedGroupIdsJson = request.AllowedGroupIds is { Length: > 0 }
                ? JsonSerializer.Serialize(request.AllowedGroupIds)
                : null,
            ExpiresAt = request.ExpiresAt,
            CreatedByUserId = userId
        };

        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
        db.PlatformApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Platform API key created: {Name} (scope={Scope}) for tenant {TenantId}",
            entity.Name, entity.Scope, tenantId);

        return new ApiKeyCreatedResult(entity.Id, entity.Name, entity.KeyPrefix, rawKey, entity.Scope, entity.ExpiresAt);
    }

    public async Task<ValidatedApiKey?> ValidateAsync(string rawKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawKey) || !rawKey.StartsWith(_keyPrefix))
            return null;

        var hash = HashKey(rawKey);

        // Use system context (tenantId=0) to search across all tenants
        using var db = _dbFactory.CreateDbContext();
        var entity = await db.PlatformApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

        if (entity is null) return null;

        if (!entity.IsActive)
        {
            _logger.LogWarning("Revoked API key used: {Prefix}", entity.KeyPrefix);
            return null;
        }

        if (entity.ExpiresAt.HasValue && entity.ExpiresAt.Value <= DateTime.UtcNow)
        {
            _logger.LogWarning("Expired API key used: {Prefix}", entity.KeyPrefix);
            return null;
        }

        // Fire-and-forget: update LastUsedAt
        _ = Task.Run(async () =>
        {
            try
            {
                using var db2 = _dbFactory.CreateDbContext();
                var tracked = await db2.PlatformApiKeys.FirstOrDefaultAsync(k => k.Id == entity.Id, CancellationToken.None);
                if (tracked is not null)
                {
                    tracked.LastUsedAt = DateTime.UtcNow;
                    await db2.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update LastUsedAt for API key {Prefix}", entity.KeyPrefix);
            }
        }, CancellationToken.None);

        string[]? allowedAgents = null;
        if (!string.IsNullOrEmpty(entity.AllowedAgentIdsJson))
        {
            try { allowedAgents = JsonSerializer.Deserialize<string[]>(entity.AllowedAgentIdsJson); }
            catch { /* ignore malformed JSON */ }
        }

        string[]? allowedGroups = null;
        if (!string.IsNullOrEmpty(entity.AllowedGroupIdsJson))
        {
            try { allowedGroups = JsonSerializer.Deserialize<string[]>(entity.AllowedGroupIdsJson); }
            catch { /* ignore malformed JSON */ }
        }

        return new ValidatedApiKey(entity.Id, entity.TenantId, entity.Name, entity.KeyPrefix, entity.Scope, allowedAgents, allowedGroups);
    }

    public async Task<List<PlatformApiKeyInfo>> ListAsync(int tenantId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
        var entities = await db.PlatformApiKeys
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        return entities.Select(e =>
        {
            string[]? allowedAgents = null;
            if (!string.IsNullOrEmpty(e.AllowedAgentIdsJson))
            {
                try { allowedAgents = JsonSerializer.Deserialize<string[]>(e.AllowedAgentIdsJson); }
                catch { /* ignore */ }
            }

            string[]? allowedGroups = null;
            if (!string.IsNullOrEmpty(e.AllowedGroupIdsJson))
            {
                try { allowedGroups = JsonSerializer.Deserialize<string[]>(e.AllowedGroupIdsJson); }
                catch { /* ignore */ }
            }

            return new PlatformApiKeyInfo(
                e.Id, e.Name, e.KeyPrefix, e.Scope, allowedAgents,
                e.CreatedAt, e.ExpiresAt, e.IsActive, e.LastUsedAt, e.CreatedByUserId, allowedGroups);
        }).ToList();
    }

    public async Task RevokeAsync(int tenantId, int keyId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.PlatformApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.TenantId == tenantId, ct);
        if (entity is null) return;

        entity.IsActive = false;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Platform API key revoked: {Name} ({Prefix}) for tenant {TenantId}",
            entity.Name, entity.KeyPrefix, tenantId);
    }

    public async Task<ApiKeyCreatedResult> RotateAsync(int tenantId, int keyId, string userId, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
        var old = await db.PlatformApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException($"API key {keyId} not found for tenant {tenantId}");

        // Revoke old
        old.IsActive = false;

        // Create new with same config
        var rawKey = GenerateKey();
        var newEntity = new PlatformApiKeyEntity
        {
            TenantId = tenantId,
            Name = old.Name,
            KeyHash = HashKey(rawKey),
            KeyPrefix = rawKey[..Math.Min(rawKey.Length, 12)],
            Scope = old.Scope,
            AllowedAgentIdsJson = old.AllowedAgentIdsJson,
            ExpiresAt = old.ExpiresAt,
            CreatedByUserId = userId
        };
        db.PlatformApiKeys.Add(newEntity);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Platform API key rotated: {Name} ({OldPrefix} → {NewPrefix}) for tenant {TenantId}",
            newEntity.Name, old.KeyPrefix, newEntity.KeyPrefix, tenantId);

        return new ApiKeyCreatedResult(newEntity.Id, newEntity.Name, newEntity.KeyPrefix, rawKey, newEntity.Scope, newEntity.ExpiresAt);
    }

    // ── Helpers ────────────────────────────────────────────────

    private string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return _keyPrefix + Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashKey(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexStringLower(bytes);
    }

    private static void ValidateScope(string scope)
    {
        if (scope is not ("admin" or "invoke" or "readonly"))
            throw new ArgumentException($"Invalid scope: '{scope}'. Must be 'admin', 'invoke', or 'readonly'.");
    }
}
