using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Diva.Infrastructure.Auth;

// ── Public contracts ──────────────────────────────────────────────────────────

public interface ILocalAuthService
{
    Task<LocalLoginResult?> LoginAsync(int tenantId, string usernameOrEmail, string password, CancellationToken ct = default);
    Task<LocalUserEntity> CreateUserAsync(int tenantId, CreateLocalUserDto dto, CancellationToken ct = default);
    Task<List<LocalUserEntity>> GetUsersAsync(int tenantId, CancellationToken ct = default);
    Task DeleteUserAsync(int tenantId, int id, CancellationToken ct = default);
    Task ResetPasswordAsync(int tenantId, int id, string newPassword, CancellationToken ct = default);
    /// <summary>
    /// Changes the password for a local user after verifying the current password.
    /// Returns false when the user is not found or the current password is wrong.
    /// </summary>
    Task<bool> ChangePasswordAsync(int tenantId, int userId, string currentPassword, string newPassword, CancellationToken ct = default);
    Task SetActiveAsync(int tenantId, int id, bool isActive, CancellationToken ct = default);
    /// <summary>Returns true if any active master admin user (TenantId=0) exists.</summary>
    Task<bool> MasterAdminExistsAsync(CancellationToken ct = default);
    ClaimsPrincipal? ValidateLocalToken(string token);
    /// <summary>
    /// Issues a local JWT for an SSO-authenticated user (no local user record needed).
    /// When <paramref name="ssoAccessToken"/> is provided it is embedded as a "sso_token" claim
    /// so MCP servers with PassSsoToken=true receive the real provider token, not the Diva JWT.
    /// </summary>
    string IssueSsoJwt(int tenantId, string userId, string email, string displayName, string[] roles, string? ssoAccessToken = null, string[]? groups = null, string[]? agentAccess = null, string[]? groupAccess = null);

    /// <summary>
    /// Issues a short-lived, agent-scoped JWT for anonymous widget sessions.
    /// The token carries an <c>agent_access</c> claim restricted to <paramref name="agentId"/>
    /// so the bearer can only invoke that single agent.
    /// </summary>
    string IssueWidgetAnonymousJwt(int tenantId, string userId, string agentId, TimeSpan ttl);
}

public record LocalLoginResult(string Token, string UserId, string Email, string DisplayName, int TenantId, string[] Roles);
public record CreateLocalUserDto(string Username, string Email, string Password, string DisplayName, string[] Roles);

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Handles local username/password authentication alongside SSO.
/// Passwords are hashed with PBKDF2-SHA256 (100 000 iterations, 16-byte salt).
/// JWTs are signed with HMAC-SHA256 using the key from LocalAuthOptions.
/// </summary>
public sealed class LocalAuthService : ILocalAuthService
{
    // Default values kept as constants for reference; runtime values come from AppBrandingOptions.
    public const string LOCAL_ISSUER_DEFAULT = "diva-local";
    public const string LOCAL_AUDIENCE_DEFAULT = "diva-api";
    private const int PBKDF2_ITERATIONS = 100_000;
    private const int SALT_SIZE = 16;
    private const int HASH_SIZE = 32;

    private readonly IDatabaseProviderFactory _db;
    private readonly LocalAuthOptions _opts;
    private readonly AppBrandingOptions _branding;

    public LocalAuthService(
        IDatabaseProviderFactory db,
        IOptions<LocalAuthOptions> opts,
        IOptions<AppBrandingOptions> branding)
    {
        _db = db;
        _opts = opts.Value;
        _branding = branding.Value;
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    public async Task<LocalLoginResult?> LoginAsync(
        int tenantId, string usernameOrEmail, string password, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));

        var normalised = usernameOrEmail.ToLowerInvariant();
        var user = await db.LocalUsers
            .Where(u => u.TenantId == tenantId &&
                        u.IsActive &&
                        (u.Username.ToLower() == normalised || u.Email.ToLower() == normalised))
            .FirstOrDefaultAsync(ct);

        if (user is null)
            return null;

        if (!VerifyPassword(password, user.PasswordHash))
            return null;

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var token = IssueJwt(user);
        return new LocalLoginResult(token, user.Id.ToString(), user.Email, user.DisplayName, tenantId, user.Roles);
    }

    // ── Create user ───────────────────────────────────────────────────────────

    public async Task<LocalUserEntity> CreateUserAsync(
        int tenantId, CreateLocalUserDto dto, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));

        var entity = new LocalUserEntity
        {
            TenantId = tenantId,
            Username = dto.Username,
            Email = dto.Email,
            PasswordHash = HashPassword(dto.Password),
            DisplayName = dto.DisplayName,
            Roles = dto.Roles,
        };

        db.LocalUsers.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<List<LocalUserEntity>> GetUsersAsync(int tenantId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        return await db.LocalUsers
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteUserAsync(int tenantId, int id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var user = await db.LocalUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == id, ct)
            ?? throw new KeyNotFoundException($"LocalUser {id} not found in tenant {tenantId}");

        db.LocalUsers.Remove(user);
        await db.SaveChangesAsync(ct);
    }

    // ── Reset password ────────────────────────────────────────────────────────

    public async Task ResetPasswordAsync(int tenantId, int id, string newPassword, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var user = await db.LocalUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == id, ct)
            ?? throw new KeyNotFoundException($"LocalUser {id} not found in tenant {tenantId}");

        user.PasswordHash = HashPassword(newPassword);
        await db.SaveChangesAsync(ct);
    }

    // ── Change own password (requires current password verification) ──────────

    public async Task<bool> ChangePasswordAsync(
        int tenantId, int userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var user = await db.LocalUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, ct);

        if (user is null || !VerifyPassword(currentPassword, user.PasswordHash))
            return false;

        user.PasswordHash = HashPassword(newPassword);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ── Set active ────────────────────────────────────────────────────────────

    public async Task SetActiveAsync(int tenantId, int id, bool isActive, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var user = await db.LocalUsers
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == id, ct)
            ?? throw new KeyNotFoundException($"LocalUser {id} not found in tenant {tenantId}");

        user.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }

    // ── Master admin existence check ─────────────────────────────────────────

    public async Task<bool> MasterAdminExistsAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext(); // TenantId=0 → bypass filter → see all
        return await db.LocalUsers.AnyAsync(u => u.TenantId == 0 && u.IsActive, ct);
    }

    // ── Validate local JWT ────────────────────────────────────────────────────

    public ClaimsPrincipal? ValidateLocalToken(string token)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));

        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = _branding.LocalIssuer,
                ValidateAudience = true,
                ValidAudience = _branding.ApiAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
            }, out _);

            return principal;
        }
        catch
        {
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string IssueJwt(LocalUserEntity user)
        => IssueSsoJwt(user.TenantId, user.Id.ToString(), user.Email, user.DisplayName, user.Roles);

    public string IssueSsoJwt(int tenantId, string userId, string email, string displayName, string[] roles, string? ssoAccessToken = null, string[]? groups = null, string[]? agentAccess = null, string[]? groupAccess = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddHours(_opts.TokenExpiryHours);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   userId),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Name,  displayName),
            new("tenant_id", tenantId.ToString()),
            new("roles",     string.Join(",", roles)),
        };

        // SSO groups are emitted as a separate claim so group-based agent access
        // control works even when the provider returns both roles and groups.
        if (groups is { Length: > 0 })
            claims.Add(new Claim("groups", string.Join(",", groups)));

        // Explicit per-agent allow-list (agent IDs) and access-group IDs resolved during the
        // SSO callback. These drive AgentGroupService access checks on every request.
        if (agentAccess is { Length: > 0 })
            claims.Add(new Claim("agent_access", string.Join(",", agentAccess)));
        if (groupAccess is { Length: > 0 })
            claims.Add(new Claim("group_access", string.Join(",", groupAccess)));

        // Embed the original SSO provider token so it can be propagated to MCP servers
        // that have PassSsoToken=true. Without this, only the Diva local JWT would be
        // available and would be meaningless to SSO-protected tool backends.
        if (!string.IsNullOrEmpty(ssoAccessToken))
            claims.Add(new Claim("sso_token", ssoAccessToken));

        var jwt = new JwtSecurityToken(
            issuer: _branding.LocalIssuer,
            audience: _branding.ApiAudience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public string IssueWidgetAnonymousJwt(int tenantId, string userId, string agentId, TimeSpan ttl)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.Add(ttl);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, string.Empty),
            new(JwtRegisteredClaimNames.Name, "Anonymous"),
            new("tenant_id", tenantId.ToString()),
            new("roles", "user"),
            new("agent_access", agentId),
        };

        var jwt = new JwtSecurityToken(
            issuer: _branding.LocalIssuer,
            audience: _branding.ApiAudience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SALT_SIZE);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, PBKDF2_ITERATIONS, HashAlgorithmName.SHA256, HASH_SIZE);

        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2)
            return false;

        byte[] salt, expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expectedHash = Convert.FromBase64String(parts[1]);
        }
        catch
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, PBKDF2_ITERATIONS, HashAlgorithmName.SHA256, HASH_SIZE);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
