using Plutus.Core.Models;

namespace Plutus.Core.Sync;

/// <summary>
/// Resolves an incoming SimpleFIN account to an existing <see cref="Account"/> row: by
/// <see cref="Account.SimpleFinAccountId"/> first (the stable case), then by exact
/// <see cref="Account.Name"/> (case-insensitive). The name fallback re-links an account whose
/// SimpleFIN id was re-minted by a bridge re-authentication, so it is updated in place instead
/// of being inserted as a duplicate.
/// </summary>
public static class AccountMatcher
{
    public static Account? FindExisting(string simpleFinAccountId, string name, IEnumerable<Account> existing)
    {
        Account? byName = null;
        foreach (var account in existing)
        {
            if (string.Equals(account.SimpleFinAccountId, simpleFinAccountId, StringComparison.Ordinal))
            {
                return account; // exact id match always wins
            }

            if (byName is null && string.Equals(account.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                byName = account;
            }
        }

        return byName;
    }
}
