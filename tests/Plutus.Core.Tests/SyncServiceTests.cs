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

    private SyncService NewSync(PlutusDbContext db, ISimpleFinClient client, FakeCategorizer categorizer) =>
        new(db, client, categorizer, new PassthroughProtector(),
            Options.Create(new SyncOptions()), _time, NullLogger<SyncService>.Instance);

    private static SimpleFinAccountSet AccountSetWith(params SimpleFinTransaction[] txns) =>
        new([new SimpleFinAccount("acc1", "Checking", "USD", "100.00", 1_700_000_000, new SimpleFinOrg("Test Bank", null), txns)], null);

    [Fact]
    public async Task RunAsync_returns_null_when_no_connection()
    {
        await using var db = NewContext();
        var sync = NewSync(db, new FakeSimpleFinClient(), new FakeCategorizer("Dining"));

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

        var categorizer = new FakeCategorizer("Dining");

        // Act
        await using (var db = NewContext())
        {
            var run = await NewSync(db, new FakeSimpleFinClient(set), categorizer).RunAsync();
            Assert.NotNull(run);
            Assert.Equal(SyncStatus.Success, run!.Status);
            Assert.Equal(1, run.NewTransactionCount);
        }

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

        var connection = await verify.SimpleFinConnections.SingleAsync();
        Assert.Equal(_time.GetUtcNow().UtcDateTime, connection.LastSyncedAt);
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

        await using var db = NewContext();
        var run = await NewSync(db, failing, new FakeCategorizer("Dining")).RunAsync();

        Assert.NotNull(run);
        Assert.Equal(SyncStatus.Failed, run!.Status);
        Assert.Contains("boom", run.Error);
        Assert.Single(await db.SyncRuns.ToListAsync());
    }

    public void Dispose() => _connection.Dispose();
}
