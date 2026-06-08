# Dashboard Insights + Transfer Exclusion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Net Worth and Top-Category widgets to the dashboard, and stop double-counting credit-card bill payments by excluding payments to *synced* cards from spending (keeping BILT, which isn't connected).

**Architecture:** A new `Transfer` system category flagged `ExcludeFromSpending` holds detected card-payment transfers. A pure `TransferDetector` flags payments to synced accounts (by last-4 or issuer org); it runs during sync and via a one-off config-gated backfill. `SpendingReport` filters excluded categories; a new `NetWorthReport` sums account balances; a `SpendingInsights.Top` helper derives the top category. The dashboard renders two new stat cards.

**Tech Stack:** .NET 10, Blazor (InteractiveServer), EF Core (SQLite), xUnit. Build/test always with `export PATH="$HOME/.dotnet:$PATH"` first.

---

## Spec

`docs/superpowers/specs/2026-06-08-dashboard-insights-design.md`

## File Structure

- **Modify** `src/Plutus.Core/Models/Category.cs` — add `ExcludeFromSpending`.
- **Modify** `src/Plutus.Core/Data/Configurations/CategoryConfiguration.cs` — seed `Transfer`.
- **Create** migration under `src/Plutus.Core/Data/Migrations/` (generated).
- **Create** `src/Plutus.Core/Transfers/TransferDetector.cs` — pure detection + `SyncedAccountRef`.
- **Modify** `src/Plutus.Core/Sync/SyncService.cs` — run detector before AI categorization.
- **Modify** `src/Plutus.Core/Reporting/SpendingReport.cs` — exclude `ExcludeFromSpending` categories.
- **Create** `src/Plutus.Core/Reporting/SpendingInsights.cs` — `TopCategory` + `Top()`.
- **Create** `src/Plutus.Core/Reporting/NetWorthReport.cs` — `NetWorth`, `INetWorthReport`, impl.
- **Modify** `src/Plutus.Core/DependencyInjection.cs` — register `INetWorthReport`.
- **Modify** `src/Plutus.Web/Components/Pages/Home.razor` — Net Worth + Top Category cards.
- **Create** `src/Plutus.Web/BackgroundServices/TransferBackfillService.cs` — one-off backfill.
- **Modify** `src/Plutus.Web/Program.cs` — register the backfill service.
- **Create** tests in `tests/Plutus.Core.Tests/`.

---

### Task 1: Schema — `ExcludeFromSpending` + `Transfer` category + migration

**Files:**
- Modify: `src/Plutus.Core/Models/Category.cs`
- Modify: `src/Plutus.Core/Data/Configurations/CategoryConfiguration.cs`
- Create: migration (generated)

- [ ] **Step 1: Add the property to `Category`**

In `src/Plutus.Core/Models/Category.cs`, add after `IsSystem`:

```csharp
    /// <summary>When true, transactions in this category are omitted from all spending totals and charts (e.g. Transfer).</summary>
    public bool ExcludeFromSpending { get; set; }
```

- [ ] **Step 2: Seed the `Transfer` category**

In `src/Plutus.Core/Data/Configurations/CategoryConfiguration.cs`, the starter set yields ids 1..N from parallel `names`/`colors` arrays with `SortOrder = i`. The existing 12 (ids 1–12) MUST keep their ids and colors unchanged. Append `Transfer` as a 13th entry. Replace the `StarterCategories()` body so the loop still emits the original 12, then add `Transfer` (id 13) with the exclude flag:

```csharp
    private static IEnumerable<Category> StarterCategories()
    {
        string[] names =
        [
            "Groceries", "Dining", "Transport", "Shopping", "Utilities",
            "Housing", "Health", "Entertainment", "Travel", "Subscriptions",
            "Fees", "Other",
        ];

        string[] colors =
        [
            "#16A34A", "#EA580C", "#0891B2", "#7C3AED", "#CA8A04",
            "#0D9488", "#DC2626", "#DB2777", "#2563EB", "#9333EA",
            "#64748B", "#6B7280",
        ];

        for (int i = 0; i < names.Length; i++)
        {
            yield return new Category
            {
                Id = i + 1,
                Name = names[i],
                Color = colors[i],
                IsSystem = true,
                SortOrder = i,
            };
        }

        yield return new Category
        {
            Id = names.Length + 1, // 13
            Name = "Transfer",
            Color = "#94A3B8", // muted slate
            IsSystem = true,
            ExcludeFromSpending = true,
            SortOrder = names.Length, // 12
        };
    }
```

- [ ] **Step 3: Generate the migration**

Run:
```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet tool restore
dotnet ef migrations add AddTransferCategoryAndExcludeFromSpending --project src/Plutus.Core
```
Expected: a new `*_AddTransferCategoryAndExcludeFromSpending.cs` in `src/Plutus.Core/Data/Migrations/`.

- [ ] **Step 4: Verify the migration content**

Open the generated migration. Its `Up()` MUST contain:
- `migrationBuilder.AddColumn<bool>(name: "ExcludeFromSpending", table: "Categories", ... defaultValue: false)`
- an `InsertData` into `Categories` for id 13 `Transfer` with `ExcludeFromSpending = true`.

If the `AddColumn` is missing or the insert is absent, the model edits in Steps 1–2 weren't saved — fix and regenerate.

- [ ] **Step 5: Build**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Plutus.Core/Models/Category.cs src/Plutus.Core/Data/Configurations/CategoryConfiguration.cs src/Plutus.Core/Data/Migrations/
git commit -m "feat(core): add ExcludeFromSpending flag + seed Transfer category"
```

---

### Task 2: `TransferDetector` (pure detection)

**Files:**
- Create: `src/Plutus.Core/Transfers/TransferDetector.cs`
- Test: `tests/Plutus.Core.Tests/TransferDetectorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Plutus.Core.Tests/TransferDetectorTests.cs`:

```csharp
using Plutus.Core.Transfers;

namespace Plutus.Core.Tests;

public sealed class TransferDetectorTests
{
    // Source = Chase College (checking). The others are synced cards.
    private static readonly IReadOnlyList<SyncedAccountRef> Accounts =
    [
        new(1, "CHASE COLLEGE (0670)", "Chase Bank"),            // source (checking)
        new(2, "Chase Freedom Unlimited (7463)", "Chase Bank"),
        new(3, "Chase Sapphire Preferred (7795)", "Chase Bank"),
        new(4, "Blue Cash Everyday® (2006)", "American Express"),
        new(5, "Savor (7496)", "Capital One"),
    ];

    private const int Source = 1;

    [Theory]
    [InlineData("Payment to Chase card ending in 7463 06/04")] // last-4 match -> Freedom
    [InlineData("Payment to Chase card ending in 7795 06/04")] // last-4 match -> Sapphire
    [InlineData("AMERICAN EXPRESS ACH PMT M0828 WEB ID: XXXXXX2111")] // issuer org match
    [InlineData("CAPITAL ONE MOBILE PMT CA084881AF448DE WEB ID: XXXXXX4380")] // issuer org match
    public void Flags_payments_to_synced_cards(string description)
    {
        Assert.True(TransferDetector.IsTransferPayment(description, Source, Accounts));
    }

    [Theory]
    [InlineData("BILT CARD PMT PPD ID: 1844372402")] // BILT not synced -> keep
    [InlineData("Online Payment 29066523695 To Fensdale Property Trust 05/22")] // rent -> keep
    [InlineData("TRADER JOE'S #123 GROCERIES")] // normal purchase, no payment marker
    public void Does_not_flag_non_card_payments(string description)
    {
        Assert.False(TransferDetector.IsTransferPayment(description, Source, Accounts));
    }

    [Fact]
    public void Does_not_match_the_source_account()
    {
        // Description references the source account's own last-4; must not self-match.
        Assert.False(TransferDetector.IsTransferPayment("AUTOPAY 0670", Source, Accounts));
    }

    [Fact]
    public void Empty_description_is_not_a_transfer()
    {
        Assert.False(TransferDetector.IsTransferPayment("", Source, Accounts));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter TransferDetectorTests`
Expected: FAIL — `TransferDetector` / `SyncedAccountRef` do not exist.

- [ ] **Step 3: Implement `TransferDetector`**

Create `src/Plutus.Core/Transfers/TransferDetector.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Plutus.Core.Transfers;

/// <summary>A synced account, reduced to what transfer detection needs.</summary>
public sealed record SyncedAccountRef(int Id, string Name, string? Org);

/// <summary>
/// Deterministically decides whether a transaction is a payment to one of the user's
/// *synced* accounts (i.e. a transfer that should not count as spending). A payment to an
/// unsynced card (e.g. BILT) matches nothing here and stays counted. Heuristic and
/// user-overridable: detected transfers remain visible/editable in the UI.
/// </summary>
public static class TransferDetector
{
    // Ordered longest-first so "ACH PMT"/"E-PAYMENT" win before the shorter substrings.
    private static readonly string[] PaymentMarkers =
        ["ACH PMT", "E-PAYMENT", "EPAYMENT", "AUTOPAY", "PAYMENT", "EPAY", "PMT"];

    private static readonly Regex Last4 = new(@"\((\d{4})\)", RegexOptions.Compiled);

    public static bool IsTransferPayment(string description, int sourceAccountId, IReadOnlyList<SyncedAccountRef> accounts)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var upper = description.ToUpperInvariant();
        if (!PaymentMarkers.Any(upper.Contains))
        {
            return false;
        }

        foreach (var account in accounts)
        {
            if (account.Id == sourceAccountId)
            {
                continue; // never treat a payment as a transfer to its own source account
            }

            var last4 = ExtractLast4(account.Name);
            if (last4 is not null && upper.Contains(last4))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(account.Org) && upper.Contains(account.Org.ToUpperInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the "(1234)" suffix banks put on account names; null if none.</summary>
    internal static string? ExtractLast4(string name)
    {
        var match = Last4.Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter TransferDetectorTests`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Transfers/TransferDetector.cs tests/Plutus.Core.Tests/TransferDetectorTests.cs
git commit -m "feat(core): add TransferDetector for credit-card payment detection"
```

---

### Task 3: Wire detection into `SyncService`

**Files:**
- Modify: `src/Plutus.Core/Sync/SyncService.cs`
- Test: `tests/Plutus.Core.Tests/SyncServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Plutus.Core.Tests/SyncServiceTests.cs` (inside the class). It seeds a connection + a synced card account, then syncs a checking-account payment that references the card's last-4; it must land in `Transfer`, be marked reviewed, and NOT call the categorizer:

```csharp
    [Fact]
    public async Task RunAsync_marks_payment_to_synced_card_as_Transfer_without_categorizing()
    {
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            // A synced credit card, so its last-4 is known to the detector.
            seed.Accounts.Add(new Account
            {
                SimpleFinAccountId = "card1",
                Name = "Chase Freedom Unlimited (7463)",
                Org = "Chase Bank",
                Currency = "USD",
            });
            await seed.SaveChangesAsync();
        }

        // Sync returns the checking account ("acc1") with a payment to card 7463.
        var set = new SimpleFinAccountSet(
        [
            new SimpleFinAccount("acc1", "CHASE COLLEGE (0670)", "USD", "1000.00", 1_700_000_000,
                new SimpleFinOrg("Chase Bank", null),
            [
                new SimpleFinTransaction("pmt1", 1_700_000_500, "-783.13", "Payment to Chase card ending in 7463 06/04"),
            ]),
        ], null);

        var categorizer = new FakeCategorizer("Fees");
        var run = await NewSync(new FakeSimpleFinClient(set), categorizer).RunAsync();

        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Success, run!.Status);

        await using var verify = NewContext();
        var transfer = await verify.Categories.FirstAsync(c => c.Name == "Transfer");
        var txn = await verify.Transactions.AsNoTracking().FirstAsync(t => t.SimpleFinTransactionId == "pmt1");
        Assert.Equal(transfer.Id, txn.CategoryId);
        Assert.True(txn.IsCategorized);
        Assert.True(txn.IsReviewed);
        Assert.Equal(0, categorizer.Calls); // detection short-circuits the Claude call
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter RunAsync_marks_payment_to_synced_card_as_Transfer_without_categorizing`
Expected: FAIL — payment is categorized as "Fees" (or uncategorized), categorizer called once.

- [ ] **Step 3: Implement the wiring**

In `src/Plutus.Core/Sync/SyncService.cs`:

(a) Add the using at the top:
```csharp
using Plutus.Core.Transfers;
```

(b) In `RunAsync`, after `newTransactions` is produced and before/at the categorization call, build the synced-account refs from the freshly-upserted accounts and pass them through. Replace the `CategorizeAsync` call site:

```csharp
            var newTransactions = await IngestAsync(db, set, ct);

            var accountRefs = await db.Accounts
                .AsNoTracking()
                .Select(a => new SyncedAccountRef(a.Id, a.Name, a.Org))
                .ToListAsync(ct);

            await CategorizeAsync(db, newTransactions, categories, accountRefs, now.UtcDateTime, ct);
```

(c) Change the `CategorizeAsync` signature and add the transfer short-circuit. Replace the whole method:

```csharp
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
                TransferDetector.IsTransferPayment(transaction.Description, transaction.AccountId, accounts))
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter SyncServiceTests`
Expected: PASS (new test + existing sync tests still green).

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/SyncService.cs tests/Plutus.Core.Tests/SyncServiceTests.cs
git commit -m "feat(core): flag payments to synced cards as transfers during sync"
```

---

### Task 4: Exclude `ExcludeFromSpending` categories from `SpendingReport`

**Files:**
- Modify: `src/Plutus.Core/Reporting/SpendingReport.cs`
- Test: `tests/Plutus.Core.Tests/SpendingReportTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `tests/Plutus.Core.Tests/SpendingReportTests.cs` (inside the class). `Transfer` is seeded category id 13:

```csharp
    [Fact]
    public async Task GetMonthlySpending_excludes_ExcludeFromSpending_categories()
    {
        const int dining = 2;    // seeded
        const int transfer = 13; // seeded, ExcludeFromSpending = true

        await AddTransactionAsync(2026, 6, 10, 40m, dining);
        await AddTransactionAsync(2026, 6, 11, 783m, transfer); // must be excluded

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Single(result);
        Assert.Equal("Dining", result[0].Name);
        Assert.Equal(40m, result[0].Total);
        Assert.DoesNotContain(result, r => r.Name == "Transfer");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter GetMonthlySpending_excludes_ExcludeFromSpending_categories`
Expected: FAIL — result contains the Transfer row.

- [ ] **Step 3: Implement the exclusion**

In `src/Plutus.Core/Reporting/SpendingReport.cs`, after opening the context and before the grouping query, resolve excluded ids and add a filter:

```csharp
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var excludedCategoryIds = await db.Categories
            .AsNoTracking()
            .Where(c => c.ExcludeFromSpending)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // Group transactions in the window by CategoryId, summing Amount.
        var rows = await db.Transactions
            .AsNoTracking()
            .Where(t => t.PostedDate >= startUtc && t.PostedDate < endUtc)
            .Where(t => t.CategoryId == null || !excludedCategoryIds.Contains(t.CategoryId.Value))
            .GroupBy(t => t.CategoryId)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync(ct);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter SpendingReportTests`
Expected: PASS (new test + existing report tests still green).

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Reporting/SpendingReport.cs tests/Plutus.Core.Tests/SpendingReportTests.cs
git commit -m "feat(core): exclude transfer-type categories from spending report"
```

---

### Task 5: `SpendingInsights.Top` (top-category helper)

**Files:**
- Create: `src/Plutus.Core/Reporting/SpendingInsights.cs`
- Test: `tests/Plutus.Core.Tests/SpendingInsightsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Plutus.Core.Tests/SpendingInsightsTests.cs`:

```csharp
using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

public sealed class SpendingInsightsTests
{
    [Fact]
    public void Top_returns_largest_category_and_share()
    {
        IReadOnlyList<CategorySpend> spend =
        [
            new(2, "Dining", "#EA580C", 60m),
            new(1, "Groceries", "#16A34A", 40m),
        ];

        var top = SpendingInsights.Top(spend);

        Assert.NotNull(top);
        Assert.Equal("Dining", top!.Name);
        Assert.Equal(60m, top.Total);
        Assert.Equal(0.6, top.Share, 3); // 60 / 100
    }

    [Fact]
    public void Top_is_null_for_empty_input()
    {
        Assert.Null(SpendingInsights.Top([]));
    }

    [Fact]
    public void Top_share_is_zero_when_total_is_zero()
    {
        IReadOnlyList<CategorySpend> spend = [new(1, "Groceries", "#16A34A", 0m)];
        var top = SpendingInsights.Top(spend);
        Assert.NotNull(top);
        Assert.Equal(0d, top!.Share);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter SpendingInsightsTests`
Expected: FAIL — `SpendingInsights` / `TopCategory` do not exist.

- [ ] **Step 3: Implement the helper**

Create `src/Plutus.Core/Reporting/SpendingInsights.cs`:

```csharp
namespace Plutus.Core.Reporting;

/// <summary>The single largest spending category for a period.</summary>
/// <param name="Name">Category name (or "Uncategorized").</param>
/// <param name="Total">Total spend in that category.</param>
/// <param name="Share">Fraction of all (non-excluded) spend, 0..1.</param>
public sealed record TopCategory(string Name, decimal Total, double Share);

public static class SpendingInsights
{
    /// <summary>Picks the largest entry and its share of the total. Null if there's no spend.</summary>
    public static TopCategory? Top(IReadOnlyList<CategorySpend> spend)
    {
        if (spend.Count == 0)
        {
            return null;
        }

        var total = spend.Sum(s => s.Total);
        var top = spend.MaxBy(s => s.Total)!;
        var share = total > 0m ? (double)(top.Total / total) : 0d;
        return new TopCategory(top.Name, top.Total, share);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter SpendingInsightsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Reporting/SpendingInsights.cs tests/Plutus.Core.Tests/SpendingInsightsTests.cs
git commit -m "feat(core): add SpendingInsights.Top for top-category widget"
```

---

### Task 6: `NetWorthReport`

**Files:**
- Create: `src/Plutus.Core/Reporting/NetWorthReport.cs`
- Modify: `src/Plutus.Core/DependencyInjection.cs`
- Test: `tests/Plutus.Core.Tests/NetWorthReportTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Plutus.Core.Tests/NetWorthReportTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

public sealed class NetWorthReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlutusDbContext> _options;

    public NetWorthReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<PlutusDbContext>().UseSqlite(_connection).Options;
        using var db = new PlutusDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task GetAsync_sums_assets_and_cards()
    {
        await using (var db = new PlutusDbContext(_options))
        {
            db.Accounts.AddRange(
                new Account { SimpleFinAccountId = "chk", Name = "Checking", Currency = "USD", Balance = 9000m },
                new Account { SimpleFinAccountId = "sav", Name = "College", Currency = "USD", Balance = 46000m },
                new Account { SimpleFinAccountId = "amex", Name = "Amex (2006)", Currency = "USD", Balance = -950m },
                new Account { SimpleFinAccountId = "free", Name = "Freedom (7463)", Currency = "USD", Balance = 0m });
            await db.SaveChangesAsync();
        }

        var report = new NetWorthReport(new TestDbContextFactory(_options));
        var nw = await report.GetAsync();

        Assert.Equal(54050m, nw.Total);  // 9000 + 46000 - 950 + 0
        Assert.Equal(55000m, nw.Assets); // positive balances
        Assert.Equal(-950m, nw.Cards);   // negative balances
    }

    public void Dispose() => _connection.Dispose();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter NetWorthReportTests`
Expected: FAIL — `NetWorthReport` / `NetWorth` do not exist.

- [ ] **Step 3: Implement the report**

Create `src/Plutus.Core/Reporting/NetWorthReport.cs`. Balances are stored as TEXT (MoneyConverter), so materialize before summing:

```csharp
using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;

namespace Plutus.Core.Reporting;

/// <summary>Net worth split by sign: assets (>0) and cards/liabilities (&lt;0).</summary>
public sealed record NetWorth(decimal Total, decimal Assets, decimal Cards);

public interface INetWorthReport
{
    Task<NetWorth> GetAsync(CancellationToken ct = default);
}

public sealed class NetWorthReport(IDbContextFactory<PlutusDbContext> dbFactory) : INetWorthReport
{
    public async Task<NetWorth> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Money is stored as TEXT via MoneyConverter — sum in memory, not in SQL.
        var balances = await db.Accounts.AsNoTracking().Select(a => a.Balance).ToListAsync(ct);

        var total = balances.Sum();
        var assets = balances.Where(b => b > 0m).Sum();
        var cards = balances.Where(b => b < 0m).Sum();
        return new NetWorth(total, assets, cards);
    }
}
```

> Note: `Select(a => a.Balance)` materializes the decimal values to memory via the converter; the `.Sum()` calls run in LINQ-to-objects.

- [ ] **Step 4: Register in DI**

In `src/Plutus.Core/DependencyInjection.cs`, after the `ISpendingReport` registration:

```csharp
        services.AddScoped<INetWorthReport, NetWorthReport>();
```

- [ ] **Step 5: Run tests + build**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test --filter NetWorthReportTests && dotnet build`
Expected: PASS, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Plutus.Core/Reporting/NetWorthReport.cs src/Plutus.Core/DependencyInjection.cs tests/Plutus.Core.Tests/NetWorthReportTests.cs
git commit -m "feat(core): add NetWorthReport (assets/cards/total)"
```

---

### Task 7: Dashboard widgets (`Home.razor`)

**Files:**
- Modify: `src/Plutus.Web/Components/Pages/Home.razor`

- [ ] **Step 1: Inject the net-worth report + add fields**

At the top of `Home.razor`, after `@inject ISpendingReport SpendingReport`:
```razor
@inject INetWorthReport NetWorthReport
@using Plutus.Core.Reporting
```

In `@code`, add fields near the others:
```csharp
    private NetWorth? _netWorth;
    private TopCategory? _topCategory;
```

- [ ] **Step 2: Populate them in `OnInitializedAsync`**

Inside the existing `try` (after `_spend`/`_fills` are set), add:
```csharp
                _topCategory = SpendingInsights.Top(_spend);
```
And after the `try/catch` that loads spending (still inside the outer `try`, before `finally`), add:
```csharp
            _netWorth = await NetWorthReport.GetAsync();
```
(Placing the net-worth load outside the spending `try/catch` keeps a chart failure from hiding net worth.)

- [ ] **Step 3: Render the two new stat cards**

In the first `<div class="card-grid">` (the one holding *Needs Review* and *Last Sync*), add two cards. Put **Net Worth** first:
```razor
        <div class="stat-card">
            <div class="stat-label">Net Worth</div>
            @if (_netWorth is null)
            {
                <div class="stat-value" style="font-size:1rem; color:var(--gray)">—</div>
            }
            else
            {
                <div class="stat-value amount" style="font-size:1.4rem">@Money.Plain(_netWorth.Total)</div>
                <span class="muted">Assets @Money.Plain(_netWorth.Assets) · Cards @Money.Plain(_netWorth.Cards)</span>
            }
        </div>

        <div class="stat-card">
            <div class="stat-label">Top Category · This Month</div>
            @if (_topCategory is null)
            {
                <div class="stat-value" style="font-size:1rem; color:var(--gray)">—</div>
                <span class="muted">No spending yet</span>
            }
            else
            {
                <div class="stat-value amount" style="font-size:1.4rem">@Money.Plain(_topCategory.Total)</div>
                <span class="muted">@_topCategory.Name · @($"{_topCategory.Share:P0}") of spend</span>
            }
        </div>
```

- [ ] **Step 4: Build + visual sanity check**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build`
Expected: 0 errors. (Live verification happens after deploy in Task 10.)

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Web/Components/Pages/Home.razor
git commit -m "feat(web): add Net Worth and Top Category dashboard cards"
```

---

### Task 8: Transfer chip in `Transactions.razor`

**Files:**
- Modify (verify): `src/Plutus.Web/Components/Pages/Transactions.razor`

The transactions list already renders any assigned category as a colored chip:
`<span class="chip" style="background:@(txn.Category.Color ?? "#4a4f65")">@txn.Category.Name</span>`.
Because detected transfers carry the `Transfer` category (muted slate `#94A3B8`), they will **already** render as a muted "Transfer" chip and appear in the category filter dropdown — satisfying the spec's "reuse the existing chip" decision with no code change.

- [ ] **Step 1: Confirm no change is needed**

Read `src/Plutus.Web/Components/Pages/Transactions.razor` and confirm the chip rendering uses `txn.Category.Color`/`txn.Category.Name` generically (it does). No edit required.

- [ ] **Step 2: (No commit — nothing changed.)**

If, on review, you decide the slate chip needs a tweak for contrast, change only the seed color in Task 1's `Transfer` entry — do not special-case Transfer in the view.

---

### Task 9: One-off transfer backfill

**Files:**
- Create: `src/Plutus.Web/BackgroundServices/TransferBackfillService.cs`
- Modify: `src/Plutus.Web/Program.cs`

- [ ] **Step 1: Create the backfill service**

Mirror `NoteBackfillService`. Create `src/Plutus.Web/BackgroundServices/TransferBackfillService.cs`:

```csharp
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
```

- [ ] **Step 2: Register the service**

In `src/Plutus.Web/Program.cs`, after the `NoteBackfillService` registration:
```csharp
builder.Services.AddHostedService<TransferBackfillService>();
```

- [ ] **Step 3: Build**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Plutus.Web/BackgroundServices/TransferBackfillService.cs src/Plutus.Web/Program.cs
git commit -m "feat(web): add one-off transfer backfill (config-gated, inert by default)"
```

---

### Task 10: Full verification + deploy + backfill

**Files:** none (operational)

- [ ] **Step 1: Run the whole suite**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: all tests pass (the original 17 + the new TransferDetector / Sync / SpendingReport / SpendingInsights / NetWorth tests).

- [ ] **Step 2: Build the container image**

Run:
```bash
sg docker -c 'export PATH="$HOME/.dotnet:$PATH"; dotnet publish src/Plutus.Web -c Release /t:PublishContainer'
```
Expected: `Pushed image 'plutus:latest' to local registry via 'docker'.`

- [ ] **Step 3: Deploy (migration applies on startup)**

Run: `sg docker -c "docker compose up -d"`
Then verify: `curl -s -o /dev/null -w "%{http_code}\n" https://plutus.kunigami.cloud/` → `200`.
The new migration applies automatically on startup (the app runs `Migrate()` at boot — confirm in `Program.cs` if unsure).

- [ ] **Step 4: Run the backfill once**

Set `Plutus__Backfill__Transfers=true` in the deploy environment (the gitignored `.env` / compose env), redeploy, and watch logs for `Transfer backfill complete: N of 113 moved to Transfer.` Expected: the synced-card payments (Chase 7463/7795, Amex, Capital One) move to Transfer; BILT and rent stay. Then **unset** the flag and redeploy so it's inert again.

- [ ] **Step 5: Verify on the live site**

Open https://plutus.kunigami.cloud — confirm: Net Worth card shows ≈ $54,432 with the Assets/Cards sub-line; Top Category reflects corrected spend; the "Fees" bucket no longer includes the card payments; transfers show a muted "Transfer" chip on the Transactions page.

- [ ] **Step 6: Code review + merge**

Run `/code-review` on the branch, address findings, then merge to `main` per the usual flow.

---

## Self-Review

**Spec coverage:**
- Net Worth widget → Task 6 (report) + Task 7 (UI). ✅
- Top Category widget → Task 5 (helper) + Task 7 (UI). ✅
- `Category.ExcludeFromSpending` + `Transfer` seed → Task 1. ✅
- Deterministic transfer detection (last-4 / issuer; BILT & rent kept; source excluded) → Task 2. ✅
- Detection at sync, skip Claude, mark reviewed → Task 3. ✅
- Spending excludes transfers → Task 4. ✅
- Transfers visible & tagged (reuse chip) → Task 8. ✅
- One-off backfill (config-gated, inert by default) → Task 9 + Task 10 Step 4. ✅
- Tests for detector / excluded-spend / net worth / top category → Tasks 2,4,5,6. ✅

**Placeholder scan:** none — every code/test step has complete content.

**Type consistency:** `SyncedAccountRef(int Id, string Name, string? Org)` and `TransferDetector.IsTransferPayment(string, int, IReadOnlyList<SyncedAccountRef>)` used identically in Tasks 2/3/9. `NetWorth(Total, Assets, Cards)` and `INetWorthReport.GetAsync` consistent in Tasks 6/7. `TopCategory(Name, Total, Share)` and `SpendingInsights.Top` consistent in Tasks 5/7. Seeded ids: Transfer = 13, Dining = 2 — used consistently in Tasks 1/3/4.
