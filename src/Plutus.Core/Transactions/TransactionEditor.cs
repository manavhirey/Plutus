using Plutus.Core.Models;

namespace Plutus.Core.Transactions;

/// <summary>
/// Applies a manual category/note edit to a transaction (the Transactions-list slide-over).
/// Mirrors the Review save minus the AI refinement and minus marking the row reviewed.
/// </summary>
public static class TransactionEditor
{
    /// <summary>
    /// <paramref name="categoryId"/> null means Uncategorized. Sets <see cref="Transaction.IsCategorized"/>
    /// and stamps <see cref="Transaction.CategorizedAt"/> only when a category is assigned; never touches
    /// <see cref="Transaction.IsReviewed"/>. A blank note is stored as null.
    /// </summary>
    public static void ApplyEdit(Transaction transaction, int? categoryId, string? note, DateTimeOffset now)
    {
        transaction.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        transaction.CategoryId = categoryId;
        transaction.IsCategorized = categoryId is not null;
        if (categoryId is not null)
        {
            transaction.CategorizedAt = now.UtcDateTime;
        }
    }
}
