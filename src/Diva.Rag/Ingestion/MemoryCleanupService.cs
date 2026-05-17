using Diva.Rag.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Ingestion;

/// <summary>
/// Background service that periodically purges expired agent memories from both SQLite and Qdrant.
/// </summary>
public sealed class MemoryCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RagOptions _opts;
    private readonly ILogger<MemoryCleanupService> _logger;

    public MemoryCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<RagOptions> opts,
        ILogger<MemoryCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.EnableAgentMemory)
        {
            _logger.LogInformation("Agent memory cleanup disabled (EnableAgentMemory=false)");
            return;
        }

        var interval = TimeSpan.FromMinutes(_opts.MemoryCleanupIntervalMinutes);
        _logger.LogInformation("Agent memory cleanup service started (interval={Interval}min)",
            _opts.MemoryCleanupIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var memory = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();
                var cleaned = await memory.CleanupExpiredAsync(stoppingToken);
                if (cleaned > 0)
                    _logger.LogInformation("Memory cleanup: removed {Count} expired memories", cleaned);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory cleanup cycle failed");
            }
        }
    }
}
