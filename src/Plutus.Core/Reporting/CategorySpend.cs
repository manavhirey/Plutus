namespace Plutus.Core.Reporting;

/// <summary>The aggregated spend for a single category within a queried month.</summary>
/// <param name="CategoryId">The category id, or <see langword="null"/> for uncategorized transactions.</param>
/// <param name="Name">The category name, or "Uncategorized" for the null bucket.</param>
/// <param name="Color">The hex color string, or <see langword="null"/> for uncategorized.</param>
/// <param name="Total">The sum of all transaction amounts in this category for the month.</param>
public record CategorySpend(int? CategoryId, string Name, string? Color, decimal Total);
