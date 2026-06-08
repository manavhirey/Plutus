using Plutus.Core.Models;

namespace Plutus.Core.Categorization;

/// <summary>The category Claude selected, a suggested plain-English note, and its confidence (0–1).</summary>
public sealed record CategorizationResult(string Category, string? Note, double Confidence);

public interface ICategorizer
{
    /// <summary>
    /// Asks Claude to pick the best-fit category for a transaction, constrained to the
    /// supplied category list. Returns <c>null</c> if categorization could not be
    /// completed (API error, empty category list, or an unrecognized result) — callers
    /// leave the transaction uncategorized rather than failing.
    /// </summary>
    Task<CategorizationResult?> CategorizeAsync(
        string description,
        string? note,
        IReadOnlyList<Category> categories,
        CancellationToken ct = default);
}
