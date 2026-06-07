namespace Plutus.Core.Models;

/// <summary>
/// A bank account surfaced by SimpleFIN. Upserted by <see cref="SimpleFinAccountId"/> on each sync.
/// </summary>
public class Account
{
    public int Id { get; set; }

    /// <summary>The SimpleFIN account id; stable across syncs and used as the upsert key.</summary>
    public required string SimpleFinAccountId { get; set; }

    public required string Name { get; set; }

    public string? Org { get; set; }

    public required string Currency { get; set; }

    public decimal Balance { get; set; }

    public DateTime BalanceDate { get; set; }

    public List<Transaction> Transactions { get; } = [];
}
