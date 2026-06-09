using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plutus.Core.Abstractions;
using Plutus.Core.Categorization;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.SimpleFin;
using Plutus.Core.Transfers;

namespace Plutus.Core.Sync;

public sealed class SyncService(
    IDbContextFactory<PlutusDbContext> dbFactory,
    ISimpleFinClient simpleFin,
    ICategorizer categorizer,
    IConnectionProtector protector,
    IOptions<SyncOptions> options,
    TimeProvider timeProvider,
    ILogger<SyncService> logger) : ISyncService
{
    public async Task<SyncRun?> RunAsync(CancellationToken ct = default)
    {
        // One short-lived context for the whole run; never shared across calls.
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var connection = await db.SimpleFinConnections.FirstOrDefaultAsync(ct);
        if (connection is null)
        {
            logger.LogInformation("Sync skipped: no SimpleFIN connection configured.");
            return null;
        }

        var opts = options.Value;
        var now = timeProvider.GetUtcNow();
        var start = connection.LastSyncedAt is { } last
            ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)).AddDays(-opts.OverlapDays)
            : now.AddDays(-opts.LookBackDays);

        var run = new SyncRun { RanAt = now.UtcDateTime };

        try
        {
            var accessUrl = protector.Unprotect(connection.AccessUrl);
            var set = await simpleFin.GetAccountsAsync(accessUrl, start, now, ct);

            var categories = await db.Categories
                .AsNoTracking()
                .OrderBy(c => c.SortOrder)
                .ToListAsync(ct);

            var newTransactions = await IngestAsync(db, set, ct);

            var accountRefs = await db.Accounts
                .AsNoTracking()
                .Select(a => new SyncedAccountRef(a.Id, a.Name, a.Org))
                .ToListAsync(ct);

            await CategorizeAsync(
                db, newTransactions, categories, accountRefs, opts.ExternalCardPayees, now.UtcDateTime, ct);

            run.Status = SyncStatus.Success;
            run.NewTransactionCount = newTransactions.Count;
            connection.LastSyncedAt = now.UtcDateTime;
            logger.LogInformation("Sync succeeded: {Count} new transactions.", newTransactions.Count);
        }
        catch (Exception ex)
        {
            run.Status = SyncStatus.Failed;
            run.Error = ex.Message;
            logger.LogError(ex, "Sync failed.");
        }

        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Upserts accounts and inserts new expense transactions (credits dropped, deduped).</summary>
    private async Task<List<Transaction>> IngestAsync(PlutusDbContext db, SimpleFinAccountSet set, CancellationToken ct)
    {
        // Map incoming accounts to tracked entities (existing or new).
        var incomingAccountIds = set.Accounts.Select(a => a.Id).ToList();
        var existingAccounts = await db.Accounts
            .Where(a => incomingAccountIds.Contains(a.SimpleFinAccountId))
            .ToDictionaryAsync(a => a.SimpleFinAccountId, ct);

        // Gather candidate expense transactions and dedupe against what's already stored. A
        // single malformed amount string from the bridge is skipped (with a warning) rather
        // than thrown — one bad record must not fail the whole sync.
        var candidates = new List<(SimpleFinAccount Account, SimpleFinTransaction Txn, decimal Amount)>();
        foreach (var account in set.Accounts)
        {
            foreach (var txn in account.Transactions ?? [])
            {
                if (!TryParseAmount(txn.Amount, out var amount))
                {
                    logger.LogWarning(
                        "Skipping transaction {Id}: unparseable amount '{Amount}'.", txn.Id, txn.Amount);
                    continue;
                }

                if (amount < 0m) // negative = money out = expense
                {
                    candidates.Add((account, txn, amount));
                }
            }
        }

        var candidateIds = candidates.Select(x => x.Txn.Id).ToList();
        var existingTxnIds = await db.Transactions
            .Where(t => candidateIds.Contains(t.SimpleFinTransactionId))
            .Select(t => t.SimpleFinTransactionId)
            .ToHashSetAsync(ct);

        foreach (var sfAccount in set.Accounts)
        {
            if (!existingAccounts.TryGetValue(sfAccount.Id, out var account))
            {
                account = new Account { SimpleFinAccountId = sfAccount.Id, Name = sfAccount.Name, Currency = sfAccount.Currency };
                db.Accounts.Add(account);
                existingAccounts[sfAccount.Id] = account;
            }

            account.Name = sfAccount.Name;
            account.Org = sfAccount.Org?.Name ?? sfAccount.Org?.Domain;
            account.Currency = sfAccount.Currency;
            if (TryParseAmount(sfAccount.Balance, out var balance))
            {
                account.Balance = balance;
            }
            else
            {
                logger.LogWarning(
                    "Account {Id}: unparseable balance '{Balance}'; leaving previous value.",
                    sfAccount.Id, sfAccount.Balance);
            }

            account.BalanceDate = DateTimeOffset.FromUnixTimeSeconds(sfAccount.BalanceDate).UtcDateTime;
        }

        var newTransactions = new List<Transaction>();
        foreach (var (sfAccount, sfTxn, amount) in candidates)
        {
            if (!existingTxnIds.Add(sfTxn.Id))
            {
                continue; // duplicate within this payload or already stored
            }

            var transaction = new Transaction
            {
                SimpleFinTransactionId = sfTxn.Id,
                Account = existingAccounts[sfAccount.Id],
                PostedDate = DateTimeOffset.FromUnixTimeSeconds(sfTxn.Posted).UtcDateTime,
                Amount = Math.Abs(amount),
                Description = sfTxn.Description,
                IsCategorized = false,
                IsReviewed = false,
            };
            db.Transactions.Add(transaction);
            newTransactions.Add(transaction);
        }

        await db.SaveChangesAsync(ct); // assigns ids before categorization
        return newTransactions;
    }

    /// <summary>
    /// Categorizes freshly inserted transactions. Payments to synced accounts are flagged
    /// as transfers (excluded from spending) without an AI call; everything else is sent to
    /// the categorizer as before.
    /// </summary>
    private async Task CategorizeAsync(
        PlutusDbContext db,
        List<Transaction> transactions,
        IReadOnlyList<Category> categories,
        IReadOnlyList<SyncedAccountRef> accounts,
        IReadOnlyList<string> externalCardPayees,
        DateTime categorizedAt,
        CancellationToken ct)
    {
        if (transactions.Count == 0)
        {
            return;
        }

        var byName = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        byName.TryGetValue("Transfer", out var transferCategory);

        foreach (var transaction in transactions)
        {
            if (transferCategory is not null &&
                TransferDetector.IsTransferPayment(
                    transaction.Description, transaction.AccountId, accounts, externalCardPayees))
            {
                transaction.CategoryId = transferCategory.Id;
                transaction.IsCategorized = true;
                transaction.IsReviewed = true; // transfers don't need manual review
                transaction.CategorizedAt = categorizedAt;
                transaction.Note = "Transfer — excluded from spending";
                continue;
            }

            var result = await categorizer.CategorizeAsync(transaction.Description, note: null, categories, ct);
            if (result is not null)
            {
                transaction.Note = result.Note;
                if (byName.TryGetValue(result.Category, out var category))
                {
                    transaction.CategoryId = category.Id;
                    transaction.IsCategorized = true;
                    transaction.CategorizedAt = categorizedAt;
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool TryParseAmount(string amount, out decimal value) =>
        decimal.TryParse(
            amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
