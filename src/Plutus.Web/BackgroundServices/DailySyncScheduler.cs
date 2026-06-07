using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.Sync;

namespace Plutus.Web.BackgroundServices;

/// <summary>
/// Drives automatic sync: a catch-up run on startup (if none succeeded today), then a
/// loop that fires once per day at the configured local time. Each run uses its own DI scope.
/// </summary>
public sealed class DailySyncScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<SyncOptions> options,
    TimeProvider timeProvider,
    ILogger<DailySyncScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CatchUpAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ComputeDelayUntilNextRun();
            logger.LogInformation("Next scheduled sync in {Delay}.", delay);

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunSyncAsync(stoppingToken);
        }
    }

    /// <summary>If no successful sync has happened today, run one now.</summary>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlutusDbContext>();

        var todayUtc = timeProvider.GetUtcNow().UtcDateTime.Date;
        var ranToday = await db.SyncRuns
            .AnyAsync(r => r.Status == SyncStatus.Success && r.RanAt >= todayUtc, ct);

        if (ranToday)
        {
            logger.LogInformation("Catch-up sync skipped: a successful run already happened today.");
            return;
        }

        await ExecuteSyncAsync(scope.ServiceProvider, ct);
    }

    private async Task RunSyncAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await ExecuteSyncAsync(scope.ServiceProvider, ct);
    }

    private async Task ExecuteSyncAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var sync = services.GetRequiredService<ISyncService>();
            await sync.RunAsync(ct);
        }
        catch (Exception ex)
        {
            // SyncService already records failures; this guards the scheduler loop itself.
            logger.LogError(ex, "Scheduled sync threw unexpectedly.");
        }
    }

    private TimeSpan ComputeDelayUntilNextRun()
    {
        var now = timeProvider.GetLocalNow();
        var timeOfDay = ParseDailyTime(options.Value.DailyTime);

        var nextRun = new DateTimeOffset(
            now.Year, now.Month, now.Day,
            timeOfDay.Hours, timeOfDay.Minutes, 0,
            now.Offset);

        if (nextRun <= now)
        {
            nextRun = nextRun.AddDays(1);
        }

        return nextRun - now;
    }

    private static TimeSpan ParseDailyTime(string value) =>
        TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new TimeSpan(6, 0, 0);
}
