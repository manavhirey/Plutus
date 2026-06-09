using Plutus.Core.Models;
using Plutus.Core.Transactions;

namespace Plutus.Core.Tests;

public sealed class TransactionEditorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 18, 0, 0, TimeSpan.Zero);

    private static Transaction Sample() => new()
    {
        SimpleFinTransactionId = "t1",
        Description = "AMZN",
        Amount = 12.34m,
        CategoryId = null,
        IsCategorized = false,
        IsReviewed = true, // already reviewed — editing must not change this
        Note = null,
    };

    [Fact]
    public void Sets_category_note_and_categorized_stamp()
    {
        var t = Sample();
        TransactionEditor.ApplyEdit(t, categoryId: 5, note: "  gift for Sam  ", now: Now);

        Assert.Equal(5, t.CategoryId);
        Assert.True(t.IsCategorized);
        Assert.Equal(Now.UtcDateTime, t.CategorizedAt);
        Assert.Equal("gift for Sam", t.Note); // trimmed
    }

    [Fact]
    public void Leaves_IsReviewed_unchanged()
    {
        var reviewed = Sample(); // IsReviewed = true
        TransactionEditor.ApplyEdit(reviewed, categoryId: 5, note: "x", now: Now);
        Assert.True(reviewed.IsReviewed);

        var unreviewed = Sample();
        unreviewed.IsReviewed = false;
        TransactionEditor.ApplyEdit(unreviewed, categoryId: 5, note: "x", now: Now);
        Assert.False(unreviewed.IsReviewed);
    }

    [Fact]
    public void Clearing_category_to_uncategorized_nulls_it()
    {
        var t = Sample();
        t.CategoryId = 5;
        t.IsCategorized = true;
        t.CategorizedAt = Now.UtcDateTime.AddDays(-1);

        TransactionEditor.ApplyEdit(t, categoryId: null, note: "still noted", now: Now);

        Assert.Null(t.CategoryId);
        Assert.False(t.IsCategorized);
        Assert.Equal(Now.UtcDateTime.AddDays(-1), t.CategorizedAt); // unchanged when no category set
        Assert.Equal("still noted", t.Note);
    }

    [Fact]
    public void Blank_note_becomes_null()
    {
        var t = Sample();
        TransactionEditor.ApplyEdit(t, categoryId: 5, note: "   ", now: Now);
        Assert.Null(t.Note);
    }
}
