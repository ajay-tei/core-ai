using Diva.Infrastructure.Data.Entities;

namespace Diva.TenantAdmin.Services;

public interface ITenantSsoConfigService
{
    Task<List<TenantSsoConfigEntity>> GetForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns all active SSO configs across all tenants (cross-tenant query), paired with
    /// the tenant organization name. Used by the public /api/auth/providers endpoint to
    /// populate the login page. Only returns configs that have an AuthorizationEndpoint configured.
    /// </summary>
    Task<List<(TenantSsoConfigEntity Config, string TenantName)>> GetAllActiveAsync(CancellationToken ct = default);
    Task<TenantSsoConfigEntity?> GetByIdAsync(int tenantId, int id, CancellationToken ct = default);
    Task<TenantSsoConfigEntity> CreateAsync(int tenantId, CreateSsoConfigDto dto, CancellationToken ct = default);
    Task<TenantSsoConfigEntity> UpdateAsync(int tenantId, int id, UpdateSsoConfigDto dto, CancellationToken ct = default);
    Task DeleteAsync(int tenantId, int id, CancellationToken ct = default);

    /// <summary>
    /// Finds an active SSO config whose authorization URL / issuer / authority shares the
    /// same root domain as the given email domain. No extra configuration required —
    /// the SSO provider URL is the source of truth.
    /// e.g. email domain "totaleintegrated.com" matches SSO auth URL "sso.totaleintegrated.com".
    /// Returns null if no SSO config's URL root-domain matches.
    /// </summary>
    Task<TenantSsoConfigEntity?> FindBySsoDomainAsync(string domain, CancellationToken ct = default);

    /// <summary>
    /// Finds the tenant that has the given email registered as a local user or user profile.
    /// Fallback discovery path when no SSO domain mapping matches.
    /// Only works for pre-registered users (admin must create the user first).
    /// Returns null when the email is not registered in any tenant.
    /// </summary>
    Task<int?> FindTenantByRegisteredEmailAsync(string email, CancellationToken ct = default);
}

public record CreateSsoConfigDto(
    string ProviderName,
    string Issuer,
    string ClientId,
    string ClientSecret,
    string TokenType,
    string? Authority,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserinfoEndpoint,
    string? IntrospectionEndpoint,
    string Audience,
    string ProxyBaseUrl,
    string? ProxyAdminEmail,
    bool UseRoleMappings,
    bool UseTeamMappings,
    string? ClaimMappingsJson,
    string? LogoutUrl,
    string? EmailDomains,
    string? SsoForwardHeadersJson = null);

public record UpdateSsoConfigDto(
    string? ClientSecret,               // null = keep existing; supply to rotate
    string TokenType,
    string? Authority,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserinfoEndpoint,
    string? IntrospectionEndpoint,
    string Audience,
    string ProxyBaseUrl,
    string? ProxyAdminEmail,
    bool IsActive,
    bool UseRoleMappings,
    bool UseTeamMappings,
    string? ClaimMappingsJson,
    string? LogoutUrl,
    string? EmailDomains,
    string? SsoForwardHeadersJson = null);
