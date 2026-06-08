using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;

namespace Plutus.Core.Reporting;

/// <summary>Net worth split by sign: assets (&gt;0) and cards/liabilities (&lt;0).</summary>
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
