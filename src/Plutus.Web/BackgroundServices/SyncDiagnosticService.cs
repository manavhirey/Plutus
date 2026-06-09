using Microsoft.EntityFrameworkCore;
using Plutus.Core.Abstractions;
using Plutus.Core.Data;
using Plutus.Core.SimpleFin;

namespace Plutus.Web.BackgroundServices;

/// <summary>
/// One-off diagnostic: queries the SimpleFIN bridge for the last 14 days both with and
/// without pending transactions, and logs raw counts + newest posted date per account —
/// WITHOUT writing anything. Used to determine why daily sync finds no new transactions.
/// Enabled only when <c>Plutus:Diag:Sync</c> is true; otherwise inert. Run once, then unset.
/// </summary>
public sealed class SyncDiagnosticService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ISimpleFinClient simpleFin,
    IConnectionProtector protector,
    TimeProvider timeProvider,
    ILogger<SyncDiagnosticService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Plutus:Diag:Sync"))
        {
            return;
        }

        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SYNCDIAG: diagnostic failed.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var connection = await db.SimpleFinConnections.FirstOrDefaultAsync(ct);
        if (connection is null)
        {
            logger.LogWarning("SYNCDIAG: no SimpleFIN connection configured.");
            return;
        }

        var accessUrl = protector.Unprotect(connection.AccessUrl);
        var now = timeProvider.GetUtcNow();
        var start = now.AddDays(-14);

        logger.LogInformation(
            "SYNCDIAG: window {Start:o} → {End:o}. Stored LastSyncedAt={Last:o}.",
            start, now, connection.LastSyncedAt);

        foreach (var includePending in new[] { false, true })
        {
            var label = includePending ? "WITH pending" : "POSTED only";
            var set = await simpleFin.GetAccountsAsync(accessUrl, start, now, includePending, ct);

            if (set.Errors is { Count: > 0 } errors)
            {
                logger.LogWarning("SYNCDIAG ({Label}): bridge errors: {Errors}", label, string.Join("; ", errors));
            }

            foreach (var account in set.Accounts)
            {
                var txns = account.Transactions ?? [];
                var newest = txns.Count == 0
                    ? "—"
                    : DateTimeOffset.FromUnixTimeSeconds(txns.Max(t => t.Posted)).UtcDateTime.ToString("o");

                logger.LogInformation(
                    "SYNCDIAG ({Label}): account '{Account}' returned {Count} txns; newest posted {Newest}.",
                    label, account.Name, txns.Count, newest);

                foreach (var t in txns.OrderByDescending(t => t.Posted).Take(5))
                {
                    logger.LogInformation(
                        "SYNCDIAG ({Label}):   {Posted:o}  {Amount,12}  {Desc}",
                        label, DateTimeOffset.FromUnixTimeSeconds(t.Posted).UtcDateTime, t.Amount, t.Description);
                }
            }
        }

        logger.LogInformation("SYNCDIAG: complete (no data was written).");
    }
}
