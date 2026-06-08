using System.Text.RegularExpressions;

namespace Plutus.Core.Transfers;

/// <summary>A synced account, reduced to what transfer detection needs.</summary>
public sealed record SyncedAccountRef(int Id, string Name, string? Org);

/// <summary>
/// Deterministically decides whether a transaction is a payment to one of the user's
/// *synced* accounts (i.e. a transfer that should not count as spending). A payment to an
/// unsynced card (e.g. BILT) matches nothing here and stays counted. Heuristic and
/// user-overridable: detected transfers remain visible/editable in the UI.
/// </summary>
public static class TransferDetector
{
    // Ordered longest-first so "ACH PMT"/"E-PAYMENT" win before the shorter substrings.
    private static readonly string[] PaymentMarkers =
        ["ACH PMT", "E-PAYMENT", "EPAYMENT", "AUTOPAY", "PAYMENT", "EPAY", "PMT"];

    private static readonly Regex Last4 = new(@"\((\d{4})\)", RegexOptions.Compiled);

    public static bool IsTransferPayment(string description, int sourceAccountId, IReadOnlyList<SyncedAccountRef> accounts)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var upper = description.ToUpperInvariant();
        if (!PaymentMarkers.Any(upper.Contains))
        {
            return false;
        }

        foreach (var account in accounts)
        {
            if (account.Id == sourceAccountId)
            {
                continue; // never treat a payment as a transfer to its own source account
            }

            var last4 = ExtractLast4(account.Name);
            if (last4 is not null && upper.Contains(last4))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(account.Org) && upper.Contains(account.Org.ToUpperInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the "(1234)" suffix banks put on account names; null if none.</summary>
    internal static string? ExtractLast4(string name)
    {
        var match = Last4.Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }
}
