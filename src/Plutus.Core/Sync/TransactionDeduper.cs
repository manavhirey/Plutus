namespace Plutus.Core.Sync;

/// <summary>
/// Decides which incoming transactions to insert for a single account. Two tiers: skip any whose
/// SimpleFIN id is already stored (fast path), then for each identical content key — posted
/// timestamp + amount + description — insert incoming rows only beyond the count already stored.
/// This suppresses a re-authentication re-import (same content, new id) while preserving genuine
/// identical same-day charges (keeps as many as the bridge reports).
/// </summary>
public static class TransactionDeduper
{
    public readonly record struct ContentKey(DateTime Posted, decimal Amount, string Description);

    public static List<T> SelectToInsert<T>(
        IReadOnlyList<T> incoming,
        Func<T, string> idSelector,
        Func<T, ContentKey> keySelector,
        ISet<string> existingIds,
        IDictionary<ContentKey, int> existingKeyCounts)
    {
        // How many the bridge reports per content key in this payload.
        var incomingCounts = new Dictionary<ContentKey, int>();
        foreach (var item in incoming)
        {
            var key = keySelector(item);
            incomingCounts[key] = incomingCounts.GetValueOrDefault(key) + 1;
        }

        // Budget per key = how many MORE we may insert = max(0, reported - alreadyStored).
        var budget = new Dictionary<ContentKey, int>();
        foreach (var (key, count) in incomingCounts)
        {
            existingKeyCounts.TryGetValue(key, out var stored);
            budget[key] = Math.Max(0, count - stored);
        }

        var result = new List<T>();
        foreach (var item in incoming)
        {
            if (existingIds.Contains(idSelector(item)))
            {
                continue; // exact same transaction already stored
            }

            var key = keySelector(item);
            if (budget.GetValueOrDefault(key) > 0)
            {
                result.Add(item);
                budget[key] -= 1;
            }
        }

        return result;
    }
}
