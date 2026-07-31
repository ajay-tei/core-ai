namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// A tenant-defined deployment environment (e.g. "Production", "Staging", "Dev") in a
/// per-tenant configurable promotion pipeline. Every existing tenant is backfilled with
/// exactly one environment named "Production" (<see cref="IsDefault"/> = true) so this
/// feature is fully backward compatible. Adding more environments and reordering the
/// pipeline is entirely opt-in per tenant.
/// </summary>
public class TenantEnvironmentEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>URL/identifier-safe slug, unique per tenant (e.g. "production", "staging").</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Human-readable name shown in the admin UI (e.g. "Production").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Promotion order — an object can only be promoted to a strictly higher Rank.</summary>
    public int Rank { get; set; }

    /// <summary>
    /// The environment new objects are created in, and that pre-existing (pre-migration) data
    /// was backfilled into. Untagged/legacy traffic always resolves to whichever environment
    /// has this flag set — never to "highest Rank" (see backward-compatibility design notes).
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Optional client/customer label (e.g. "Acme", "Globex") for topologies where a shared
    /// upstream tier (Dev/QA — ClientGroup left null) fans out into multiple per-client
    /// environment pairs (e.g. "Acme-Play"/"Acme-Live", "Globex-Play"/"Globex-Live") that all
    /// share the same Rank tier. Purely a label — Rank alone still governs promotion direction.
    /// Used to (a) group/search the admin UI's environment switcher and manager list, and
    /// (b) block cross-client promotion in <see cref="IPromotionOrchestrationService"/> when both
    /// the source and target environment have a (different) non-null ClientGroup.
    /// </summary>
    public string? ClientGroup { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
