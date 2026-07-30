using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Validates the Bearer token, builds TenantContext, and stores it in HttpContext.Items["TenantContext"].
/// Bypasses auth for health check and swagger endpoints.
/// When OAuthOptions.Enabled = false (dev mode), injects a system TenantContext without validation.
/// </summary>
public sealed class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;
    private readonly OAuthOptions _options;
    private readonly AppBrandingOptions _branding;

    private static readonly HashSet<string> BypassPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health/live", "/health/ready", "/metrics", "/favicon.ico",
        "/api/auth/login", "/api/auth/callback", "/api/auth/providers",
        "/api/auth/local", "/api/auth/discover",
        "/api/auth/setup", "/api/auth/admin",
        "/api/auth/logout", "/api/auth/logout-callback",
        "/api/debug/vision-probe",
        "/api/debug/vision-probe/summarize",
        // Scheduler feedback public endpoints — protected by HMAC token, not Bearer auth
        "/api/scheduler-feedback/context",
        "/api/scheduler-feedback/submit",
    };

    public TenantContextMiddleware(
        RequestDelegate next,
        ILogger<TenantContextMiddleware> logger,
        IOptions<OAuthOptions> options,
        IOptions<AppBrandingOptions> branding)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
        _branding = branding.Value;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IOAuthTokenValidator validator,
        ITenantClaimsExtractor extractor,
        IUserLoginTracker loginTracker,
        IPlatformApiKeyService apiKeyService,
        IDatabaseProviderFactory dbFactory)
    {
        // Always bypass for health checks, swagger, and auth callbacks
        if (BypassPaths.Contains(context.Request.Path.Value ?? string.Empty) ||
            context.Request.Path.StartsWithSegments("/swagger") ||
            context.Request.Path.StartsWithSegments("/hubs") ||
            context.Request.Path.StartsWithSegments("/.well-known"))
        {
            await _next(context);
            return;
        }

        // Dev bypass: when auth is disabled, inject a master admin context so all
        // platform-level endpoints (groups, tenants, LLM config, etc.) are accessible.
        if (!_options.Enabled)
        {
            context.Items["TenantContext"] = TenantContext.DevMasterAdmin();
            await _next(context);
            return;
        }

        // ── API Key authentication (X-API-Key header) ─────────────────────────
        // Checked before Bearer/JWT so external systems and scheduled tasks can
        // authenticate without SSO. Skips JWT validation entirely.
        var apiKeyHeader = context.Request.Headers["X-API-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKeyHeader))
        {
            var validatedKey = await apiKeyService.ValidateAsync(apiKeyHeader, context.RequestAborted);
            if (validatedKey is null)
            {
                _logger.LogWarning("Invalid or expired API key used on {Path}", context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized: invalid API key");
                return;
            }

            // Map scope to role
            var role = validatedKey.Scope switch
            {
                "admin" => "admin",
                "readonly" => "reader",
                _ => "user",
            };

            // Extract X-Tenant-* custom headers from the inbound request so they propagate
            // to MCP tool servers (via McpRequestContext.ToHeaders) when PassTenantHeaders=true.
            // X-Tenant-ID is excluded — it is carried as TenantContext.TenantId, not CustomHeaders.
            var apiKeyCustomHeaders = context.Request.Headers
                .Where(h => h.Key.StartsWith("X-Tenant-", StringComparison.OrdinalIgnoreCase)
                         && !h.Key.Equals("X-Tenant-ID", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key["X-Tenant-".Length..], h => h.Value.ToString());

            // Environment resolution (Phase E): the key's own tagged environment wins; an untagged
            // key falls back to the tenant's IsDefault environment so legacy/untagged keys keep
            // resolving correctly.
            var apiKeyEnvironmentId = validatedKey.EnvironmentId
                ?? await ResolveDefaultEnvironmentIdAsync(dbFactory, validatedKey.TenantId, context.RequestAborted);

            var apiKeyTenant = new TenantContext
            {
                TenantId = validatedKey.TenantId,
                TenantName = $"ApiKey:{validatedKey.Name}",
                UserId = $"apikey:{validatedKey.KeyPrefix}",
                Role = role,
                UserRoles = [role],
                AgentAccess = validatedKey.AllowedAgentIds ?? ["*"],
                GroupAccess = validatedKey.AllowedGroupIds ?? [],
                SiteIds = [],
                CurrentSiteId = int.TryParse(context.Request.Headers["X-Site-ID"].FirstOrDefault(), out var sid) ? sid : 0,
                EnvironmentId = apiKeyEnvironmentId,
                InboundApiKey = apiKeyHeader,
                PlatformApiKeyId = validatedKey.Id,
                CustomHeaders = apiKeyCustomHeaders,
            };

            context.Items["TenantContext"] = apiKeyTenant;

            using var apiKeyScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["TenantId"] = apiKeyTenant.TenantId,
                ["UserId"] = apiKeyTenant.UserId,
                ["SiteId"] = apiKeyTenant.CurrentSiteId,
                ["AuthMethod"] = "ApiKey",
                ["CorrelationId"] = apiKeyTenant.CorrelationId
            });

            _logger.LogDebug("API key auth: tenant {TenantId}, scope {KeyScope}, key {Prefix}",
                apiKeyTenant.TenantId, validatedKey.Scope, validatedKey.KeyPrefix);

            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Request missing Bearer token: {Path}", context.Request.Path);

            // Browser requests (Accept: text/html) get a redirect to the SSO login endpoint.
            // API clients (Postman, mobile, etc.) get a plain 401.
            var accept = context.Request.Headers.Accept.ToString();
            if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault() ?? "1";
                context.Response.Redirect($"/api/auth/login?tenantId={tenantId}");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = $"Bearer realm=\"{_branding.ApiAudience}\"";
            await context.Response.WriteAsync("Unauthorized: missing token");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var principal = await validator.ValidateTokenAsync(token, context.RequestAborted);

        if (principal is null)
        {
            _logger.LogWarning("Invalid or expired token for {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized: invalid token");
            return;
        }

        var requestSiteId = context.Request.Headers["X-Site-ID"].FirstOrDefault();
        var tenantContext = extractor.Extract(principal, token, requestSiteId);

        // Environment resolution (Phase E): an explicit X-Environment header is honored ONLY for
        // admins (staging/preview access in the admin portal) — rejected for non-admin roles to
        // avoid a regular user spoofing their way into a different environment's data. Falls back
        // to the tenant's IsDefault environment for everyone else (the sole fallback for all
        // untagged/legacy traffic).
        var requestedEnvironmentHeader = context.Request.Headers["X-Environment"].FirstOrDefault();
        int resolvedEnvironmentId;
        if (tenantContext.IsAdmin && int.TryParse(requestedEnvironmentHeader, out var explicitEnvId) && explicitEnvId > 0)
        {
            resolvedEnvironmentId = explicitEnvId;
        }
        else
        {
            resolvedEnvironmentId = await ResolveDefaultEnvironmentIdAsync(dbFactory, tenantContext.TenantId, context.RequestAborted);
        }

        tenantContext = tenantContext.WithEnvironment(resolvedEnvironmentId);

        // Check if the user's account is active (admin may have disabled it)
        if (!await loginTracker.IsActiveAsync(tenantContext.TenantId, tenantContext.UserId, context.RequestAborted))
        {
            _logger.LogWarning("Disabled account attempted access: tenant={TenantId} user={UserId}",
                tenantContext.TenantId, tenantContext.UserId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Account disabled");
            return;
        }

        context.Items["TenantContext"] = tenantContext;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = tenantContext.TenantId,
            ["UserId"] = tenantContext.UserId,
            ["SiteId"] = tenantContext.CurrentSiteId,
            ["CorrelationId"] = tenantContext.CorrelationId
        });

        _logger.LogDebug("TenantContext built for tenant {TenantId}, site {SiteId}",
            tenantContext.TenantId, tenantContext.CurrentSiteId);

        // Upsert user profile asynchronously (non-fatal)
        try { await loginTracker.UpsertOnLoginAsync(tenantContext, context.RequestAborted); }
        catch (Exception ex) { _logger.LogError(ex, "User profile upsert failed for tenant={TenantId} user={UserId}", tenantContext.TenantId, tenantContext.UserId); }

        await _next(context);
    }

    /// <summary>
    /// Resolves the tenant's IsDefault TenantEnvironmentEntity.Id, or 0 (wildcard/no-filter) if the
    /// tenant has none configured yet — e.g. TenantId=0 master-admin/system contexts, or a brand-new
    /// tenant before Program.cs's environment backfill has run. Deliberately a direct EF query (not
    /// IEnvironmentService) since Diva.Infrastructure cannot reference Diva.TenantAdmin without
    /// creating a circular project reference.
    /// </summary>
    private static async Task<int> ResolveDefaultEnvironmentIdAsync(IDatabaseProviderFactory dbFactory, int tenantId, CancellationToken ct)
    {
        using var db = dbFactory.CreateDbContext();
        var defaultEnv = await db.TenantEnvironments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IsDefault, ct);
        return defaultEnv?.Id ?? 0;
    }
}

/// <summary>Extension method for accessing TenantContext from HttpContext in controllers.</summary>
public static class HttpContextExtensions
{
    public static TenantContext GetTenantContext(this HttpContext context)
    {
        if (context.Items.TryGetValue("TenantContext", out var obj) && obj is TenantContext tc)
            return tc;
        throw new InvalidOperationException(
            "TenantContext not found. Ensure TenantContextMiddleware is registered before controllers.");
    }

    public static TenantContext? TryGetTenantContext(this HttpContext context)
        => context.Items.TryGetValue("TenantContext", out var obj) ? obj as TenantContext : null;
}
