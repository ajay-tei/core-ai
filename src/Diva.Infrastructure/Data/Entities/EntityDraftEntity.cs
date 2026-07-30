namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Generic pending-edit storage for any promotable object type. A single working copy per
/// (TenantId, ObjectType, LogicalId, EnvironmentId) — writing a draft never touches the live row;
/// only an explicit Publish action applies it. Additive mechanism: existing PUT endpoints on the 4
/// promotable entity controllers are untouched and keep applying changes immediately as today —
/// this table is populated only by the new, separate `.../draft` endpoints.
/// </summary>
public class EntityDraftEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    /// <summary>"Agent" | "McpServer" | "ScheduledTask" | "AgentGroup".</summary>
    public string ObjectType { get; set; } = string.Empty;

    public Guid LogicalId { get; set; }
    public int EnvironmentId { get; set; }

    /// <summary>Serialized pending-edit request — same DTO shape each object type's own PUT already accepts.</summary>
    public string DraftJson { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
