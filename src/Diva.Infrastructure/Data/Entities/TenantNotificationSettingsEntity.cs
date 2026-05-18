namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Tenant-level default notification config for all scheduled jobs.
/// One row per tenant — TenantId is the primary key (not a filter column),
/// so this entity does NOT implement ITenantEntity.
/// </summary>
public class TenantNotificationSettingsEntity
{
    /// <summary>Primary key — equals TenantId.</summary>
    public int TenantId { get; set; }

    /// <summary>
    /// Comma-separated email addresses applied to ALL scheduled jobs in this tenant
    /// (the global override layer). Null or empty = global override disabled.
    /// </summary>
    public string? GlobalNotifyEmails { get; set; }

    /// <summary>
    /// When the global recipients receive notification:
    /// "failure" | "success" | "always" | null (override disabled).
    /// </summary>
    public string? GlobalNotifyOn { get; set; }
}
