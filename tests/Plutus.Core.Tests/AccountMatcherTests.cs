using Plutus.Core.Models;
using Plutus.Core.Sync;

namespace Plutus.Core.Tests;

public sealed class AccountMatcherTests
{
    private static Account Acct(int id, string sfId, string name) =>
        new() { Id = id, SimpleFinAccountId = sfId, Name = name, Currency = "USD" };

    [Fact]
    public void Matches_by_simplefin_id_first()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        var match = AccountMatcher.FindExisting("ACT-old", "Some Other Name", existing);
        Assert.Same(existing[0], match);
    }

    [Fact]
    public void Matches_by_name_when_id_not_found()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        var match = AccountMatcher.FindExisting("ACT-new", "chase freedom unlimited (7463)", existing); // diff case
        Assert.Same(existing[0], match);
    }

    [Fact]
    public void Id_match_wins_over_name_match()
    {
        var byName = Acct(1, "ACT-a", "Savor (7496)");
        var byId = Acct(2, "ACT-b", "Different");
        var match = AccountMatcher.FindExisting("ACT-b", "Savor (7496)", new[] { byName, byId });
        Assert.Same(byId, match);
    }

    [Fact]
    public void Returns_null_when_neither_matches()
    {
        var existing = new[] { Acct(1, "ACT-old", "Chase Freedom Unlimited (7463)") };
        Assert.Null(AccountMatcher.FindExisting("ACT-new", "Brand New Account (9999)", existing));
    }
}
