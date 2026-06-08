namespace Plutus.Core.Reporting;

public interface ISpendingReport
{
    /// <summary>
    /// Returns the total spend per category for the given calendar month (local time).
    /// Results are ordered descending by total spend; the Uncategorized bucket is always last.
    /// </summary>
    Task<IReadOnlyList<CategorySpend>> GetMonthlySpendingAsync(int year, int month, CancellationToken ct = default);
}
