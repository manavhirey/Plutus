using Plutus.Core.Transfers;

namespace Plutus.Core.Tests;

public sealed class TransferDetectorTests
{
    // Source = Chase College (checking). The others are synced cards.
    private static readonly IReadOnlyList<SyncedAccountRef> Accounts =
    [
        new(1, "CHASE COLLEGE (0670)", "Chase Bank"),            // source (checking)
        new(2, "Chase Freedom Unlimited (7463)", "Chase Bank"),
        new(3, "Chase Sapphire Preferred (7795)", "Chase Bank"),
        new(4, "Blue Cash Everyday® (2006)", "American Express"),
        new(5, "Savor (7496)", "Capital One"),
    ];

    private const int Source = 1;

    [Theory]
    [InlineData("Payment to Chase card ending in 7463 06/04")] // last-4 match -> Freedom
    [InlineData("Payment to Chase card ending in 7795 06/04")] // last-4 match -> Sapphire
    [InlineData("AMERICAN EXPRESS ACH PMT M0828 WEB ID: XXXXXX2111")] // issuer org match
    [InlineData("CAPITAL ONE MOBILE PMT CA084881AF448DE WEB ID: XXXXXX4380")] // issuer org match
    public void Flags_payments_to_synced_cards(string description)
    {
        Assert.True(TransferDetector.IsTransferPayment(description, Source, Accounts));
    }

    [Theory]
    [InlineData("BILT CARD PMT PPD ID: 1844372402")] // BILT not synced -> keep
    [InlineData("Online Payment 29066523695 To Fensdale Property Trust 05/22")] // rent -> keep
    [InlineData("TRADER JOE'S #123 GROCERIES")] // normal purchase, no payment marker
    public void Does_not_flag_non_card_payments(string description)
    {
        Assert.False(TransferDetector.IsTransferPayment(description, Source, Accounts));
    }

    [Fact]
    public void Does_not_match_the_source_account()
    {
        // Description references the source account's own last-4; must not self-match.
        Assert.False(TransferDetector.IsTransferPayment("AUTOPAY 0670", Source, Accounts));
    }

    [Fact]
    public void Empty_description_is_not_a_transfer()
    {
        Assert.False(TransferDetector.IsTransferPayment("", Source, Accounts));
    }
}
