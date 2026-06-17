using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Sso;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// Manages per-tenant SSO configurations.
/// Also implements ISsoConfigResolver (from Diva.Sso) so validators can look up configs.
/// Singleton-safe: uses IDatabaseProviderFactory per call.
/// </summary>
public sealed class TenantSsoConfigService : ITenantSsoConfigService, ISsoConfigResolver
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantSsoConfigService> _logger;

    private const string IssuerCachePrefix = "sso:issuer:";
    private const string TenantCachePrefix = "sso:tenant:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public TenantSsoConfigService(
        IDatabaseProviderFactory db,
        IMemoryCache cache,
        ILogger<TenantSsoConfigService> logger)
    {
        _db     = db;
        _cache  = cache;
        _logger = logger;
    }

    // ── ISsoConfigResolver ────────────────────────────────────────────────────

    public async Task<ISsoProviderConfig?> FindByIssuerAsync(string issuer, CancellationToken ct = default)
    {
        var key = IssuerCachePrefix + issuer;
        if (_cache.TryGetValue(key, out TenantSsoConfigEntity? cached))
            return cached;

        // Pass null to bypass query filter — tenantId 0 means cross-tenant
        using var db = _db.CreateDbContext();
        var config = await db.SsoConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Issuer == issuer && c.IsActive, ct);

        _cache.Set(key, config, CacheTtl);
        return config;
    }

    public async Task<ISsoProviderConfig?> FindByTenantIdAsync(int tenantId, CancellationToken ct = default)
    {
        var key = TenantCachePrefix + tenantId;
        if (_cache.TryGetValue(key, out TenantSsoConfigEntity? cached))
            return cached;

        using var db = _db.CreateDbContext();  // null → tenantId 0 → bypass filter
        var config = await db.SsoConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IsActive && c.TokenType == "opaque", ct);

        _cache.Set(key, config, CacheTtl);
        return config;
    }

    // ── ITenantSsoConfigService (CRUD) ────────────────────────────────────────

    public async Task<List<TenantSsoConfigEntity>> GetForTenantAsync(int tenantId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.SsoConfigs
            .AsNoTracking()
            .OrderBy(c => c.ProviderName)
            .ToListAsync(ct);
    }

    public async Task<TenantSsoConfigEntity?> GetByIdAsync(int tenantId, int id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.SsoConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<TenantSsoConfigEntity> CreateAsync(
        int tenantId, CreateSsoConfigDto dto, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = new TenantSsoConfigEntity
        {
            TenantId               = tenantId,
            ProviderName           = dto.ProviderName,
            Issuer                 = dto.Issuer,
            ClientId               = dto.ClientId,
            ClientSecret           = dto.ClientSecret,
            TokenType              = dto.TokenType,
            Authority              = dto.Authority,
            AuthorizationEndpoint  = dto.AuthorizationEndpoint,
            TokenEndpoint          = dto.TokenEndpoint,
            UserinfoEndpoint       = dto.UserinfoEndpoint,
            IntrospectionEndpoint  = dto.IntrospectionEndpoint,
            Audience               = dto.Audience,
            ProxyBaseUrl           = dto.ProxyBaseUrl,
            ProxyAdminEmail        = dto.ProxyAdminEmail,
            UseRoleMappings        = dto.UseRoleMappings,
            UseTeamMappings        = dto.UseTeamMappings,
            ClaimMappingsJson      = dto.ClaimMappingsJson,
            LogoutUrl              = dto.LogoutUrl,
            EmailDomains           = dto.EmailDomains,
            SsoForwardHeadersJson  = dto.SsoForwardHeadersJson,
            IsActive               = true,
            CreatedAt              = DateTime.UtcNow
        };
        db.SsoConfigs.Add(entity);
        await db.SaveChangesAsync(ct);
        InvalidateCache(entity.Issuer, tenantId);
        return entity;
    }

    public async Task<TenantSsoConfigEntity> UpdateAsync(
        int tenantId, int id, UpdateSsoConfigDto dto, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.SsoConfigs.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"SSO config {id} not found for tenant {tenantId}");

        if (dto.ClientSecret is not null)
            entity.ClientSecret = dto.ClientSecret;
        entity.TokenType              = dto.TokenType;
        entity.Authority              = dto.Authority;
        entity.AuthorizationEndpoint  = dto.AuthorizationEndpoint;
        entity.TokenEndpoint          = dto.TokenEndpoint;
        entity.UserinfoEndpoint       = dto.UserinfoEndpoint;
        entity.IntrospectionEndpoint  = dto.IntrospectionEndpoint;
        entity.Audience               = dto.Audience;
        entity.ProxyBaseUrl           = dto.ProxyBaseUrl;
        entity.ProxyAdminEmail        = dto.ProxyAdminEmail;
        entity.IsActive               = dto.IsActive;
        entity.UseRoleMappings        = dto.UseRoleMappings;
        entity.UseTeamMappings        = dto.UseTeamMappings;
        entity.ClaimMappingsJson      = dto.ClaimMappingsJson;
        entity.LogoutUrl              = dto.LogoutUrl;
        entity.EmailDomains           = dto.EmailDomains;
        entity.SsoForwardHeadersJson  = dto.SsoForwardHeadersJson;
        entity.UpdatedAt              = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        InvalidateCache(entity.Issuer, tenantId);
        return entity;
    }

    public async Task DeleteAsync(int tenantId, int id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var entity = await db.SsoConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return;
        db.SsoConfigs.Remove(entity);
        await db.SaveChangesAsync(ct);
        InvalidateCache(entity.Issuer, tenantId);
    }

    public async Task<List<(TenantSsoConfigEntity Config, string TenantName)>> GetAllActiveAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(); // bypass tenant filter — cross-tenant
        return await db.SsoConfigs
            .AsNoTracking()
            .Where(c => c.IsActive && c.AuthorizationEndpoint != null)
            .Join(db.Tenants,
                  sso    => sso.TenantId,
                  tenant => tenant.Id,
                  (sso, tenant) => new { sso, tenant.Name })
            .OrderBy(x => x.Name)
            .Select(x => new { x.sso, x.Name })
            .ToListAsync(ct)
            .ContinueWith(t => t.Result.Select(x => (x.sso, x.Name)).ToList(), ct);
    }

    public async Task<TenantSsoConfigEntity?> FindBySsoDomainAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        // Normalise the email domain to its root domain (e.g. "mail.company.com" → "company.com")
        var emailRoot = GetRootDomain(domain) ?? domain.ToLower();

        using var db = _db.CreateDbContext(); // bypass tenant filter — cross-tenant lookup
        var allActive = await db.SsoConfigs
            .AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(ct);

        // Derive the SSO domain from the provider URL — no extra config required.
        // Priority: AuthorizationEndpoint → Issuer → Authority
        // e.g. "https://sso.totaleintegrated.com/oauth/authorize" → "totaleintegrated.com"
        return allActive.FirstOrDefault(c =>
        {
            var ssoDomain = GetRootDomain(c.AuthorizationEndpoint)
                         ?? GetRootDomain(c.Issuer)
                         ?? GetRootDomain(c.Authority);
            return ssoDomain is not null &&
                   string.Equals(ssoDomain, emailRoot, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Extracts the root domain (eTLD+1) from a URL or hostname.
    /// "https://sso.company.com/path" → "company.com"
    /// "company.co.uk" → "co.uk" (2-part heuristic; adequate for enterprise IdPs)
    /// </summary>
    private static string? GetRootDomain(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost)) return null;
        var toParse = urlOrHost.Contains("://") ? urlOrHost : "https://" + urlOrHost;
        if (!Uri.TryCreate(toParse, UriKind.Absolute, out var uri)) return null;
        var parts = uri.Host.Split('.');
        return parts.Length >= 2
            ? string.Join('.', parts[^2..]).ToLower()
            : uri.Host.ToLower();
    }

    public async Task<int?> FindTenantByRegisteredEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        using var db = _db.CreateDbContext(); // bypass tenant filter — cross-tenant lookup

        // Check LocalUsers first (explicit registration by admin)
        var localUser = await db.LocalUsers
            .AsNoTracking()
            .Where(u => u.Email.ToLower() == email.ToLower() && u.IsActive)
            .Select(u => (int?)u.TenantId)
            .FirstOrDefaultAsync(ct);
        if (localUser is not null) return localUser;

        // Fall back to UserProfiles (created on SSO login)
        var profile = await db.UserProfiles
            .AsNoTracking()
            .Where(p => p.Email.ToLower() == email.ToLower() && p.IsActive)
            .Select(p => (int?)p.TenantId)
            .FirstOrDefaultAsync(ct);
        return profile;
    }

    private void InvalidateCache(string issuer, int tenantId)
    {
        _cache.Remove(IssuerCachePrefix + issuer);
        _cache.Remove(TenantCachePrefix + tenantId);
    }
}
