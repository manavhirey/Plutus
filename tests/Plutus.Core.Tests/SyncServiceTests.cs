using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.SimpleFin;
using Plutus.Core.Sync;

namespace Plutus.Core.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PlutusDbContext> _options;
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero));

    public SyncServiceTests()
    {
        // Real SQLite (in-memory) so converters/SQL behave like production.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<PlutusDbContext>().UseSqlite(_connection).Options;

        using var db = NewContext();
        db.Database.EnsureCreated(); // applies the model incl. seeded categories
    }

    private PlutusDbContext NewContext() => new(_options);

    private SyncService NewSync(ISimpleFinClient client, FakeCategorizer categorizer) =>
        new(new TestDbContextFactory(_options), client, categorizer, new PassthroughProtector(),
            Options.Create(new SyncOptions()), _time, NullLogger<SyncService>.Instance);

    private static SimpleFinAccountSet AccountSetWith(params SimpleFinTransaction[] txns) =>
        new([new SimpleFinAccount("acc1", "Checking", "USD", "100.00", 1_700_000_000, new SimpleFinOrg("Test Bank", null), txns)], null);

    [Fact]
    public async Task RunAsync_returns_null_when_no_connection()
    {
        await using var db = NewContext();
        var sync = NewSync(new FakeSimpleFinClient(), new FakeCategorizer("Dining"));

        var run = await sync.RunAsync();

        Assert.Null(run);
        Assert.Empty(await db.SyncRuns.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_filters_credits_dedupes_and_categorizes()
    {
        // Arrange: a connection + one pre-existing transaction to dedupe against.
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            var account = new Account { SimpleFinAccountId = "acc1", Name = "Checking", Currency = "USD" };
            seed.Accounts.Add(account);
            seed.Transactions.Add(new Transaction
            {
                SimpleFinTransactionId = "dup1",
                Account = account,
                Description = "Existing",
                Amount = 5m,
                PostedDate = _time.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var set = AccountSetWith(
            new SimpleFinTransaction("t-new", 1_700_000_500, "-10.00", "Coffee"), // expense, new
            new SimpleFinTransaction("t-credit", 1_700_000_600, "50.00", "Refund"), // credit, dropped
            new SimpleFinTransaction("dup1", 1_700_000_700, "-5.00", "Existing")); // duplicate, skipped

        var categorizer = new FakeCategorizer("Dining", suggestedNote: "Coffee shop");

        // Act
        var run = await NewSync(new FakeSimpleFinClient(set), categorizer).RunAsync();
        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Success, run!.Status);
        Assert.Equal(1, run.NewTransactionCount);

        // Assert: only the new expense was added; credit dropped; dup skipped; categorized.
        await using var verify = NewContext();
        var transactions = await verify.Transactions.AsNoTracking().ToListAsync();
        Assert.Equal(2, transactions.Count); // dup1 + t-new
        Assert.DoesNotContain(transactions, t => t.SimpleFinTransactionId == "t-credit");

        var added = transactions.Single(t => t.SimpleFinTransactionId == "t-new");
        Assert.Equal(10m, added.Amount); // stored as positive magnitude
        Assert.True(added.IsCategorized);
        var dining = await verify.Categories.SingleAsync(c => c.Name == "Dining");
        Assert.Equal(dining.Id, added.CategoryId);
        Assert.Equal("Coffee shop", added.Note);

        var connection = await verify.SimpleFinConnections.SingleAsync();
        Assert.Equal(_time.GetUtcNow().UtcDateTime, connection.LastSyncedAt);
    }

    [Fact]
    public async Task RunAsync_skips_transactions_with_unparseable_amounts()
    {
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var set = AccountSetWith(
            new SimpleFinTransaction("t-good", 1_700_000_500, "-10.00", "Coffee"), // valid expense
            new SimpleFinTransaction("t-bad", 1_700_000_600, "n/a", "Glitchy")); // malformed amount

        // Act: the bad record must not blow up the whole run.
        var run = await NewSync(new FakeSimpleFinClient(set), new FakeCategorizer("Dining")).RunAsync();

        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Success, run!.Status);
        Assert.Equal(1, run.NewTransactionCount);

        await using var verify = NewContext();
        var transactions = await verify.Transactions.AsNoTracking().ToListAsync();
        Assert.Single(transactions);
        Assert.Equal("t-good", transactions[0].SimpleFinTransactionId);
    }

    [Fact]
    public async Task RunAsync_records_failure_when_client_throws()
    {
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var failing = new FakeSimpleFinClient(throws: new HttpRequestException("boom"));

        var run = await NewSync(failing, new FakeCategorizer("Dining")).RunAsync();

        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Failed, run!.Status);
        Assert.Contains("boom", run.Error);
        await using var db = NewContext();
        Assert.Single(await db.SyncRuns.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_stores_note_even_when_category_unknown()
    {
        // Arrange: seed a connection so sync runs.
        await using (var seed = NewContext())
        {
            seed.SimpleFinConnections.Add(new SimpleFinConnection
            {
                AccessUrl = "https://user:pass@bridge.simplefin.org/simplefin",
                CreatedAt = _time.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        var set = AccountSetWith(
            new SimpleFinTransaction("t-x", 1_700_000_500, "-12.00", "Mystery"));

        var categorizer = new FakeCategorizer("NotARealCategory", suggestedNote: "Mystery merchant");

        // Act
        var run = await NewSync(new FakeSimpleFinClient(set), categorizer).RunAsync();
        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Success, run!.Status);
        Assert.Equal(1, run.NewTransactionCount);

        // Assert: note is stored but transaction remains uncategorized.
        await using var verify = NewContext();
        var added = await verify.Transactions.AsNoTracking()
            .SingleAsync(t => t.SimpleFinTransactionId == "t-x");
        Assert.Equal("Mystery merchant", added.Note);
        Assert.False(added.IsCategorized);
        Assert.Null(added.CategoryId);
    }

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

    public void Dispose() => _connection.Dispose();
}
