namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-level configuration for scheduler run feedback links.
/// One row per tenant — TenantId is the primary key (not a filter column),
/// so this entity does NOT implement ITenantEntity.
/// </summary>
public class TenantFeedbackSettingsEntity
{
    /// <summary>Primary key — equals TenantId.</summary>
    public int TenantId { get; set; }

    /// <summary>Whether to embed a signed feedback link in scheduler emails and prompt variables.</summary>
    public bool EnableFeedbackLinks { get; set; } = true;

    /// <summary>
    /// Base URL of the portal, e.g. "https://app.example.com".
    /// The feedback link is formed as {BaseUrl}/scheduler-feedback?token={token}.
    /// When null or empty, falls back to TaskSchedulerOptions.FeedbackLinkBaseUrl.
    /// </summary>
    public string? FeedbackLinkBaseUrl { get; set; }

    /// <summary>How many days a feedback token remains valid. 0 = use default (30).</summary>
    public int ExpiryDays { get; set; } = 30;
}
