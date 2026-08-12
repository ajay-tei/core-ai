using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Resolves tenant-scoped credential names into decrypted API keys.
/// Uses a 2-minute in-memory cache to avoid DB roundtrips on every tool call.
/// </summary>
public sealed class CredentialResolver : ICredentialResolver
{
    private readonly IDatabaseProviderFactory _dbFactory;
    private readonly ICredentialEncryptor _encryptor;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CredentialResolver> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public CredentialResolver(
        IDatabaseProviderFactory dbFactory,
        ICredentialEncryptor encryptor,
        IMemoryCache cache,
        ILogger<CredentialResolver> logger)
    {
        _dbFactory = dbFactory;
        _encryptor = encryptor;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ResolvedCredential?> ResolveAsync(int tenantId, string credentialName, int environmentId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentialName)) return null;

        var cacheKey = $"cred:{tenantId}:{credentialName}:{environmentId}";

        if (_cache.TryGetValue(cacheKey, out ResolvedCredential? cached))
            return cached;

        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));

        // Prefer a row tagged to the caller's own environment; fall back to an untagged row
        // (pre-Phase-I data, or simply not tagged yet) — never a DIFFERENT tagged environment's
        // row, so a Staging value can never leak into a Production resolution.
        McpCredentialEntity? entity = null;
        if (environmentId > 0)
        {
            entity = await db.McpCredentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == credentialName && c.EnvironmentId == environmentId, ct);
        }

        entity ??= await db.McpCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == credentialName && c.EnvironmentId == null, ct);

        if (entity is null)
        {
            _logger.LogWarning("Credential '{Name}' not found for tenant {TenantId} (environment {EnvironmentId})", credentialName, tenantId, environmentId);
            return null;
        }

        if (!entity.IsActive)
        {
            _logger.LogWarning("Credential '{Name}' is inactive for tenant {TenantId}", credentialName, tenantId);
            return null;
        }

        if (entity.ExpiresAt.HasValue && entity.ExpiresAt.Value <= DateTime.UtcNow)
        {
            _logger.LogWarning("Credential '{Name}' has expired for tenant {TenantId}", credentialName, tenantId);
            return null;
        }

        string apiKey;
        try
        {
            apiKey = _encryptor.Decrypt(entity.EncryptedApiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt credential '{Name}' for tenant {TenantId}", credentialName, tenantId);
            return null;
        }

        // Masked tail lets an admin cross-check this exact resolution against the credential's
        // current DB value (shown the same way in the admin UI) without ever logging the full key.
        var tail = apiKey.Length <= 4 ? apiKey : apiKey[^4..];
        _logger.LogInformation(
            "Credential '{Name}' resolved for tenant {TenantId} (environment {EnvironmentId}, scheme={Scheme}): key ****{Tail}",
            credentialName, tenantId, environmentId, entity.AuthScheme, tail);

        var resolved = new ResolvedCredential(apiKey, entity.AuthScheme, entity.CustomHeaderName);

        _cache.Set(cacheKey, resolved, CacheTtl);

        // Fire-and-forget: update LastUsedAt
        _ = Task.Run(async () =>
        {
            try
            {
                using var db2 = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
                var tracked = await db2.McpCredentials
                    .FirstOrDefaultAsync(c => c.Id == entity.Id, CancellationToken.None);
                if (tracked is not null)
                {
                    tracked.LastUsedAt = DateTime.UtcNow;
                    await db2.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update LastUsedAt for credential '{Name}'", credentialName);
            }
        }, CancellationToken.None);

        return resolved;
    }

    public async Task InvalidateAsync(int tenantId, string credentialName, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentialName)) return;

        // IMemoryCache has no prefix-evict, and the cache key includes the CALLER's environmentId
        // (0 = unscoped), not just the credential's own tag — so sweep every environment this
        // tenant has, not just the one the credential currently happens to be tagged to.
        _cache.Remove($"cred:{tenantId}:{credentialName}:0");

        using var db = _dbFactory.CreateDbContext(TenantContext.System(tenantId));
        var envIds = await db.TenantEnvironments
            .Where(e => e.TenantId == tenantId)
            .Select(e => e.Id)
            .ToListAsync(ct);
        foreach (var envId in envIds)
            _cache.Remove($"cred:{tenantId}:{credentialName}:{envId}");

        _logger.LogInformation("CredentialResolver: invalidated cache for '{Name}' (tenant {TenantId})", credentialName, tenantId);
    }
}
