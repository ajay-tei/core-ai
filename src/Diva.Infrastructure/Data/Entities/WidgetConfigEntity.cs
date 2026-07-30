namespace Diva.Infrastructure.Data.Entities;

public class WidgetConfigEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>JSON string[] of exact origins allowed to embed this widget.</summary>
    public string? AllowedOriginsJson { get; set; }

    /// <summary>Optional FK to TenantSsoConfigs — enables SSO token exchange.</summary>
    public int? SsoConfigId { get; set; }

    public bool AllowAnonymous { get; set; } = true;
    public string? WelcomeMessage { get; set; }
    public string? PlaceholderText { get; set; }

    /// <summary>JSON WidgetTheme. Null = use Light preset defaults.</summary>
    public string? ThemeJson { get; set; }

    public bool RespectSystemTheme { get; set; } = true;
    public bool ShowBranding { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    /// <summary>FK to TenantEnvironmentEntity — which environment this widget serves (Phase E).
    /// Null = untagged (resolves as if using the tenant's IsDefault environment).</summary>
    public int? EnvironmentId { get; set; }
}
