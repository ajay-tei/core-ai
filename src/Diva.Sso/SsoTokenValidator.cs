using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Http;

namespace Diva.Sso;

/// <summary>
/// Validates SSO tokens for both JWT (local JWKS verification) and opaque (introspection) flows.
///
/// JWT path:
///   - Validates JWT signature against JWKS from OIDC discovery or explicit endpoints
///   - Caches ConfigurationManager per authority to avoid repeated discovery fetches
///
/// Opaque path:
///   - POSTs token to the provider's introspection endpoint with client credentials
///   - Caches introspection responses for 60 seconds (keyed by token hash)
///   - Returns null if active=false in introspection response
/// </summary>
public sealed class SsoTokenValidator : ISsoTokenValidator
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<SsoTokenValidator> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // Per-authority OIDC config managers — safe to cache; they self-refresh JWKS
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        _configManagers = new(StringComparer.OrdinalIgnoreCase);

    private readonly JwtSecurityTokenHandler _jwtHandler = new() { MapInboundClaims = false };

    public SsoTokenValidator(
        IMemoryCache cache,
        ILogger<SsoTokenValidator> logger,
        IHttpClientFactory httpClientFactory)
    {
        _cache = cache;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public Task<ClaimsPrincipal?> ValidateAsync(
        string token,
        ISsoProviderConfig config,
        CancellationToken ct = default)
        => IsJwt(token)
            ? ValidateJwtAsync(token, config, ct)
            : ValidateOpaqueAsync(token, config, ct);

    // ── JWT path ─────────────────────────────────────────────────────────────

    private async Task<ClaimsPrincipal?> ValidateJwtAsync(
        string token, ISsoProviderConfig config, CancellationToken ct)
    {
        try
        {
            var signingKeys = await GetSigningKeysAsync(config, ct);
            if (signingKeys is null)
            {
                _logger.LogWarning("Could not retrieve signing keys for provider {Provider}", config.ProviderName);
                return null;
            }

            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = signingKeys,
                ValidateIssuer = !string.IsNullOrEmpty(config.Issuer),
                ValidIssuer = config.Issuer,
                ValidateAudience = !string.IsNullOrEmpty(config.Audience),
                ValidAudience = config.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = _jwtHandler.ValidateToken(token, parameters, out _);
            return principal;
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogWarning("JWT expired for provider {Provider}: {Message}", config.ProviderName, ex.Message);
            return null;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("JWT validation failed for provider {Provider}: {Message}", config.ProviderName, ex.Message);
            return null;
        }
    }

    private async Task<IEnumerable<SecurityKey>?> GetSigningKeysAsync(
        ISsoProviderConfig config, CancellationToken ct)
    {
        // Prefer explicit Authority for OIDC discovery; fall back to constructed metadata URL
        var authority = config.Authority;
        if (string.IsNullOrEmpty(authority))
            return null;

        var manager = _configManagers.GetOrAdd(authority, a =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{a.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever()));

        var oidcConfig = await manager.GetConfigurationAsync(ct);
        return oidcConfig.SigningKeys;
    }

    // ── Opaque token path ─────────────────────────────────────────────────────
    //
    // Two validation strategies, tried in order:
    //   1. RFC 7662 introspection  — used when IntrospectionEndpoint is set
    //   2. Userinfo-based          — used when UserinfoEndpoint is set but no IntrospectionEndpoint
    //      GET UserinfoEndpoint with "Authorization: Bearer {token}"
    //      200 = valid; claims extracted from JSON response body
    //      401/403 = invalid/expired → return null
    //
    // This covers non-OIDC OAuth2 providers (e.g. custom enterprise gateways) that don't
    // implement RFC 7662 but do expose a userinfo-style endpoint.

    private async Task<ClaimsPrincipal?> ValidateOpaqueAsync(
        string token, ISsoProviderConfig config, CancellationToken ct)
    {
        bool hasIntrospection = !string.IsNullOrEmpty(config.IntrospectionEndpoint);
        bool hasUserinfo = !string.IsNullOrEmpty(config.UserinfoEndpoint);

        if (!hasIntrospection && !hasUserinfo)
        {
            _logger.LogWarning(
                "Opaque token for provider {Provider}: neither IntrospectionEndpoint nor UserinfoEndpoint is configured",
                config.ProviderName);
            return null;
        }

        // Cache by hash of token to avoid remote call on every request
        var strategy = hasIntrospection ? "introspect" : "userinfo";
        var cacheKey = $"sso:{strategy}:{config.ProviderName}:{HashToken(token)}";
        if (_cache.TryGetValue(cacheKey, out ClaimsPrincipal? cached))
            return cached;

        ClaimsPrincipal? result = null;
        Exception? ex = null;
        try
        {
            result = hasIntrospection
                ? await IntrospectTokenAsync(token, config, ct)
                : await ValidateViaUserinfoAsync(token, config, ct);
        }
        catch (Exception e)
        {
            ex = e;
        }

        if (ex is not null)
        {
            _logger.LogWarning(ex, "Opaque token validation failed for provider {Provider} (strategy: {Strategy})",
                config.ProviderName, strategy);
            return null;
        }

        // Cache valid principals for 60s; never cache null
        if (result is not null)
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(60));

        return result;
    }

    // ── RFC 7662 introspection ────────────────────────────────────────────────

    private async Task<ClaimsPrincipal?> IntrospectTokenAsync(
        string token, ISsoProviderConfig config, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("sso-introspect");

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("token", token),
            new KeyValuePair<string, string>("token_type_hint", "access_token")
        });

        var response = await http.PostAsync(config.IntrospectionEndpoint, body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("active", out var activeProp) || !activeProp.GetBoolean())
        {
            _logger.LogDebug("Introspection returned active=false for provider {Provider}", config.ProviderName);
            return null;
        }

        return BuildPrincipalFromIntrospection(doc.RootElement, config);
    }

    // ── Userinfo-based validation (non-OIDC / custom OAuth2 providers) ────────

    private async Task<ClaimsPrincipal?> ValidateViaUserinfoAsync(
        string token, ISsoProviderConfig config, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("sso-introspect");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.GetAsync(config.UserinfoEndpoint, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogDebug("Userinfo returned {Status} for provider {Provider} — token invalid/expired",
                (int)response.StatusCode, config.ProviderName);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        return BuildPrincipalFromIntrospection(doc.RootElement, config);
    }

    private static ClaimsPrincipal BuildPrincipalFromIntrospection(
        JsonElement root, ISsoProviderConfig config)
    {
        var claims = new List<Claim>();

        // Standard introspection response fields (RFC 7662)
        AddIfPresent(claims, "sub", root);
        AddIfPresent(claims, "username", root);
        AddIfPresent(claims, "email", root);
        AddIfPresent(claims, "tenant_id", root);
        AddIfPresent(claims, "tenant_name", root);
        AddIfPresent(claims, "site_ids", root);
        AddIfPresent(claims, "roles", root);
        AddIfPresent(claims, "groups", root);
        AddIfPresent(claims, "agent_access", root);
        AddIfPresent(claims, "litellm_team_key", root);

        // Expiry from exp field
        if (root.TryGetProperty("exp", out var expProp))
            claims.Add(new Claim("exp", expProp.GetInt64().ToString()));

        var identity = new ClaimsIdentity(claims, "introspection");
        return new ClaimsPrincipal(identity);
    }

    private static void AddIfPresent(List<Claim> claims, string name, JsonElement root)
    {
        if (root.TryGetProperty(name, out var prop))
            claims.Add(new Claim(name, prop.GetRawText().Trim('"')));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects if a token is a JWT (three base64url segments separated by '.').
    /// Does NOT validate structure — just routes to the correct validation path.
    /// </summary>
    public static bool IsJwt(string token)
        => token.Split('.').Length == 3;

    /// <summary>
    /// Decode JWT payload without verification to extract the iss claim.
    /// Used by the config resolver to find the right tenant config before full validation.
    /// </summary>
    public static string? PeekIssuer(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            if (!handler.CanReadToken(token)) return null;
            var jwt = handler.ReadJwtToken(token);
            return jwt.Issuer;
        }
        catch
        {
            return null;
        }
    }

    private static string HashToken(string token)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes)[..16]; // first 16 chars of hex is enough for a cache key
    }
}
