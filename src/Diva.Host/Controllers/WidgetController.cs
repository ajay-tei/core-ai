using Diva.Core.Models.Widgets;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Sso;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diva.Host.Controllers;

[ApiController]
[AllowAnonymous]
[EnableCors("Widget")]
public class WidgetController : ControllerBase
{
    private readonly IWidgetConfigService _widgets;
    private readonly ISsoTokenValidator _ssoValidator;
    private readonly ITenantClaimsExtractor _claimsExtractor;
    private readonly ILocalAuthService _localAuth;
    private readonly IDatabaseProviderFactory _db;
    private readonly IWebHostEnvironment _env;

    public WidgetController(
        IWidgetConfigService widgets,
        ISsoTokenValidator ssoValidator,
        ITenantClaimsExtractor claimsExtractor,
        ILocalAuthService localAuth,
        IDatabaseProviderFactory db,
        IWebHostEnvironment env)
    {
        _widgets        = widgets;
        _ssoValidator   = ssoValidator;
        _claimsExtractor = claimsExtractor;
        _localAuth      = localAuth;
        _db             = db;
        _env            = env;
    }

    // ── Widget SPA shell ──────────────────────────────────────────────────────

    [HttpGet("/widget-ui")]
    public IActionResult WidgetUi()
    {
        var path = Path.Combine(_env.WebRootPath ?? "wwwroot", "widget", "index.html");
        if (!System.IO.File.Exists(path))
            return NotFound("Widget UI not built yet. Run 'npm run build' in admin-portal.");
        return PhysicalFile(path, "text/html");
    }

    // ── Public widget API ─────────────────────────────────────────────────────

    // GET /api/widget/{widgetId}/init
    [HttpGet("api/widget/{widgetId}/init")]
    public async Task<IActionResult> Init(string widgetId, CancellationToken ct)
    {
        var widget = await _widgets.GetByIdAsync(widgetId, ct);
        if (widget is null || !widget.IsActive || IsExpired(widget.ExpiresAt))
            return NotFound();

        // Look up agent display name
        using var db = _db.CreateDbContext();
        var agentName = await db.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.Id == widget.AgentId)
            .Select(a => a.DisplayName)
            .FirstOrDefaultAsync(ct) ?? widget.AgentId;

        var theme = DeserializeTheme(widget.ThemeJson);

        return Ok(new WidgetInitResponse(
            widget.Id,
            widget.AgentId,
            agentName,
            HasSso: widget.SsoConfigId.HasValue,
            widget.AllowAnonymous,
            widget.WelcomeMessage,
            widget.PlaceholderText,
            theme,
            widget.RespectSystemTheme,
            widget.ShowBranding));
    }

    // POST /api/widget/{widgetId}/auth  — SSO token exchange
    [HttpPost("api/widget/{widgetId}/auth")]
    public async Task<IActionResult> Auth(string widgetId, [FromBody] WidgetAuthRequest request, CancellationToken ct)
    {
        var widget = await _widgets.GetByIdAsync(widgetId, ct);
        if (widget is null || !widget.IsActive || IsExpired(widget.ExpiresAt))
            return NotFound();

        if (!IsOriginAllowed(widget.AllowedOriginsJson))
            return Forbid();

        if (!widget.SsoConfigId.HasValue)
            return BadRequest("This widget does not have an SSO provider configured.");

        // Load SSO config (bypass tenant filter)
        using var db = _db.CreateDbContext();
        var ssoConfig = await db.SsoConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == widget.SsoConfigId.Value && s.IsActive, ct);

        if (ssoConfig is null)
            return BadRequest("SSO configuration not found or inactive.");

        // Validate the SSO token
        var principal = await _ssoValidator.ValidateAsync(request.SsoToken, ssoConfig, ct);
        if (principal is null)
            return Unauthorized("SSO token is invalid or expired.");

        // Extract identity
        var tenantCtx = _claimsExtractor.Extract(principal, request.SsoToken, requestSiteId: null);

        // Issue a local Diva JWT (8 h, or widget expiry if sooner)
        var ssoFwdHeaders = ParseSsoForwardHeaders(ssoConfig.SsoForwardHeadersJson);
        var token = _localAuth.IssueSsoJwt(
            widget.TenantId,
            tenantCtx.UserId ?? "unknown",
            tenantCtx.UserEmail ?? string.Empty,
            tenantCtx.UserName ?? "User",
            tenantCtx.UserRoles,
            ssoAccessToken: request.SsoToken,
            ssoForwardHeaders: ssoFwdHeaders);

        var expiresAt = DateTime.UtcNow.AddHours(8);
        return Ok(new WidgetAuthResponse(token, tenantCtx.UserId ?? "unknown", expiresAt));
    }

    // POST /api/widget/{widgetId}/session  — anonymous session
    [HttpPost("api/widget/{widgetId}/session")]
    public async Task<IActionResult> Session(string widgetId, CancellationToken ct)
    {
        var widget = await _widgets.GetByIdAsync(widgetId, ct);
        if (widget is null || !widget.IsActive || IsExpired(widget.ExpiresAt))
            return NotFound();

        if (!IsOriginAllowed(widget.AllowedOriginsJson))
            return Forbid();

        if (!widget.AllowAnonymous)
            return Forbid();

        var sessionId = Guid.NewGuid().ToString("N");
        var userId    = $"anon:{sessionId}";
        var token = _localAuth.IssueWidgetAnonymousJwt(
            widget.TenantId, userId, widget.AgentId, TimeSpan.FromHours(1));

        var expiresAt = DateTime.UtcNow.AddHours(1);
        return Ok(new WidgetSessionResponse(token, sessionId, expiresAt));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool IsOriginAllowed(string? allowedOriginsJson)
    {
        if (string.IsNullOrWhiteSpace(allowedOriginsJson)) return false;
        var origin = Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrEmpty(origin)) return true; // server-to-server (no Origin header)
        string[] allowed;
        try { allowed = System.Text.Json.JsonSerializer.Deserialize<string[]>(allowedOriginsJson) ?? []; }
        catch { return false; }
        return allowed.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExpired(DateTime? expiresAt) =>
        expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow;

    private static WidgetTheme DeserializeTheme(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return WidgetTheme.Light;
        try { return System.Text.Json.JsonSerializer.Deserialize<WidgetTheme>(json) ?? WidgetTheme.Light; }
        catch { return WidgetTheme.Light; }
    }

    private static Dictionary<string, string>? ParseSsoForwardHeaders(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return null; }
    }
}
