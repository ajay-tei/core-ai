using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Host.Auth;
using Diva.Infrastructure.Auth;
using Diva.TenantAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace Diva.Host.Controllers;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record LocalLoginRequest(int TenantId, string Username, string Password);
public record AdminLoginRequest(string Username, string Password);
public record SetupMasterAdminRequest(string Username, string Email, string Password = "changemeonlogin", string DisplayName = "Platform Admin");
public record CreateLocalUserRequest(string Username, string Email, string Password, string DisplayName, string[] Roles);
public record ResetPasswordRequest(string NewPassword);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Auth endpoints: SSO redirect login, OAuth2 callback, and token introspection.
///
/// Login flow:
///   1. Browser → GET /api/auth/login?tenantId=1
///      Looks up active SSO config, redirects browser to provider's AuthorizationEndpoint.
///   2. Provider → GET /api/auth/callback?code=…&state=…
///      Exchanges code for token, calls GetUserInfo to read identity, redirects to portal.
///   3. Portal /auth/callback reads token from URL fragment, stores in localStorage.
///   4. Portal → GET /api/auth/me (with Bearer) to get canonical TenantContext fields.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ITenantSsoConfigService _sso;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalAuthService _localAuth;
    private readonly string _portalOrigin;

    public AuthController(
        ITenantSsoConfigService sso,
        IHttpClientFactory httpClientFactory,
        ILocalAuthService localAuth,
        IConfiguration configuration)
    {
        _sso = sso;
        _httpClientFactory = httpClientFactory;
        _localAuth = localAuth;
        // CorsOrigin may be comma-separated (multi-origin support); use only the first
        // value for redirect URLs — SSO providers need a single whitelisted URI.
        var corsOrigin = configuration["AdminPortal:CorsOrigin"] ?? "http://localhost:5173";
        _portalOrigin = corsOrigin.Split(',', StringSplitOptions.TrimEntries)[0];
    }

    // ── GET /api/auth/login?tenantId=1 ────────────────────────────────────────
    // Looks up the active SSO config and redirects the browser to the provider's
    // Authorization Endpoint (OAuth2 Authorization Code flow).

    [HttpGet("login")]
    public async Task<IActionResult> Login([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var configs = await _sso.GetForTenantAsync(tenantId, ct);
        var config = configs.FirstOrDefault(c => c.IsActive && !string.IsNullOrEmpty(c.AuthorizationEndpoint));
        if (config is null)
            return NotFound("No active SSO provider with an authorization endpoint configured for this tenant.");

        // Encode tenantId + nonce in state so the callback can route back to the right config
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var statePayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { tenantId, nonce })));

        // redirect_uri must be the API's callback URL — this is what the provider calls back.
        // ProxyBaseUrl in the SSO config should be the API base URL registered with the provider.
        var redirectUri = BuildApiCallbackUri(config.ProxyBaseUrl);

        var authUrl = QueryHelpers.AddQueryString(config.AuthorizationEndpoint!, new Dictionary<string, string?>
        {
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["state"] = statePayload,
        });

        return Redirect(authUrl);
    }

    // ── GET /api/auth/callback?code=…&state=… ────────────────────────────────
    // OAuth2 callback from the provider.
    // 1. Exchanges the code for an access token
    // 2. Calls GetUserInfo to validate the token and read user identity
    // 3. Redirects the browser to the portal's /auth/callback with token in the URL fragment

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(error))
            return BadRequest($"SSO provider returned error: {error}");

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return BadRequest("Missing code or state parameter.");

        // Decode state to get tenantId
        int tenantId;
        try
        {
            var stateJson = Encoding.UTF8.GetString(Convert.FromBase64String(state));
            var stateDoc = JsonDocument.Parse(stateJson);
            tenantId = stateDoc.RootElement.GetProperty("tenantId").GetInt32();
        }
        catch
        {
            return BadRequest("Invalid state parameter.");
        }

        var configs = await _sso.GetForTenantAsync(tenantId, ct);
        var config = configs.FirstOrDefault(c => c.IsActive && !string.IsNullOrEmpty(c.TokenEndpoint));
        if (config is null)
            return NotFound("No active SSO provider with a token endpoint found for this tenant.");

        // ── Resolve per-tenant claim field names ──────────────────────────────
        // ClaimMappingsJson lets each tenant specify which fields in the provider's
        // token/userinfo response map to our internal identity fields.
        // Falls back to well-known OIDC defaults when not configured.
        ClaimMappingsOptions? tenantMappings = null;
        if (!string.IsNullOrEmpty(config.ClaimMappingsJson))
        {
            try { tenantMappings = JsonSerializer.Deserialize<ClaimMappingsOptions>(config.ClaimMappingsJson); }
            catch { /* ignore malformed JSON — fall through to defaults */ }
        }
        var userIdField = tenantMappings?.UserId ?? "sub";
        var emailField = tenantMappings?.Email ?? "email";
        var nameField = tenantMappings?.DisplayName ?? "name";
        var rolesField = tenantMappings?.Roles ?? "roles";
        var groupsField = tenantMappings?.Groups ?? "groups";
        var agentAccessField = tenantMappings?.AgentAccess ?? "agent_access";
        var groupAccessField = tenantMappings?.GroupAccess ?? "group_access";

        var redirectUri = BuildApiCallbackUri(config.ProxyBaseUrl);
        var http = _httpClientFactory.CreateClient("sso-auth");

        // ── Step 1: Exchange authorization code for access token ──────────────
        // Send credentials via HTTP Basic Auth (client_secret_basic) — required by most
        // OIDC-compliant providers. client_id/client_secret are still included in the form
        // body as a fallback for providers that only support client_secret_post.
        string accessToken;
        JsonDocument tokenDoc;
        try
        {
            var basicCredentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(config.ClientId)}:{Uri.EscapeDataString(config.ClientSecret)}"));

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",    "authorization_code"),
                new KeyValuePair<string, string>("code",          code),
                new KeyValuePair<string, string>("redirect_uri",  redirectUri),
                new KeyValuePair<string, string>("client_id",     config.ClientId),
                new KeyValuePair<string, string>("client_secret", config.ClientSecret),
            });

            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, config.TokenEndpoint) { Content = form };
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicCredentials);

            var tokenResponse = await http.SendAsync(tokenRequest, ct);
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);
            if (!tokenResponse.IsSuccessStatusCode)
                return StatusCode(502, $"Token exchange failed ({(int)tokenResponse.StatusCode}): {tokenBody}");

            tokenDoc = JsonDocument.Parse(tokenBody);
            accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("No access_token in token response");
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Token exchange error: {ex.Message}");
        }

        string? userEmail = null;
        string? userName = null;
        string? userId = null;
        string[]? roles = null;
        string[]? groups = null;
        string[]? agentAccess = null;
        string[]? groupAccess = null;

        // ── Step 2a: Extract identity from the id_token (OIDC) ───────────────
        // The id_token is a signed JWT returned alongside the access_token.
        // We read its claims without signature verification here — validation of
        // the access_token happens later via TenantContextMiddleware on every API call.
        // This provides a reliable identity source even when the userinfo endpoint
        // is unavailable or not configured.
        if (tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenProp))
        {
            var idTokenRaw = idTokenProp.GetString();
            if (!string.IsNullOrEmpty(idTokenRaw))
            {
                try
                {
                    var parts = idTokenRaw.Split('.');
                    if (parts.Length >= 2)
                    {
                        var padded = parts[1].Replace('-', '+').Replace('_', '/');
                        padded += new string('=', (4 - padded.Length % 4) % 4);
                        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
                        var idDoc = JsonDocument.Parse(payloadJson);
                        var idRoot = idDoc.RootElement;
                        userId = TryGetString(idRoot, userIdField, "sub", "user_id", "id");
                        userEmail = TryGetString(idRoot, emailField, "email", "mail");
                        userName = TryGetString(idRoot, nameField, "name", "preferred_username", "username");
                        roles = TryGetStringArray(idRoot, rolesField, "roles");
                        groups = TryGetStringArray(idRoot, groupsField, "groups");
                        agentAccess = TryGetStringArray(idRoot, agentAccessField, "agent_access");
                        groupAccess = TryGetStringArray(idRoot, groupAccessField, "group_access");
                    }
                }
                catch
                {
                    // non-fatal — malformed id_token, fall through to userinfo
                }
            }
        }

        // ── Step 2b: Call userinfo endpoint to get authoritative identity ─────
        // Userinfo is authoritative when it succeeds and overrides anything from the id_token.
        // This confirms the access_token is valid and can return richer profile data.
        if (!string.IsNullOrEmpty(config.UserinfoEndpoint))
        {
            try
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var userResponse = await http.GetAsync(config.UserinfoEndpoint, ct);

                if (userResponse.IsSuccessStatusCode)
                {
                    var userJson = await userResponse.Content.ReadAsStringAsync(ct);
                    var userDoc = JsonDocument.Parse(userJson);
                    var root = userDoc.RootElement;

                    // Use per-tenant field names first, then standard OIDC fallbacks
                    var uiUserId = TryGetString(root, userIdField, "sub", "user_id", "id");
                    var uiUserEmail = TryGetString(root, emailField, "email", "mail", "EmailAddress", "email_address");
                    var uiUserName = TryGetString(root, nameField, "name", "display_name", "displayName",
                                                   "full_name", "FullName", "preferred_username", "username");
                    var uiRoles = TryGetStringArray(root, rolesField, "roles", "groups");
                    var uiGroups = TryGetStringArray(root, groupsField, "groups");
                    var uiAgentAccess = TryGetStringArray(root, agentAccessField, "agent_access");
                    var uiGroupAccess = TryGetStringArray(root, groupAccessField, "group_access");

                    // Userinfo wins over id_token when it returns a value
                    userId = uiUserId ?? userId;
                    userEmail = uiUserEmail ?? userEmail;
                    userName = uiUserName ?? userName;
                    roles = uiRoles ?? roles;
                    groups = uiGroups ?? groups;
                    agentAccess = uiAgentAccess ?? agentAccess;
                    groupAccess = uiGroupAccess ?? groupAccess;
                }
            }
            catch
            {
                // non-fatal — id_token identity used as fallback
            }
        }

        // ── Step 3: Issue a local JWT so the portal can use it for API calls ───
        // The SSO access token was only needed to authenticate the user.
        // We issue our own short-lived JWT (signed with the local signing key) so
        // TenantContextMiddleware can validate it without depending on the SSO provider.
        // Fail explicitly if neither sub nor email was returned — the "sso-user" literal
        // would cause all unidentified users to share a single UserProfile row.
        var effectiveUserId = userId ?? userEmail;
        if (string.IsNullOrEmpty(effectiveUserId))
            return BadRequest("SSO provider did not return a user identifier (sub) or email. Cannot complete login.");

        // Normalize roles/groups into canonical Diva roles + access-group IDs, applying the
        // default-user fallback when the IdP supplied no roles. A user with no mapped access
        // groups ends up with an empty group_access list and can therefore invoke only agents
        // that are not restricted to any access group.
        var mapped = SsoClaimMapper.Map(roles, groups, groupAccess, tenantMappings, config.UseRoleMappings);
        roles = mapped.Roles;
        groupAccess = mapped.GroupAccess;

        var localToken = _localAuth.IssueSsoJwt(
            tenantId,
            effectiveUserId,
            userEmail ?? "",
            userName ?? userEmail ?? effectiveUserId,
            roles: roles,
            ssoAccessToken: accessToken,
            groups: groups ?? [],
            agentAccess: agentAccess ?? [],
            groupAccess: groupAccess);

        // ── Step 4: Redirect browser to portal with local token in URL fragment ─
        // Fragment (#) is never sent to the server and doesn't appear in access logs.
        // The portal's /auth/callback page reads window.location.hash to extract the token.
        var fragment = new StringBuilder();
        fragment.Append($"token={Uri.EscapeDataString(localToken)}");
        fragment.Append($"&tenant_id={tenantId}");
        var isAdmin = (roles ?? []).Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase));
        fragment.Append($"&is_admin={(isAdmin ? "true" : "false")}");
        if (userId is not null) fragment.Append($"&user_id={Uri.EscapeDataString(userId)}");
        if (userEmail is not null) fragment.Append($"&email={Uri.EscapeDataString(userEmail)}");
        if (userName is not null) fragment.Append($"&name={Uri.EscapeDataString(userName)}");
        if (!string.IsNullOrEmpty(config.LogoutUrl))
            fragment.Append($"&logout_url={Uri.EscapeDataString(config.LogoutUrl)}");

        return Redirect($"{_portalOrigin}/auth/callback#{fragment}");
    }

    // ── GET /api/auth/logout ──────────────────────────────────────────────────
    // Redirects the browser to the SSO provider's logout URL and sets
    // post_logout_redirect_uri back to /api/auth/logout-callback (this API),
    // which then redirects to the portal /login page.
    //
    // Routing logout through the API means:
    //  1. Only the API URL needs to be whitelisted in the SSO provider (not the portal URL).
    //  2. Works regardless of whether the portal and API are on different origins.

    [HttpGet("logout")]
    public IActionResult Logout([FromQuery] string? logoutUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(logoutUrl))
            return Redirect($"{_portalOrigin}/login");

        var callbackUrl = $"{Request.Scheme}://{Request.Host}/api/auth/logout-callback";

        // Try standard OIDC parameter; the SSO provider must redirect to callbackUrl after logout.
        // If the provider ignores this parameter, logout-callback is never called — see fallback below.
        var redirectUrl = $"{logoutUrl}?post_logout_redirect_uri={Uri.EscapeDataString(callbackUrl)}";
        return Redirect(redirectUrl);
    }

    // ── GET /api/auth/logout-callback ─────────────────────────────────────────
    // Called by the SSO provider after it completes logout.
    // Simply redirects the browser back to the portal login page.

    [HttpGet("logout-callback")]
    public IActionResult LogoutCallback()
        => Redirect($"{_portalOrigin}/login");

    // ── GET /api/auth/providers ───────────────────────────────────────────────
    // Public endpoint — no auth required.
    // Returns all active SSO providers across all tenants so the login page
    // can display a list of providers. Tenant ID is embedded in each entry;
    // clicking a provider button navigates to /api/auth/login?tenantId={tenantId}.
    // Only non-sensitive fields are returned.

    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders(CancellationToken ct)
    {
        var providers = await _sso.GetAllActiveAsync(ct);
        return Ok(providers.Select(p => new
        {
            p.Config.Id,
            p.Config.TenantId,
            p.Config.ProviderName,
            tenantName = p.TenantName,
        }));
    }

    // ── GET /api/auth/setup ───────────────────────────────────────────────────
    // Returns whether the master admin has been configured yet.
    // Used by the portal to decide whether to show the setup form or the login form.

    [HttpGet("setup")]
    public async Task<IActionResult> SetupStatus(CancellationToken ct)
    {
        var configured = await _localAuth.MasterAdminExistsAsync(ct);
        return Ok(new { isConfigured = configured });
    }

    // ── POST /api/auth/setup ─────────────────────────────────────────────────
    // First-time platform setup: creates the master admin user (TenantId=0).
    // Fails with 409 if a master admin already exists.
    // This endpoint is public (no auth) — secure by creating the master admin
    // immediately after first deployment.

    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromBody] SetupMasterAdminRequest req, CancellationToken ct)
    {
        if (await _localAuth.MasterAdminExistsAsync(ct))
            return Conflict(new { message = "Master admin already configured. Use the admin login endpoint." });

        var dto = new CreateLocalUserDto(req.Username, req.Email, req.Password, req.DisplayName, ["master_admin"]);
        var user = await _localAuth.CreateUserAsync(0, dto, ct);  // TenantId=0 = platform-level

        return Ok(new { user.Id, user.Username, user.Email, message = "Master admin created. You can now sign in via POST /api/auth/admin." });
    }

    // ── POST /api/auth/admin ──────────────────────────────────────────────────
    // Master admin login. Authenticates against TenantId=0 local users only.
    // Returns the same token shape as /api/auth/local so the portal can store
    // it in localStorage — except tenantId will be 0 and isMasterAdmin=true.

    [HttpPost("admin")]
    public async Task<IActionResult> AdminLogin([FromBody] AdminLoginRequest req, CancellationToken ct)
    {
        var result = await _localAuth.LoginAsync(0, req.Username, req.Password, ct);
        if (result is null)
            return Unauthorized("Invalid credentials");

        return Ok(new
        {
            token = result.Token,
            email = result.Email,
            name = result.DisplayName,
            userId = result.UserId,
            tenantId = result.TenantId,   // 0 = platform level
            roles = result.Roles,
            isMasterAdmin = true,
            isAdmin = true,
        });
    }

    // ── GET /api/auth/discover?email=user@company.com ────────────────────────
    // Tenant discovery for multi-tenant setups.
    //
    // Discovery order:
    //   1. SSO domain match — extract domain from email, find SSO config whose EmailDomains
    //      list contains that domain (admin-configured, explicit mapping). This is the primary
    //      path for SSO users.
    //   2. Registered email fallback — exact email match in LocalUsers or UserProfiles.
    //      Covers local users and SSO users whose provider domain isn't configured.
    //
    // Returns 404 when neither path finds a match.

    [HttpGet("discover")]
    public async Task<IActionResult> Discover([FromQuery] string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest("email is required");

        // ── Path 1: SSO domain mapping ────────────────────────────────────────
        var atIndex = email.IndexOf('@');
        if (atIndex > 0)
        {
            var domain = email[(atIndex + 1)..];
            var ssoConfig = await _sso.FindBySsoDomainAsync(domain, ct);
            if (ssoConfig is not null)
            {
                return Ok(new
                {
                    tenantId = ssoConfig.TenantId,
                    providerName = ssoConfig.ProviderName,
                    hasSso = !string.IsNullOrEmpty(ssoConfig.AuthorizationEndpoint),
                    hasLocalAuth = true,
                });
            }
        }

        // ── Path 2: registered email fallback ────────────────────────────────
        var tenantId = await _sso.FindTenantByRegisteredEmailAsync(email, ct);
        if (tenantId is null)
            return NotFound(new { message = "Email not registered. Contact your administrator." });

        var configs = await _sso.GetForTenantAsync(tenantId.Value, ct);
        var activeSso = configs.FirstOrDefault(c => c.IsActive && !string.IsNullOrEmpty(c.AuthorizationEndpoint));

        return Ok(new
        {
            tenantId,
            providerName = activeSso?.ProviderName,   // null = no SSO, only local login available
            hasSso = activeSso is not null,
            hasLocalAuth = true,
        });
    }

    // ── GET /api/auth/me ──────────────────────────────────────────────────────

    [HttpGet("me")]
    public IActionResult Me()
    {
        var t = HttpContext.TryGetTenantContext();
        if (t is null) return Unauthorized();

        return Ok(new
        {
            t.UserId,
            t.UserEmail,
            t.TenantId,
            t.TenantName,
            t.UserRoles,
            t.AgentAccess,
            t.CurrentSiteId,
            t.IsAdmin,
            t.IsMasterAdmin,
        });
    }

    // ── POST /api/auth/local ──────────────────────────────────────────────────
    // Local username/password login. Returns the same shape as the SSO callback
    // fragment params so the portal can handle both authentication paths identically.

    [HttpPost("local")]
    public async Task<IActionResult> LocalLogin([FromBody] LocalLoginRequest req, CancellationToken ct)
    {
        var result = await _localAuth.LoginAsync(req.TenantId, req.Username, req.Password, ct);
        if (result is null)
            return Unauthorized("Invalid username or password");

        return Ok(new
        {
            token = result.Token,
            email = result.Email,
            name = result.DisplayName,
            userId = result.UserId,
            tenantId = result.TenantId,
            roles = result.Roles,
            isAdmin = result.Roles.Any(r => string.Equals(r, "admin", StringComparison.OrdinalIgnoreCase)),
        });
    }

    private int EffectiveTenantId(int requestedTenantId)
    {
        var ctx = HttpContext.TryGetTenantContext();
        return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
    }

    // ── GET /api/auth/local-users?tenantId=1 ─────────────────────────────────

    [HttpGet("local-users")]
    [RequireTenantAdmin]
    public async Task<IActionResult> GetLocalUsers([FromQuery] int tenantId = 1, CancellationToken ct = default)
    {
        var users = await _localAuth.GetUsersAsync(EffectiveTenantId(tenantId), ct);
        return Ok(users.Select(u => new
        {
            u.Id,
            u.Username,
            u.Email,
            u.DisplayName,
            u.Roles,
            u.IsActive,
            u.CreatedAt,
            u.LastLoginAt
        }));
    }

    // ── POST /api/auth/local-users?tenantId=1 ────────────────────────────────

    [HttpPost("local-users")]
    [RequireTenantAdmin]
    public async Task<IActionResult> CreateLocalUser(
        [FromBody] CreateLocalUserRequest req,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var dto = new CreateLocalUserDto(req.Username, req.Email, req.Password, req.DisplayName, req.Roles);
        var user = await _localAuth.CreateUserAsync(EffectiveTenantId(tenantId), dto, ct);
        return Ok(new { user.Id, user.Username, user.Email, user.DisplayName, user.Roles, user.IsActive });
    }

    // ── DELETE /api/auth/local-users/{id}?tenantId=1 ─────────────────────────

    [HttpDelete("local-users/{id:int}")]
    [RequireTenantAdmin]
    public async Task<IActionResult> DeleteLocalUser(
        int id,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        var tid = EffectiveTenantId(tenantId);

        // Prevent removing the last active platform admin.
        if (tid == 0)
        {
            var admins = await _localAuth.GetUsersAsync(0, ct);
            if (admins.Count(u => u.IsActive && u.Id != id) == 0)
                return BadRequest("Cannot delete the last active platform admin.");
        }

        await _localAuth.DeleteUserAsync(tid, id, ct);
        return NoContent();
    }

    // ── POST /api/auth/local-users/{id}/reset-password?tenantId=1 ────────────

    [HttpPost("local-users/{id:int}/reset-password")]
    [RequireTenantAdmin]
    public async Task<IActionResult> ResetLocalUserPassword(
        int id,
        [FromBody] ResetPasswordRequest req,
        [FromQuery] int tenantId = 1,
        CancellationToken ct = default)
    {
        await _localAuth.ResetPasswordAsync(EffectiveTenantId(tenantId), id, req.NewPassword, ct);
        return NoContent();
    }

    // ── POST /api/auth/change-password ─────────────────────────────────────
    // Allows a logged-in local-auth user to change their own password.
    // Requires the current password to prevent abuse from unlocked sessions.
    // Returns 400 for SSO users (no local password record) or wrong current password.

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest req,
        CancellationToken ct)
    {
        var ctx = HttpContext.TryGetTenantContext();
        if (ctx is null)
            return Unauthorized();

        if (!int.TryParse(ctx.UserId, out var userId))
            return BadRequest("Change password is only available for local account users.");

        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("New password must be at least 8 characters.");

        var ok = await _localAuth.ChangePasswordAsync(ctx.TenantId, userId, req.CurrentPassword, req.NewPassword, ct);
        if (!ok)
            return BadRequest("Current password is incorrect.");

        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The redirect_uri sent to the provider — must be the API's callback URL,
    /// matching exactly what was registered in the provider's app config.
    /// ProxyBaseUrl in SSO config = API base URL (e.g. https://api.yourapp.com).
    /// </summary>
    private string BuildApiCallbackUri(string? proxyBaseUrl)
    {
        var baseUrl = string.IsNullOrEmpty(proxyBaseUrl)
            ? $"{Request.Scheme}://{Request.Host}"
            : proxyBaseUrl.TrimEnd('/');
        return $"{baseUrl}/api/auth/callback";
    }

    /// <summary>
    /// Reads the first non-null string value from a list of field name candidates.
    /// Handles providers that use non-standard field names for common identity fields.
    /// </summary>
    private static string? TryGetString(JsonElement root, params string[] fieldNames)
    {
        foreach (var name in fieldNames)
            if (root.TryGetProperty(name, out var prop) &&
                prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        return null;
    }

    /// <summary>
    /// Reads the first matching string array from a list of field name candidates.
    /// Accepts both a JSON array of strings and a comma-delimited single string.
    /// </summary>
    private static string[]? TryGetStringArray(JsonElement root, params string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            if (!root.TryGetProperty(name, out var prop)) continue;
            if (prop.ValueKind == JsonValueKind.Array)
            {
                var result = prop.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
                return result.Length > 0 ? result : null;
            }
            if (prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString();
                if (!string.IsNullOrEmpty(s))
                    return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        return null;
    }
}
