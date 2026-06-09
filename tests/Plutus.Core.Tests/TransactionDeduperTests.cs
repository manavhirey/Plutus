using Plutus.Core.Sync;
using Key = Plutus.Core.Sync.TransactionDeduper.ContentKey;

namespace Plutus.Core.Tests;

public sealed class TransactionDeduperTests
{
    private static readonly DateTime Noon = new(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc);

    // Incoming item shape used by the tests: a stable id + a content key.
    private sealed record Item(string Id, Key Key);

    private static List<Item> Select(
        IReadOnlyList<Item> incoming, ISet<string> existingIds, IDictionary<Key, int> existingCounts) =>
        TransactionDeduper.SelectToInsert(incoming, i => i.Id, i => i.Key, existingIds, existingCounts);

    [Fact]
    public void Skips_ids_already_stored()
    {
        var incoming = new List<Item> { new("x", new Key(Noon, 2.40m, "MBTA")) };
        var result = Select(incoming, new HashSet<string> { "x" }, new Dictionary<Key, int> { [new Key(Noon, 2.40m, "MBTA")] = 1 });
        Assert.Empty(result);
    }

    [Fact]
    public void Suppresses_reauth_reimport_same_content_new_id()
    {
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("new-id", key) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int> { [key] = 1 });
        Assert.Empty(result); // budget = max(0, 1 - 1) = 0
    }

    [Fact]
    public void Keeps_genuine_identical_same_day_charges()
    {
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("a", key), new("b", key) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int>());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Different_timestamp_is_not_a_duplicate()
    {
        var stored = new Key(Noon, 5.00m, "Coffee");
        var later = new Key(Noon.AddHours(3), 5.00m, "Coffee");
        var incoming = new List<Item> { new("later", later) };
        var result = Select(incoming, new HashSet<string>(), new Dictionary<Key, int> { [stored] = 1 });
        Assert.Single(result); // different key entirely
    }

    [Fact]
    public void Inserts_only_the_genuinely_new_one_when_some_overlap()
    {
        var key = new Key(Noon, 2.40m, "MBTA");
        var incoming = new List<Item> { new("x", key), new("y", key) };
        var result = Select(incoming, new HashSet<string> { "x" }, new Dictionary<Key, int> { [key] = 1 });
        Assert.Single(result);
        Assert.Equal("y", result[0].Id); // budget = max(0, 2 - 1) = 1; "x" skipped by id, "y" inserted
    }
}
