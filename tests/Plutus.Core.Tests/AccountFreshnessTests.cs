using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

public sealed class AccountFreshnessTests
{
    // Fixed reference "now" so every case is deterministic.
    private static readonly DateTimeOffset Now = new(2026, 06, 09, 12, 00, 00, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(24);

    [Fact]
    public void Not_stale_just_under_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-23); // 23h old
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Fact]
    public void Not_stale_exactly_at_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-24); // exactly 24h old
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Fact]
    public void Stale_just_over_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-24).AddMinutes(-1); // 24h01m old
        Assert.True(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Theory]
    [InlineData(-30, "just now")]            // 30 seconds ago (value is seconds)
    public void Describe_seconds(int seconds, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddSeconds(seconds);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-5, "5m ago")]
    [InlineData(-45, "45m ago")]
    public void Describe_minutes(int minutes, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddMinutes(minutes);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-1, "1h ago")]
    [InlineData(-5, "5h ago")]
    public void Describe_hours(int hours, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddHours(hours);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-25, "1 day ago")]
    [InlineData(-50, "2 days ago")]
    public void Describe_days(int hours, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddHours(hours);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Fact]
    public void Future_balance_date_reads_just_now()
    {
        var balanceDate = Now.UtcDateTime.AddMinutes(5); // clock skew, in the future
        Assert.Equal("just now", AccountFreshness.Describe(balanceDate, Now));
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }
}
