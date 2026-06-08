using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

public sealed class SpendingInsightsTests
{
    [Fact]
    public void Top_returns_largest_category_and_share()
    {
        IReadOnlyList<CategorySpend> spend =
        [
            new(2, "Dining", "#EA580C", 60m),
            new(1, "Groceries", "#16A34A", 40m),
        ];

        var top = SpendingInsights.Top(spend);

        Assert.NotNull(top);
        Assert.Equal("Dining", top!.Name);
        Assert.Equal(60m, top.Total);
        Assert.Equal(0.6, top.Share, 3); // 60 / 100
    }

    [Fact]
    public void Top_is_null_for_empty_input()
    {
        Assert.Null(SpendingInsights.Top([]));
    }

    [Fact]
    public void Top_share_is_zero_when_total_is_zero()
    {
        IReadOnlyList<CategorySpend> spend = [new(1, "Groceries", "#16A34A", 0m)];
        var top = SpendingInsights.Top(spend);
        Assert.NotNull(top);
        Assert.Equal(0d, top!.Share);
    }
}
