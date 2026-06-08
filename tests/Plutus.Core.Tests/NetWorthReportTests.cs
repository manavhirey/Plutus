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
