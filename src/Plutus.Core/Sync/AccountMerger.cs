using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;

namespace Plutus.Core.Sync;

/// <summary>
/// One-off heal for duplicate accounts left behind when a SimpleFIN re-authentication re-minted
/// account ids. Merges rows that share the same <see cref="Models.Account.Name"/> into a single
/// canonical row (the one with the most transactions), adopting the freshest row's id/balance/date,
/// repointing transactions, and dropping content-duplicate transactions.
/// </summary>
public static class AccountMerger
{
    public sealed record MergeResult(int GroupsMerged, int AccountsDeleted, int TransactionsRepointed, int TransactionsDeleted);

    public static async Task<MergeResult> MergeDuplicateAccountsAsync(PlutusDbContext db, CancellationToken ct = default)
    {
        var accounts = await db.Accounts.ToListAsync(ct);
        var groups = accounts
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        int groupsMerged = 0, accountsDeleted = 0, repointed = 0, txnsDeleted = 0;

        foreach (var group in groups)
        {
            var rows = group.ToList();
            var ids = rows.Select(a => a.Id).ToList();
            var txns = await db.Transactions.Where(t => ids.Contains(t.AccountId)).ToListAsync(ct);

            // Target count per content key = the MAX any single source account held for that key
            // (so genuinely identical same-day charges survive, while re-imports collapse).
            var targetByKey = new Dictionary<TransactionDeduper.ContentKey, int>();
            foreach (var perAccount in txns.GroupBy(t => t.AccountId))
            {
                foreach (var perKey in perAccount.GroupBy(t => new TransactionDeduper.ContentKey(t.PostedDate, t.Amount, t.Description)))
                {
                    targetByKey[perKey.Key] = Math.Max(targetByKey.GetValueOrDefault(perKey.Key), perKey.Count());
                }
            }

            // Canonical = most transactions, tie-break lowest id. Freshest = most recent BalanceDate.
            var countById = txns.GroupBy(t => t.AccountId).ToDictionary(g => g.Key, g => g.Count());
            var canonical = rows.OrderByDescending(a => countById.GetValueOrDefault(a.Id)).ThenBy(a => a.Id).First();
            var freshest = rows.OrderByDescending(a => a.BalanceDate).First();
            var freshId = freshest.SimpleFinAccountId;
            var freshBalance = freshest.Balance;
            var freshDate = freshest.BalanceDate;
            var freshOrg = freshest.Org;

            // Repoint every transaction onto the canonical row.
            foreach (var t in txns)
            {
                if (t.AccountId != canonical.Id)
                {
                    t.AccountId = canonical.Id;
                    repointed++;
                }
            }

            // Drop surplus per key (highest id first → keep the originals, remove the re-imports).
            foreach (var perKey in txns.GroupBy(t => new TransactionDeduper.ContentKey(t.PostedDate, t.Amount, t.Description)))
            {
                var keep = targetByKey[perKey.Key];
                foreach (var surplus in perKey.OrderByDescending(t => t.Id).Take(perKey.Count() - keep))
                {
                    db.Transactions.Remove(surplus);
                    txnsDeleted++;
                }
            }

            // Delete the now-empty other rows BEFORE adopting the fresh id (SimpleFinAccountId is unique).
            foreach (var other in rows.Where(a => a.Id != canonical.Id))
            {
                db.Accounts.Remove(other);
                accountsDeleted++;
            }
            await db.SaveChangesAsync(ct);

            canonical.SimpleFinAccountId = freshId;
            canonical.Balance = freshBalance;
            canonical.BalanceDate = freshDate;
            canonical.Org = freshOrg;
            await db.SaveChangesAsync(ct);

            groupsMerged++;
        }

        return new MergeResult(groupsMerged, accountsDeleted, repointed, txnsDeleted);
    }
}
