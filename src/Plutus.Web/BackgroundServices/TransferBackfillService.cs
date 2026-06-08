using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Transfers;

namespace Plutus.Web.BackgroundServices;

/// <summary>
/// One-off pass that re-runs transfer detection over every existing transaction and moves
/// payments to synced accounts into the <c>Transfer</c> category (excluded from spending).
/// Enabled only when <c>Plutus:Backfill:Transfers</c> is true; otherwise a no-op so it's inert
/// in production by default. Run once after deploy, then unset.
/// </summary>
public sealed class TransferBackfillService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<TransferBackfillService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Plutus:Backfill:Transfers"))
        {
            logger.LogInformation("Transfer backfill is disabled (Plutus:Backfill:Transfers is not set). Skipping.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

        var transfer = await db.Categories.FirstOrDefaultAsync(c => c.Name == "Transfer", stoppingToken);
        if (transfer is null)
        {
            logger.LogWarning("Transfer backfill: no 'Transfer' category found; aborting.");
            return;
        }

        var accounts = await db.Accounts
            .AsNoTracking()
            .Select(a => new SyncedAccountRef(a.Id, a.Name, a.Org))
            .ToListAsync(stoppingToken);

        var transactions = await db.Transactions.ToListAsync(stoppingToken);
        logger.LogInformation("Transfer backfill starting: {Count} transactions.", transactions.Count);

        int moved = 0;
        foreach (var t in transactions)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (t.CategoryId == transfer.Id)
            {
                continue; // already a transfer
            }

            if (TransferDetector.IsTransferPayment(t.Description, t.AccountId, accounts))
            {
                t.CategoryId = transfer.Id;
                t.IsCategorized = true;
                t.IsReviewed = true;
                t.CategorizedAt = timeProvider.GetUtcNow().UtcDateTime;
                t.Note = "Transfer — excluded from spending";
                moved++;
            }
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Transfer backfill complete: {Moved} of {Count} moved to Transfer.", moved, transactions.Count);
    }
}
