using Diva.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Sessions;

/// <summary>
/// Background service that periodically marks expired <see cref="Data.Entities.AgentSessionEntity"/>
/// rows as "expired" (Status = "expired") and hard-deletes rows older than
/// <c>Sessions:HardDeleteAfterDays</c>.
///
/// This enforces the <c>ExpiresAt</c> field that is already modelled on the entity
/// but was previously never acted on at runtime.
/// </summary>
public sealed class SessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly SessionCleanupOptions _opts;

    public SessionCleanupService(
        IServiceProvider sp,
        ILogger<SessionCleanupService> logger,
        SessionCleanupOptions opts)
    {
        _sp = sp;
        _logger = logger;
        _opts = opts;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield(); // let host finish startup

        if (!_opts.Enabled)
        {
            _logger.LogInformation("Session cleanup disabled (Sessions:CleanupEnabled=false)");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(_opts.IntervalHours), ct); }
            catch (OperationCanceledException) { break; }

            try { await CleanupAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Session cleanup failed");
            }
        }
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();

        // 1. Mark active sessions whose ExpiresAt has passed as "expired".
        var expired = await db.Sessions
            .Where(s => s.Status == "active" && s.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "expired"), ct);

        if (expired > 0)
            _logger.LogInformation("Session cleanup: marked {Count} sessions as expired", expired);

        // 2. Hard-delete sessions (any status) older than HardDeleteAfterDays.
        if (_opts.HardDeleteAfterDays > 0)
        {
            var cutoff = now.AddDays(-_opts.HardDeleteAfterDays);
            var deleted = await db.Sessions
                .Where(s => s.LastActivityAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation(
                    "Session cleanup: hard-deleted {Count} sessions last active before {Cutoff:u}",
                    deleted, cutoff);
        }
    }
}

/// <summary>Options bound from the <c>Sessions</c> config section.</summary>
public sealed class SessionCleanupOptions
{
    /// <summary>Enable/disable the background cleanup worker. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the cleanup job runs, in hours. Default: 6.</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>
    /// Sessions not active for this many days are hard-deleted from the DB.
    /// Set to 0 to disable hard deletion. Default: 90.
    /// </summary>
    public int HardDeleteAfterDays { get; set; } = 90;
}
