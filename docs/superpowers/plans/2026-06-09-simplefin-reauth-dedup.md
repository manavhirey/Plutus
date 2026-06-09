# SimpleFIN Re-auth Dedup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop SimpleFIN re-authentication from creating duplicate accounts/transactions (it re-mints account & transaction IDs), and heal the 5 duplicate accounts already in the live DB.

**Architecture:** Two pure helpers in `Plutus.Core` (account match-by-name re-link; multiset content de-dupe) wired into `SyncService.IngestAsync` for always-on prevention, plus a Core merge routine driven by a one-off config-gated `AccountMergeBackfill` (Web) to heal existing duplicates.

**Tech Stack:** .NET 10, xUnit (real in-memory SQLite in tests), EF Core (SQLite). Always run `export PATH="$HOME/.dotnet:$PATH"` before dotnet commands.

**Reference spec:** `docs/superpowers/specs/2026-06-09-simplefin-reauth-dedup-design.md`

**Key constraints (verified):** `Account.SimpleFinAccountId` has a UNIQUE index; `Transaction.SimpleFinTransactionId` has a UNIQUE index; `Transaction → Account` FK is **cascade-delete** (repoint transactions before deleting an account row).

---

### Task 1: `AccountMatcher` pure helper

Matches an incoming SimpleFIN account to an existing row — by ID first, then by exact Name (re-link case).

**Files:**
- Create: `src/Plutus.Core/Sync/AccountMatcher.cs`
- Test: `tests/Plutus.Core.Tests/AccountMatcherTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Plutus.Core.Tests/AccountMatcherTests.cs`:

```csharp
using Plutus.Core.Models;
using Plutus.Core.Sync;

namespace Plutus.Core.Tests;

public sealed class AccountMatcherTests
{
    private static Account Acct(int id, string sfId, string name) =>
        new() { Id = id, SimpleFinAccountId = sfId, Name = name, Currency = "USD" };

    [Fact]
    public void Matches_by_simplefin_id_first()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        var match = AccountMatcher.FindExisting("ACT-old", "Some Other Name", existing);
        Assert.Same(existing[0], match);
    }

    [Fact]
    public void Matches_by_name_when_id_not_found()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        var match = AccountMatcher.FindExisting("ACT-new", "chase freedom unlimited (7463)", existing); // diff case
        Assert.Same(existing[0], match);
    }

    [Fact]
    public void Id_match_wins_over_name_match()
    {
        var byName = Acct(1, "ACT-a", "Savor (7496)");
        var byId = Acct(2, "ACT-b", "Different");
        var match = AccountMatcher.FindExisting("ACT-b", "Savor (7496)", new[] { byName, byId });
        Assert.Same(byId, match);
    }

    [Fact]
    public void Returns_null_when_neither_matches()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        Assert.Null(AccountMatcher.FindExisting("ACT-new", "Brand New Account (9999)", existing));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — `AccountMatcher` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Plutus.Core/Sync/AccountMatcher.cs`:

```csharp
using Plutus.Core.Models;

namespace Plutus.Core.Sync;

/// <summary>
/// Resolves an incoming SimpleFIN account to an existing <see cref="Account"/> row: by
/// <see cref="Account.SimpleFinAccountId"/> first (the stable case), then by exact
/// <see cref="Account.Name"/> (case-insensitive). The name fallback re-links an account whose
/// SimpleFIN id was re-minted by a bridge re-authentication, so it is updated in place instead
/// of being inserted as a duplicate.
/// </summary>
public static class AccountMatcher
{
    public static Account? FindExisting(string simpleFinAccountId, string name, IEnumerable<Account> existing)
    {
        Account? byName = null;
        foreach (var account in existing)
        {
            if (string.Equals(account.SimpleFinAccountId, simpleFinAccountId, StringComparison.Ordinal))
            {
                return account; // exact id match always wins
            }

            if (byName is null && string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                byName = account;
            }
        }

        return byName;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: PASS — all `AccountMatcherTests` green; existing tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/AccountMatcher.cs tests/Plutus.Core.Tests/AccountMatcherTests.cs
git commit -m "feat(core): add AccountMatcher (re-link by id then name)"
```

---

### Task 2: `TransactionDeduper` pure helper (multiset content de-dupe)

Selects which incoming transactions to insert: skip exact-id matches, then per identical content key insert only beyond the already-stored count.

**Files:**
- Create: `src/Plutus.Core/Sync/TransactionDeduper.cs`
- Test: `tests/Plutus.Core.Tests/TransactionDeduperTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Plutus.Core.Tests/TransactionDeduperTests.cs`:

```csharp
using Plutus.Core.Sync;
using Key = Plutus.Core.Sync.TransactionDeduper.ContentKey;

namespace Plutus.Core.Tests;

public sealed class TransactionDeduperTests
{
    private static readonly DateTime Noon = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    // Incoming item shape used by the tests: a stable id + a content key.
    private sealed record Item(string Id, Key Key);

    private static List<Item> Select(
        IReadOnlyList<Item> incoming, ISet<string> existingIds, IDictionary<Key, int> existingCounts) =>
        TransactionDeduper.SelectToInsert(incoming, i => i.Id, i => i.Key, existingIds, existingCounts);

    [Fact]
    public void Skips_ids_already_stored()
    {
        var incoming = new List<Item> { new("x", new Key(Noon, 2.40m, "MBTA")) };
        var result = Select(incoming, new HashSet<string> { "x" }, new Dictionary<Key, int> { [new Key(Noon, 2.40m, "MBTA")] = 1 });
        Assert.Empty(result);
    }

    [Fact]
    public void Suppresses_reauth_reimport_same_content_new_id()
    {
        // Already have one MBTA charge stored; bridge re-sends it with a NEW id after re-auth.
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("new-id", key) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int> { [key] = 1 });
        Assert.Empty(result); // budget = max(0, 1 - 1) = 0
    }

    [Fact]
    public void Keeps_genuine_identical_same_day_charges()
    {
        // Two real $2.40 MBTA taps the same day (both at noon), nothing stored yet.
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("a", key), new("b", key) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int>());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Different_timestamp_is_not_a_duplicate()
    {
        var stored = new Key(Noon, 5.00m, "Coffee");
        var later = new Key(Noon.AddHours(3), 5.00m, "Coffee");
        var incoming = new List<Item> { new("later", later) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int> { [stored] = 1 });
        Assert.Single(result); // different key entirely
    }

    [Fact]
    public void Inserts_only_the_genuinely_new_one_when_some_overlap()
    {
        // Stored has one (id "x"); bridge sends the same content twice (id "x" + a new "y").
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("x", key), new("y", key) };
        var result = Select(incoming, new HashSet<string> { "x" }, new Dictionary<Key, int> { [key] = 1 });
        Assert.Single(result);
        Assert.Equal("y", result[0].Id); // budget = max(0, 2 - 1) = 1; "x" skipped by id, "y" inserted
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — `TransactionDeduper` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Plutus.Core/Sync/TransactionDeduper.cs`:

```csharp
namespace Plutus.Core.Sync;

/// <summary>
/// Decides which incoming transactions to insert for a single account. Two tiers: skip any whose
/// SimpleFIN id is already stored (fast path), then for each identical content key — posted
/// timestamp + amount + description — insert incoming rows only beyond the count already stored.
/// This suppresses a re-authentication re-import (same content, new id) while preserving genuine
/// identical same-day charges (keeps as many as the bridge reports).
/// </summary>
public static class TransactionDeduper
{
    public readonly record struct ContentKey(DateTime Posted, decimal Amount, string Description);

    public static List<T> SelectToInsert<T>(
        IReadOnlyList<T> incoming,
        Func<T, string> idSelector,
        Func<T, ContentKey> keySelector,
        ISet<string> existingIds,
        IDictionary<ContentKey, int> existingKeyCounts)
    {
        // How many the bridge reports per content key in this payload.
        var incomingCounts = new Dictionary<ContentKey, int>();
        foreach (var item in incoming)
        {
            var key = keySelector(item);
            incomingCounts[key] = incomingCounts.GetValueOrDefault(key) + 1;
        }

        // Budget per key = how many MORE we may insert = max(0, reported - alreadyStored).
        var budget = new Dictionary<ContentKey, int>();
        foreach (var (key, count) in incomingCounts)
        {
            existingKeyCounts.TryGetValue(key, out var stored);
            budget[key] = Math.Max(0, count - stored);
        }

        var result = new List<T>();
        foreach (var item in incoming)
        {
            if (existingIds.Contains(idSelector(item)))
            {
                continue; // exact same transaction already stored
            }

            var key = keySelector(item);
            if (budget.GetValueOrDefault(key) > 0)
            {
                result.Add(item);
                budget[key] -= 1;
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: PASS — all `TransactionDeduperTests` green.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/TransactionDeduper.cs tests/Plutus.Core.Tests/TransactionDeduperTests.cs
git commit -m "feat(core): add TransactionDeduper (multiset content de-dupe)"
```

---

### Task 3: Wire re-link + content de-dupe into `SyncService.IngestAsync`

**Files:**
- Modify: `src/Plutus.Core/Sync/SyncService.cs` (replace the `IngestAsync` method body, lines ~81-166)
- Test: `tests/Plutus.Core.Tests/SyncServiceTests.cs` (add two tests)

- [ ] **Step 1: Write the failing tests**

Append these two tests to `SyncServiceTests.cs`, immediately before the closing `public void Dispose()` line:

```csharp
    [Fact]
    public async Task RunAsync_relinks_account_when_simplefin_id_changes_after_reauth()
    {
        // Existing account with the OLD SimpleFIN id and one transaction.
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            var account = new Account { SimpleFinAccountId = "ACT-old", Name = "Savor (7496)", Currency = "USD" };
            seed.Accounts.Add(account);
            seed.Transactions.Add(new Transaction
            {
                SimpleFinTransactionId = "old-txn", Account = account, Description = "Old", Amount = 9m,
                PostedDate = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        // Bridge now reports the SAME account under a NEW id, with a genuinely new transaction.
        var set = new SimpleFinAccountSet(
        [
            new SimpleFinAccount("ACT-new", "Savor (7496)", "USD", "-50.00", 1_700_000_000,
                new SimpleFinOrg("Capital One", null),
            [
                new SimpleFinTransaction("brand-new", 1_700_000_500, "-12.00", "Lunch"),
            ]),
        ], null);

        var run = await NewSync(new FakeSimpleFinClient(set), new FakeCategorizer("Dining")).RunAsync();
        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Success, run!.Status);

        await using var verify = NewContext();
        var accounts = await verify.Accounts.AsNoTracking().ToListAsync();
        Assert.Single(accounts);                                   // re-linked, NOT duplicated
        Assert.Equal("ACT-new", accounts[0].SimpleFinAccountId);   // adopted the new id
        var txns = await verify.Transactions.AsNoTracking().Where(t => t.AccountId == accounts[0].Id).ToListAsync();
        Assert.Equal(2, txns.Count);                               // old + brand-new both on the one account
    }

    [Fact]
    public async Task RunAsync_does_not_reimport_same_transaction_with_new_id()
    {
        var posted = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);
        long postedUnix = new DateTimeOffset(posted, TimeSpan.Zero).ToUnixTimeSeconds();

        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            var account = new Account { SimpleFinAccountId = "ACT-1", Name = "Savor (7496)", Currency = "USD" };
            seed.Accounts.Add(account);
            seed.Transactions.Add(new Transaction
            {
                SimpleFinTransactionId = "mbta-old", Account = account, Description = "MBTA", Amount = 2.40m, PostedDate = posted,
            });
            await seed.SaveChangesAsync();
        }

        // Same account id, but the MBTA charge comes back with a NEW transaction id (re-auth).
        var set = new SimpleFinAccountSet(
        [
            new SimpleFinAccount("ACT-1", "Savor (7496)", "USD", "-2.40", 1_700_000_000, new SimpleFinOrg("Capital One", null),
            [
                new SimpleFinTransaction("mbta-new", postedUnix, "-2.40", "MBTA"),
            ]),
        ], null);

        var run = await NewSync(new FakeSimpleFinClient(set), new FakeCategorizer("Transport")).RunAsync();
        Assert.NotNull(run);
        Assert.Equal(0, run!.NewTransactionCount); // content-duplicate suppressed

        await using var verify = NewContext();
        Assert.Single(await verify.Transactions.AsNoTracking().ToListAsync()); // still just the one
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — the re-link test finds 2 accounts (duplicate) and the re-import test finds 2 transactions.

- [ ] **Step 3: Replace the `IngestAsync` method**

In `src/Plutus.Core/Sync/SyncService.cs`, replace the entire `IngestAsync` method (from its `/// <summary>Upserts accounts...` doc comment through its closing brace) with:

```csharp
    /// <summary>
    /// Upserts accounts and inserts new expense transactions (credits dropped, deduped). Accounts are
    /// matched by SimpleFIN id, then by name (re-linking an account whose id was re-minted on re-auth).
    /// Transactions are deduped by SimpleFIN id and then by content (a re-auth re-import keeps the same
    /// posted time/amount/description but gets a new id).
    /// </summary>
    private async Task<List<Transaction>> IngestAsync(PlutusDbContext db, SimpleFinAccountSet set, CancellationToken ct)
    {
        // Load all accounts so we can re-link by name when SimpleFIN re-mints an id on re-auth.
        var allAccounts = await db.Accounts.ToListAsync(ct);

        // Resolve each incoming account to a tracked entity (existing, re-linked, or new).
        var resolved = new Dictionary<string, Account>(StringComparer.Ordinal); // incoming sf id -> entity
        foreach (var sfAccount in set.Accounts)
        {
            var account = AccountMatcher.FindExisting(sfAccount.Id, sfAccount.Name, allAccounts);
            if (account is null)
            {
                account = new Account { SimpleFinAccountId = sfAccount.Id, Name = sfAccount.Name, Currency = sfAccount.Currency };
                db.Accounts.Add(account);
                allAccounts.Add(account);
            }
            else
            {
                account.SimpleFinAccountId = sfAccount.Id; // re-link: adopt the (possibly new) id
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
            resolved[sfAccount.Id] = account;
        }

        // Gather candidate expense transactions (negative = money out). A single malformed amount is
        // skipped (with a warning) rather than thrown. Dedupe within the payload by id defensively.
        var candidates = new List<(SimpleFinAccount Account, SimpleFinTransaction Txn, decimal Amount)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in set.Accounts)
        {
            foreach (var txn in account.Transactions ?? [])
            {
                if (!TryParseAmount(txn.Amount, out var amount))
                {
                    logger.LogWarning("Skipping transaction {Id}: unparseable amount '{Amount}'.", txn.Id, txn.Amount);
                    continue;
                }

                if (amount < 0m && seenIds.Add(txn.Id))
                {
                    candidates.Add((account, txn, amount));
                }
            }
        }

        // Fast-path dedupe set: SimpleFIN ids already stored.
        var candidateIds = candidates.Select(x => x.Txn.Id).ToList();
        var existingTxnIds = await db.Transactions
            .Where(t => candidateIds.Contains(t.SimpleFinTransactionId))
            .Select(t => t.SimpleFinTransactionId)
            .ToHashSetAsync(ct);

        var newTransactions = new List<Transaction>();

        // Process per resolved account so the content de-dupe compares against the right history.
        foreach (var group in candidates.GroupBy(c => resolved[c.Account.Id]))
        {
            var account = group.Key;

            // Existing content-key counts for this account (only if it is already persisted).
            var existingKeyCounts = new Dictionary<TransactionDeduper.ContentKey, int>();
            if (account.Id != 0)
            {
                var existing = await db.Transactions
                    .Where(t => t.AccountId == account.Id)
                    .Select(t => new { t.PostedDate, t.Amount, t.Description })
                    .ToListAsync(ct);
                foreach (var e in existing)
                {
                    var key = new TransactionDeduper.ContentKey(e.PostedDate, e.Amount, e.Description);
                    existingKeyCounts[key] = existingKeyCounts.GetValueOrDefault(key) + 1;
                }
            }

            var toInsert = TransactionDeduper.SelectToInsert(
                group.ToList(),
                c => c.Txn.Id,
                c => new TransactionDeduper.ContentKey(
                    DateTimeOffset.FromUnixTimeSeconds(c.Txn.Posted).UtcDateTime, Math.Abs(c.Amount), c.Txn.Description),
                existingTxnIds,
                existingKeyCounts);

            foreach (var (_, sfTxn, amount) in toInsert)
            {
                var transaction = new Transaction
                {
                    SimpleFinTransactionId = sfTxn.Id,
                    Account = account,
                    PostedDate = DateTimeOffset.FromUnixTimeSeconds(sfTxn.Posted).UtcDateTime,
                    Amount = Math.Abs(amount),
                    Description = sfTxn.Description,
                    IsCategorized = false,
                    IsReviewed = false,
                };
                db.Transactions.Add(transaction);
                newTransactions.Add(transaction);
            }
        }

        await db.SaveChangesAsync(ct); // assigns ids before categorization
        return newTransactions;
    }
```

- [ ] **Step 4: Run to verify all tests pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build && dotnet test`
Expected: 0 build warnings/errors; all tests pass, including the two new ones and the pre-existing sync tests.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/SyncService.cs tests/Plutus.Core.Tests/SyncServiceTests.cs
git commit -m "feat(sync): re-link accounts by name + content de-dupe transactions on ingest"
```

---

### Task 4: `AccountMerger` (Core) — heal existing duplicates

A DB-operating routine (tested via in-memory SQLite) that merges same-named account rows into one.

**Files:**
- Create: `src/Plutus.Core/Sync/AccountMerger.cs`
- Test: `tests/Plutus.Core.Tests/AccountMergerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Plutus.Core.Tests/AccountMergerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — `AccountMerger` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Plutus.Core/Sync/AccountMerger.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: PASS — both `AccountMergerTests` green; all other tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/AccountMerger.cs tests/Plutus.Core.Tests/AccountMergerTests.cs
git commit -m "feat(core): add AccountMerger to heal duplicate accounts"
```

---

### Task 5: `AccountMergeBackfill` (Web) — config-gated one-off + compose flag

**Files:**
- Create: `src/Plutus.Web/BackgroundServices/AccountMergeBackfill.cs`
- Modify: `src/Plutus.Web/Program.cs` (register the hosted service)
- Modify: `docker-compose.yml` (env wiring)

- [ ] **Step 1: Create the gated service**

Create `src/Plutus.Web/BackgroundServices/AccountMergeBackfill.cs`:

```csharp
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
```

- [ ] **Step 2: Register the hosted service**

In `src/Plutus.Web/Program.cs`, after the line `builder.Services.AddHostedService<SyncDiagnosticService>();`, add:

```csharp
builder.Services.AddHostedService<AccountMergeBackfill>();
```

- [ ] **Step 3: Wire the compose flag**

In `docker-compose.yml`, after the `Plutus__Diag__Sync` line in the `plutus` service `environment:` block, add:

```yaml
      # One-off account-merge backfill: set PLUTUS_BACKFILL_MERGE_ACCOUNTS=true in .env to run once, then unset.
      - Plutus__Backfill__MergeAccounts=${PLUTUS_BACKFILL_MERGE_ACCOUNTS:-false}
```

- [ ] **Step 4: Build and test**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build && dotnet test`
Expected: 0 warnings/errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Web/BackgroundServices/AccountMergeBackfill.cs src/Plutus.Web/Program.cs docker-compose.yml
git commit -m "feat(web): add config-gated AccountMergeBackfill + compose flag"
```

---

### Task 6: Deploy, run the heal on prod, verify

**Files:** none (deploy + one-off data heal). Done by the orchestrator, not a subagent.

- [ ] **Step 1: Snapshot the prod DB (reversible safety net)**

```bash
sg docker -c "docker cp plutus:/data/plutus.db /tmp/plutus.backup-$(date +%s).db"
sg docker -c "docker cp plutus:/data/plutus.db-wal /tmp/plutus.backup.db-wal" 2>/dev/null || true
sg docker -c "docker cp plutus:/data/plutus.db-shm /tmp/plutus.backup.db-shm" 2>/dev/null || true
```

- [ ] **Step 2: Enable the heal flag, build, deploy**

Set `PLUTUS_BACKFILL_MERGE_ACCOUNTS=true` in `/home/manav/Plutus/.env` (use the Write tool, not sed). Then:

```bash
sg docker -c 'export PATH="$HOME/.dotnet:$PATH" && dotnet publish src/Plutus.Web -c Release /t:PublishContainer'
sg docker -c "docker compose up -d"
```

- [ ] **Step 3: Verify the heal ran**

```bash
sg docker -c "docker logs plutus 2>&1 | grep -i 'account-merge backfill'"
```
Expected: "complete: 1+ groups merged, 5 accounts deleted, … transactions repointed, 1 duplicate transactions deleted."

Then copy the DB out (with WAL) and confirm 6 accounts and the corrected total:

```bash
rm -f /tmp/verify.db*; for f in plutus.db plutus.db-wal plutus.db-shm; do sg docker -c "docker cp plutus:/data/$f /tmp/verify.db${f#plutus.db}" 2>/dev/null; done
python3 -c "import sqlite3; c=sqlite3.connect('/tmp/verify.db').cursor(); print('accounts:', c.execute('SELECT COUNT(*) FROM Accounts').fetchone()[0]); print('sum balances:', c.execute('SELECT ROUND(SUM(Balance),2) FROM Accounts').fetchone()[0])"
```
Expected: accounts: 6; sum balances ≈ 53847.32.

- [ ] **Step 4: Disable the flag and redeploy (make it inert)**

Set `PLUTUS_BACKFILL_MERGE_ACCOUNTS` back to absent/false in `.env` (Write tool), then:

```bash
sg docker -c "docker compose up -d"
sg docker -c "docker logs plutus 2>&1 | grep -i 'account-merge backfill is disabled'"
curl -s -o /dev/null -w "HTTP %{http_code}\n" https://plutus.kunigami.cloud
```
Expected: backfill disabled line present; HTTP 200.

- [ ] **Step 5: Push**

```bash
git push origin main
```

---

## Self-Review notes

- **Spec coverage:** ① account re-link (Task 1 helper + Task 3 wiring); ② content de-dupe with posted-timestamp key + count check (Task 2 helper + Task 3 wiring); ③ one-off gated heal with canonical=most-txns, adopt-freshest, repoint, content-drop, delete (Task 4 Core + Task 5 Web + Task 6 run). Exact-name identity rule used in Tasks 1 and 4. Tests per the spec's testing section.
- **Constraints honored:** unique `SimpleFinAccountId` (merge deletes other rows before adopting the fresh id); cascade FK (merge repoints transactions before deleting accounts).
- **Type consistency:** `TransactionDeduper.ContentKey(DateTime, decimal, string)` is the single shared key type, used in `SelectToInsert`, `IngestAsync`, and `AccountMerger`. `AccountMatcher.FindExisting(string, string, IEnumerable<Account>)` and `AccountMerger.MergeDuplicateAccountsAsync(PlutusDbContext, CancellationToken)` signatures match their call sites.
- **No placeholders:** every code/command step is complete.
