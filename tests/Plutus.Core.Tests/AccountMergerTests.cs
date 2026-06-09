using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.Sync;

namespace Plutus.Core.Tests;

public sealed class AccountMergerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlutusDbContext> _options;

    public AccountMergerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<PlutusDbContext>().UseSqlite(_connection).Options;
        using var db = new PlutusDbContext(_options);
        db.Database.EnsureCreated();
    }

    private PlutusDbContext NewContext() => new(_options);

    [Fact]
    public async Task Merges_duplicate_named_accounts_keeping_history_and_fresh_balance()
    {
        var posted = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = NewContext())
        {
            // Old row: more transactions (history) + one MBTA; stale balance/date.
            var oldAcct = new Account { SimpleFinAccountId = "ACT-old", Name = "Savor (7496)", Currency = "USD",
                Balance = -4.80m, BalanceDate = new DateTime(2026, 6, 7, 21, 0, 0, DateTimeKind.Utc), Org = "Capital One" };
            seed.Accounts.Add(oldAcct);
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "old-mbta", Account = oldAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "old-lunch", Account = oldAcct, Description = "Lunch", Amount = 12m, PostedDate = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc) });

            // New row: re-import of the SAME MBTA (new id) + fresh balance/date.
            var newAcct = new Account { SimpleFinAccountId = "ACT-new", Name = "Savor (7496)", Currency = "USD",
                Balance = -225.73m, BalanceDate = new DateTime(2026, 6, 9, 13, 53, 0, DateTimeKind.Utc), Org = "Capital One" };
            seed.Accounts.Add(newAcct);
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "new-mbta", Account = newAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            var result = await AccountMerger.MergeDuplicateAccountsAsync(act);
            Assert.Equal(1, result.GroupsMerged);
            Assert.Equal(1, result.AccountsDeleted);
            Assert.Equal(1, result.TransactionsDeleted); // the duplicate MBTA
        }

        await using var verify = NewContext();
        var accounts = await verify.Accounts.AsNoTracking().ToListAsync();
        Assert.Single(accounts);
        Assert.Equal("ACT-new", accounts[0].SimpleFinAccountId);      // adopted fresh id
        Assert.Equal(-225.73m, accounts[0].Balance);                  // adopted fresh balance
        var txns = await verify.Transactions.AsNoTracking().ToListAsync();
        Assert.Equal(2, txns.Count);                                  // MBTA (once) + Lunch
        Assert.All(txns, t => Assert.Equal(accounts[0].Id, t.AccountId));
        Assert.Single(txns, t => t.Description == "MBTA");            // dup removed
    }

    [Fact]
    public async Task Keeps_two_genuine_identical_same_day_charges()
    {
        var posted = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = NewContext())
        {
            var oldAcct = new Account { SimpleFinAccountId = "ACT-old", Name = "Savor (7496)", Currency = "USD",
                BalanceDate = new DateTime(2026, 6, 7, 21, 0, 0, DateTimeKind.Utc) };
            seed.Accounts.Add(oldAcct);
            // Two real MBTA taps the same day on the old account.
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "o1", Account = oldAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "o2", Account = oldAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });

            var newAcct = new Account { SimpleFinAccountId = "ACT-new", Name = "Savor (7496)", Currency = "USD",
                BalanceDate = new DateTime(2026, 6, 9, 13, 0, 0, DateTimeKind.Utc) };
            seed.Accounts.Add(newAcct);
            // Re-auth re-fetched BOTH of them (new ids).
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "n1", Account = newAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });
            seed.Transactions.Add(new Transaction { SimpleFinTransactionId = "n2", Account = newAcct, Description = "MBTA", Amount = 2.40m, PostedDate = posted });
            await seed.SaveChangesAsync();
        }

        await using (var act = NewContext())
        {
            await AccountMerger.MergeDuplicateAccountsAsync(act);
        }

        await using var verify = NewContext();
        Assert.Single(await verify.Accounts.AsNoTracking().ToListAsync());
        Assert.Equal(2, (await verify.Transactions.AsNoTracking().ToListAsync()).Count); // both genuine taps kept
    }

    public void Dispose() => _connection.Dispose();
}
