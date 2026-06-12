using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.AspNetCore.Http;
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
        IPlatformApiKeyService apiKeyService)
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
                InboundApiKey = apiKeyHeader,
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
