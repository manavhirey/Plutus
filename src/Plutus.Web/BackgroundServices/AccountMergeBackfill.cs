using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Sync;

namespace Plutus.Web.BackgroundServices;

/// <summary>
/// One-off pass that merges duplicate accounts left by a SimpleFIN re-authentication (see
/// <see cref="AccountMerger"/>). Enabled only when <c>Plutus:Backfill:MergeAccounts</c> is true;
/// otherwise a no-op so it is inert in production by default. Run once after deploy, then unset.
/// </summary>
public sealed class AccountMergeBackfill(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AccountMergeBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Plutus:Backfill:MergeAccounts"))
        {
            logger.LogInformation("Account-merge backfill is disabled (Plutus:Backfill:MergeAccounts is not set). Skipping.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

        logger.LogInformation("Account-merge backfill starting.");
        var result = await AccountMerger.MergeDuplicateAccountsAsync(db, stoppingToken);
        logger.LogInformation(
            "Account-merge backfill complete: {Groups} groups merged, {Accounts} accounts deleted, " +
            "{Repointed} transactions repointed, {Deleted} duplicate transactions deleted.",
            result.GroupsMerged, result.AccountsDeleted, result.TransactionsRepointed, result.TransactionsDeleted);
    }
}
