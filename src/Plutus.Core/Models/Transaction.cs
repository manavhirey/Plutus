namespace Plutus.Core.Models;

/// <summary>
/// A single expense (outflow). Credits are filtered out at sync time, so only
/// expenses are persisted. Deduped by <see cref="SimpleFinTransactionId"/>.
/// </summary>
public class Transaction
{
    public int Id { get; set; }

    /// <summary>The SimpleFIN transaction id; unique, used to dedupe across overlapping sync windows.</summary>
    public required string SimpleFinTransactionId { get; set; }

    public int AccountId { get; set; }

    public Account? Account { get; set; }

    public DateTime PostedDate { get; set; }

    /// <summary>The expense amount (outflow). Stored as a positive magnitude.</summary>
    public decimal Amount { get; set; }

    public required string Description { get; set; }

    /// <summary>Free-text note — AI-suggested at sync, editable by the user. Editing the note on Review can refine the category.</summary>
    public string? Note { get; set; }

    public int? CategoryId { get; set; }

    public Category? Category { get; set; }

    /// <summary>True once Claude (or a manual override) has assigned a category.</summary>
    public bool IsCategorized { get; set; }

    /// <summary>True once the user has reviewed this transaction on the /review page.</summary>
    public bool IsReviewed { get; set; }

    public DateTime? CategorizedAt { get; set; }
}
