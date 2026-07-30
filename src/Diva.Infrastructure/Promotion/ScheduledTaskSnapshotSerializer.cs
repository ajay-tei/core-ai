namespace Diva.Infrastructure.Promotion;

using System.Text.Json;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Snapshot serializer for scheduled tasks. AgentId is resolved to/from the target agent's Name
/// (not portable across tenants/environments as a raw id) — mirrors AgentExportService's
/// delegate-name resolution pattern. Matched by Name within the tenant on materialize (see
/// <see cref="IPromotableSnapshotSerializer"/> for the environment-scoping caveat).
/// </summary>
public sealed class ScheduledTaskSnapshotSerializer : IPromotableSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<ScheduledTaskSnapshotSerializer> _logger;

    public string ObjectType => "ScheduledTask";

    public ScheduledTaskSnapshotSerializer(IDatabaseProviderFactory db, ILogger<ScheduledTaskSnapshotSerializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SerializedSnapshot?> SerializeAsync(int tenantId, Guid logicalId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var task = await db.ScheduledTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.LogicalId == logicalId, ct);
        if (task is null)
        {
            return null;
        }

        var agentName = await db.AgentDefinitions
            .Where(a => a.Id == task.AgentId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var snapshot = new ScheduledTaskSnapshot
        {
            Name = task.Name,
            Description = task.Description,
            AgentName = agentName,
            ScheduleType = task.ScheduleType,
            ScheduledAtUtc = task.ScheduledAtUtc,
            RunAtTime = task.RunAtTime,
            DayOfWeek = task.DayOfWeek,
            TimeZoneId = task.TimeZoneId,
            PayloadType = task.PayloadType,
            PromptText = task.PromptText,
            ParametersJson = task.ParametersJson,
            IsEnabled = task.IsEnabled,
            NotifyEmails = task.NotifyEmails,
            NotifyOn = task.NotifyOn,
            SuccessKeywords = task.SuccessKeywords,
        };
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        return new SerializedSnapshot { SnapshotJson = json, Name = task.Name };
    }

    public async Task MaterializeAsync(int tenantId, int environmentId, Guid logicalId, string snapshotJson, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Deserialize<ScheduledTaskSnapshot>(snapshotJson, JsonOptions)
            ?? throw new InvalidOperationException("Invalid scheduled task snapshot JSON.");

        using var db = _db.CreateDbContext();

        var agentId = await db.AgentDefinitions
            .Where(a => a.TenantId == tenantId && a.Name == snapshot.AgentName)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);
        if (agentId is null)
        {
            _logger.LogWarning(
                "Scheduled task snapshot materialize for '{Name}': agent '{AgentName}' not found in tenant {TenantId} — keeping existing AgentId if updating, else task will reference a missing agent.",
                snapshot.Name, snapshot.AgentName, tenantId);
        }

        var task = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Name == snapshot.Name, ct);
        if (task is null)
        {
            task = new ScheduledTaskEntity { TenantId = tenantId, Name = snapshot.Name, CreatedAt = DateTime.UtcNow };
            db.ScheduledTasks.Add(task);
        }

        if (agentId is not null)
        {
            task.AgentId = agentId;
        }

        task.Description = snapshot.Description;
        task.ScheduleType = snapshot.ScheduleType;
        task.ScheduledAtUtc = snapshot.ScheduledAtUtc;
        task.RunAtTime = snapshot.RunAtTime;
        task.DayOfWeek = snapshot.DayOfWeek;
        task.TimeZoneId = snapshot.TimeZoneId;
        task.PayloadType = snapshot.PayloadType;
        task.PromptText = snapshot.PromptText;
        task.ParametersJson = snapshot.ParametersJson;
        task.IsEnabled = snapshot.IsEnabled;
        task.NotifyEmails = snapshot.NotifyEmails;
        task.NotifyOn = snapshot.NotifyOn;
        task.SuccessKeywords = snapshot.SuccessKeywords;
        task.UpdatedAt = DateTime.UtcNow;
        task.LogicalId = logicalId;
        task.EnvironmentId = environmentId;

        await db.SaveChangesAsync(ct);
    }
}
