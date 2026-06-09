namespace Plutus.Core.Reporting;

/// <summary>
/// Pure helpers for presenting how fresh an account's data is, based on its SimpleFIN
/// <c>BalanceDate</c> (the bridge's last-refresh stamp). The caller supplies <c>now</c> so
/// the logic is deterministic and unit-testable. <c>balanceDateUtc</c> is stored UTC.
/// </summary>
public static class AccountFreshness
{
    /// <summary>True when the account hasn't refreshed within <paramref name="threshold"/>.</summary>
    public static bool IsStale(DateTime balanceDateUtc, DateTimeOffset now, TimeSpan threshold) =>
        Age(balanceDateUtc, now) > threshold;

    /// <summary>Coarse relative-time label for a glance, e.g. "just now", "5m ago", "2 days ago".</summary>
    public static string Describe(DateTime balanceDateUtc, DateTimeOffset now)
    {
        var age = Age(balanceDateUtc, now);

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        var days = (int)age.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    // Age is clamped at zero so a future BalanceDate (clock skew) reads as "just now", not stale.
    private static TimeSpan Age(DateTime balanceDateUtc, DateTimeOffset now)
    {
        var age = now.UtcDateTime - DateTime.SpecifyKind(balanceDateUtc, DateTimeKind.Utc);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
