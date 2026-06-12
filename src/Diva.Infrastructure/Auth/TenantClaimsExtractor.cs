using System.Security.Claims;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Auth;

public interface ITenantClaimsExtractor
{
    TenantContext Extract(ClaimsPrincipal principal, string? accessToken, string? requestSiteId);
}

public sealed class TenantClaimsExtractor : ITenantClaimsExtractor
{
    private readonly OAuthOptions _options;
    private readonly ILogger<TenantClaimsExtractor> _logger;

    public TenantClaimsExtractor(IOptions<OAuthOptions> options, ILogger<TenantClaimsExtractor> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public TenantContext Extract(ClaimsPrincipal principal, string? accessToken, string? requestSiteId)
    {
        var mappings = _options.ClaimMappings;

        var tenantId = ParseInt(principal.FindFirstValue(mappings.TenantId));
        var siteIds = ParseIntArray(principal.FindFirstValue(mappings.SiteIds));
        var roles = ParseStringArray(principal.FindFirstValue(mappings.Roles));
        var groups = ParseStringArray(principal.FindFirstValue(mappings.Groups));

        // Determine current site: request header → first allowed site → 0
        var currentSiteId = ParseInt(requestSiteId);
        if (currentSiteId == 0 && siteIds.Length > 0)
            currentSiteId = siteIds[0];

        // AccessToken is the SSO provider token only — never the Diva-internal JWT.
        // When the user authenticated via SSO, AuthController embeds the original
        // access_token as an "sso_token" claim. That token is safe to forward to MCP
        // servers in the same SSO ecosystem (PassSsoToken=true).
        // Local-auth users have no sso_token claim → AccessToken is null, so the Diva
        // JWT is never forwarded to external MCP servers (it is meaningless to them).
        var ssoToken = principal.FindFirstValue("sso_token");
        var propagToken = !string.IsNullOrEmpty(ssoToken) ? ssoToken : null;

        _logger.LogDebug(
            "TenantClaimsExtractor: tenant={TenantId} user={UserId} | sso_token claim={HasSsoToken} | AccessToken source={TokenSource}",
            tenantId,
            principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?",
            !string.IsNullOrEmpty(ssoToken),
            !string.IsNullOrEmpty(ssoToken) ? "sso_token claim" : "none (local-auth, not forwarded)");

        return new TenantContext
        {
            TenantId = tenantId,
            TenantName = principal.FindFirstValue(mappings.TenantName) ?? string.Empty,
            UserId = principal.FindFirstValue(mappings.UserId) ?? string.Empty,
            UserEmail = principal.FindFirstValue(mappings.Email)
                            ?? principal.FindFirstValue(ClaimTypes.Email)
                            ?? principal.FindFirstValue("email") ?? string.Empty,
            UserName = principal.FindFirstValue(mappings.DisplayName)
                            ?? principal.FindFirstValue(ClaimTypes.Name)
                            ?? principal.FindFirstValue("name") ?? string.Empty,
            Role = roles.FirstOrDefault() ?? string.Empty,
            UserRoles = roles,
            UserGroups = groups,
            AgentAccess = ParseStringArray(principal.FindFirstValue(mappings.AgentAccess)),
            GroupAccess = ParseStringArray(principal.FindFirstValue(mappings.GroupAccess)),
            SiteIds = siteIds,
            CurrentSiteId = currentSiteId,
            AccessToken = propagToken,
            TokenExpiry = ParseExpiry(principal),
            TeamApiKey = principal.FindFirstValue(mappings.TeamApiKey),
            CorrelationId = Guid.NewGuid().ToString()
        };
    }

    private static int ParseInt(string? value)
        => int.TryParse(value, out var i) ? i : 0;

    private static int[] ParseIntArray(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        // Handle JSON array "[1,2,3]" or comma-separated "1,2,3"
        if (value.TrimStart().StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<int[]>(value) ?? []; }
            catch { return []; }
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var i) ? i : 0)
                    .Where(i => i > 0)
                    .ToArray();
    }

    private static string[] ParseStringArray(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        if (value.TrimStart().StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
            catch { return []; }
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
    }

    private static DateTimeOffset ParseExpiry(ClaimsPrincipal principal)
    {
        var exp = principal.FindFirstValue("exp");
        if (long.TryParse(exp, out var unixSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return DateTimeOffset.UtcNow.AddHours(1);
    }
}
