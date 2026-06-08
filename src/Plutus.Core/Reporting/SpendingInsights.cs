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
