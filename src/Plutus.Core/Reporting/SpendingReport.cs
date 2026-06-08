using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;

namespace Plutus.Core.Reporting;

public sealed class SpendingReport(IDbContextFactory<PlutusDbContext> dbFactory) : ISpendingReport
{
    public async Task<IReadOnlyList<CategorySpend>> GetMonthlySpendingAsync(
        int year, int month, CancellationToken ct = default)
    {
        // Compute the month window in local time, then convert both bounds to UTC.
        var startLocal = new DateTime(year, month, 1);
        var endLocal = startLocal.AddMonths(1);
        var startUtc = DateTime.SpecifyKind(startLocal, DateTimeKind.Local).ToUniversalTime();
        var endUtc = DateTime.SpecifyKind(endLocal, DateTimeKind.Local).ToUniversalTime();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Group transactions in the window by CategoryId, summing Amount.
        var rows = await db.Transactions
            .AsNoTracking()
            .Where(t => t.PostedDate >= startUtc && t.PostedDate < endUtc)
            .GroupBy(t => t.CategoryId)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return [];
        }

        // Resolve category Name/Color for non-null ids in a single query.
        var categoryIds = rows
            .Where(r => r.CategoryId.HasValue)
            .Select(r => r.CategoryId!.Value)
            .ToList();

        var categoryMap = await db.Categories
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        // Build result list: categorized rows sorted desc by Total, then Uncategorized last.
        var categorized = rows
            .Where(r => r.CategoryId.HasValue)
            .Select(r =>
            {
                categoryMap.TryGetValue(r.CategoryId!.Value, out var cat);
                return new CategorySpend(
                    r.CategoryId,
                    cat?.Name ?? "Unknown",
                    cat?.Color,
                    r.Total);
            })
            .OrderByDescending(s => s.Total)
            .ToList();

        var uncategorized = rows
            .Where(r => !r.CategoryId.HasValue)
            .Select(r => new CategorySpend(null, "Uncategorized", null, r.Total))
            .ToList();

        // Categorized desc by total, then uncategorized bucket(s) at the end.
        return [.. categorized, .. uncategorized];
    }
}
