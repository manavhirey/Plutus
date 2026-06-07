namespace Plutus.Core.Models;

/// <summary>
/// A spending category. Seeded with a starter set and editable in the UI.
/// The set of category names is what constrains Claude's categorization.
/// </summary>
public class Category
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Hex color used for the category chip in the UI (e.g. "#4F46E5").</summary>
    public string? Color { get; set; }

    /// <summary>True for built-in starter categories. Informational; system categories are still editable.</summary>
    public bool IsSystem { get; set; }

    public int SortOrder { get; set; }

    public List<Transaction> Transactions { get; } = [];
}
