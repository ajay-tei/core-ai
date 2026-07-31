namespace Diva.Core.Models.Widgets;

public record WidgetConfigDto(
    string Id,
    int TenantId,
    string AgentId,
    string Name,
    string[] AllowedOrigins,
    int? SsoConfigId,
    bool AllowAnonymous,
    string? WelcomeMessage,
    string? PlaceholderText,
    WidgetTheme Theme,
    bool RespectSystemTheme,
    bool ShowBranding,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    int? EnvironmentId = null);

public record CreateWidgetRequest(
    string AgentId,
    string Name,
    string[] AllowedOrigins,
    int? SsoConfigId = null,
    bool AllowAnonymous = true,
    string? WelcomeMessage = null,
    string? PlaceholderText = null,
    WidgetTheme? Theme = null,
    bool RespectSystemTheme = true,
    bool ShowBranding = true,
    DateTime? ExpiresAt = null,
    int? EnvironmentId = null);

/// <summary>Returned by GET /api/widget/{id}/init — contains no secrets.</summary>
public record WidgetInitResponse(
    string WidgetId,
    string AgentId,
    string AgentName,
    bool HasSso,
    bool AllowAnonymous,
    string? WelcomeMessage,
    string? PlaceholderText,
    WidgetTheme Theme,
    bool RespectSystemTheme,
    bool ShowBranding);

public record WidgetAuthRequest(string SsoToken);

public record WidgetAuthResponse(string Token, string UserId, DateTime ExpiresAt);

public record WidgetSessionResponse(string Token, string SessionId, DateTime ExpiresAt);
