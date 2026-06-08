using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SpendingReport"/>.
/// Uses a real in-memory SQLite connection (same pattern as <see cref="SyncServiceTests"/>)
/// so the EF converters and SQL behaviour match production.
/// </summary>
public sealed class SpendingReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlutusDbContext> _options;

    public SpendingReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<PlutusDbContext>().UseSqlite(_connection).Options;

        using var db = new PlutusDbContext(_options);
        db.Database.EnsureCreated(); // applies model + seeds 12 starter categories
    }

    private SpendingReport NewReport() => new(new TestDbContextFactory(_options));

    // ---------- helpers ----------

    /// <summary>
    /// Builds a UTC DateTime representing the given local date/time components.
    /// Mirrors the production conversion: local → UTC.
    /// </summary>
    private static DateTime LocalToUtc(int year, int month, int day, int hour = 12, int minute = 0) =>
        DateTime.SpecifyKind(new DateTime(year, month, day, hour, minute, 0), DateTimeKind.Local)
                .ToUniversalTime();

    /// <summary>Seed one transaction and return the saved entity.</summary>
    private async Task<Transaction> AddTransactionAsync(
        int year, int month, int day,
        decimal amount,
        int? categoryId,
        string description = "tx")
    {
        await using var db = new PlutusDbContext(_options);

        // Ensure a dummy account exists.
        var account = await db.Accounts.FirstOrDefaultAsync()
            ?? new Account { SimpleFinAccountId = "acc1", Name = "Checking", Currency = "USD" };
        if (account.Id == 0)
        {
            db.Accounts.Add(account);
        }

        var txn = new Transaction
        {
            SimpleFinTransactionId = Guid.NewGuid().ToString(),
            Account = account,
            PostedDate = LocalToUtc(year, month, day),
            Amount = amount,
            Description = description,
            CategoryId = categoryId,
        };
        db.Transactions.Add(txn);
        await db.SaveChangesAsync();
        return txn;
    }

    // ---------- tests ----------

    [Fact]
    public async Task GetMonthlySpendingAsync_returns_empty_when_no_transactions()
    {
        var result = await NewReport().GetMonthlySpendingAsync(2026, 5);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_aggregates_per_category_correctly()
    {
        // Groceries (id=1): two transactions in June 2026 → total 75.50
        await AddTransactionAsync(2026, 6, 5,  50.00m, categoryId: 1);
        await AddTransactionAsync(2026, 6, 10, 25.50m, categoryId: 1);
        // Dining (id=2): one transaction in June 2026 → total 30.00
        await AddTransactionAsync(2026, 6, 15, 30.00m, categoryId: 2);

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Equal(2, result.Count);

        var groceries = result.Single(r => r.CategoryId == 1);
        Assert.Equal("Groceries", groceries.Name);
        Assert.Equal(75.50m, groceries.Total);

        var dining = result.Single(r => r.CategoryId == 2);
        Assert.Equal("Dining", dining.Name);
        Assert.Equal(30.00m, dining.Total);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_results_are_descending_by_total()
    {
        // Dining 120, Groceries 30, Transport 80 → expected order: Dining, Transport, Groceries
        await AddTransactionAsync(2026, 6, 1, 120.00m, categoryId: 2); // Dining
        await AddTransactionAsync(2026, 6, 2,  30.00m, categoryId: 1); // Groceries
        await AddTransactionAsync(2026, 6, 3,  80.00m, categoryId: 3); // Transport

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Equal(3, result.Count);
        Assert.Equal(120.00m, result[0].Total);
        Assert.Equal(80.00m,  result[1].Total);
        Assert.Equal(30.00m,  result[2].Total);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_uncategorized_aggregates_and_is_last()
    {
        // Groceries 10.00; two uncategorized transactions totalling 200.00.
        // Even though uncategorized total > Groceries, Uncategorized must be last.
        await AddTransactionAsync(2026, 6, 1,  10.00m, categoryId: 1); // Groceries
        await AddTransactionAsync(2026, 6, 2, 150.00m, categoryId: null);
        await AddTransactionAsync(2026, 6, 3,  50.00m, categoryId: null);

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Equal(2, result.Count);

        // Groceries comes first (only categorized entry).
        Assert.Equal(1, result[0].CategoryId);
        Assert.Equal(10.00m, result[0].Total);

        // Uncategorized is last despite larger total.
        var uncategorized = result[result.Count - 1];
        Assert.Null(uncategorized.CategoryId);
        Assert.Equal("Uncategorized", uncategorized.Name);
        Assert.Null(uncategorized.Color);
        Assert.Equal(200.00m, uncategorized.Total);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_excludes_prior_month_transactions()
    {
        // May transaction — must be excluded when querying June.
        await AddTransactionAsync(2026, 5, 31, 99.00m, categoryId: 1);
        // June transaction — must be included.
        await AddTransactionAsync(2026, 6, 1,  10.00m, categoryId: 1);

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Single(result);
        Assert.Equal(10.00m, result[0].Total);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_excludes_next_month_transactions()
    {
        // June transaction — included.
        await AddTransactionAsync(2026, 6, 30, 10.00m, categoryId: 1);
        // July transaction — must be excluded when querying June.
        await AddTransactionAsync(2026, 7, 1,  99.00m, categoryId: 1);

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Single(result);
        Assert.Equal(10.00m, result[0].Total);
    }

    [Fact]
    public async Task GetMonthlySpendingAsync_month_boundary_is_correct_at_first_instant_of_month()
    {
        // Transaction exactly at midnight local time of June 1 — must be INCLUDED in June.
        await using (var db = new PlutusDbContext(_options))
        {
            var account = await db.Accounts.FirstOrDefaultAsync()
                ?? new Account { SimpleFinAccountId = "acc1", Name = "Checking", Currency = "USD" };
            if (account.Id == 0)
            {
                db.Accounts.Add(account);
            }

            var startOfJuneUtc = DateTime.SpecifyKind(
                new DateTime(2026, 6, 1, 0, 0, 0), DateTimeKind.Local).ToUniversalTime();

            db.Transactions.Add(new Transaction
            {
                SimpleFinTransactionId = Guid.NewGuid().ToString(),
                Account = account,
                PostedDate = startOfJuneUtc,
                Amount = 42.00m,
                Description = "boundary",
                CategoryId = 1,
            });
            await db.SaveChangesAsync();
        }

        var result = await NewReport().GetMonthlySpendingAsync(2026, 6);

        Assert.Single(result);
        Assert.Equal(42.00m, result[0].Total);
    }

    public void Dispose() => _connection.Dispose();
}
