namespace Diva.Core.Models;

/// <summary>
/// Generic pending-edit store for any promotable object type. Content-agnostic — DraftJson is an
/// opaque string whose shape is whatever the object type's own PUT endpoint already accepts; only
/// the calling controller knows how to (de)serialize it. Additive to the existing PUT endpoints —
/// nothing in this service touches a live entity row.
/// </summary>
public interface IEntityDraftService
{
    Task<EntityDraftDto?> GetDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, CancellationToken ct);

    Task SaveDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, string draftJson, string? updatedBy, CancellationToken ct);

    Task ClearDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, CancellationToken ct);
}

public sealed record EntityDraftDto
{
    public string DraftJson { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
}
