namespace Diva.Infrastructure.Promotion;

using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Implements <see cref="IEntityDraftService"/>. Singleton-safe — creates a new DbContext per call
/// via <see cref="IDatabaseProviderFactory"/> (matches EnvironmentService/PromotionLedgerService).
/// </summary>
public sealed class EntityDraftService : IEntityDraftService
{
    private readonly IDatabaseProviderFactory _db;

    public EntityDraftService(IDatabaseProviderFactory db)
    {
        _db = db;
    }

    public async Task<EntityDraftDto?> GetDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var draft = await db.EntityDrafts.AsNoTracking().FirstOrDefaultAsync(
            d => d.TenantId == tenantId && d.ObjectType == objectType && d.LogicalId == logicalId && d.EnvironmentId == environmentId, ct);
        return draft is null ? null : new EntityDraftDto { DraftJson = draft.DraftJson, UpdatedAt = draft.UpdatedAt, UpdatedBy = draft.UpdatedBy };
    }

    public async Task SaveDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, string draftJson, string? updatedBy, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var draft = await db.EntityDrafts.FirstOrDefaultAsync(
            d => d.TenantId == tenantId && d.ObjectType == objectType && d.LogicalId == logicalId && d.EnvironmentId == environmentId, ct);
        if (draft is null)
        {
            draft = new EntityDraftEntity
            {
                TenantId = tenantId,
                ObjectType = objectType,
                LogicalId = logicalId,
                EnvironmentId = environmentId,
            };
            db.EntityDrafts.Add(draft);
        }

        draft.DraftJson = draftJson;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.UpdatedBy = updatedBy;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearDraftAsync(int tenantId, string objectType, Guid logicalId, int environmentId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var draft = await db.EntityDrafts.FirstOrDefaultAsync(
            d => d.TenantId == tenantId && d.ObjectType == objectType && d.LogicalId == logicalId && d.EnvironmentId == environmentId, ct);
        if (draft is not null)
        {
            db.EntityDrafts.Remove(draft);
            await db.SaveChangesAsync(ct);
        }
    }
}
