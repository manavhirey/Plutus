using System.Text.Json.Serialization;

namespace Plutus.Core.SimpleFin;

/// <summary>Top-level response from <c>GET {accessUrl}/accounts</c>.</summary>
public sealed record SimpleFinAccountSet(
    [property: JsonPropertyName("accounts")] IReadOnlyList<SimpleFinAccount> Accounts,
    [property: JsonPropertyName("errors")] IReadOnlyList<string>? Errors);

public sealed record SimpleFinAccount(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("currency")] string Currency,
    // SimpleFIN sends money amounts as decimal strings to avoid float drift.
    [property: JsonPropertyName("balance")] string Balance,
    // Unix epoch seconds.
    [property: JsonPropertyName("balance-date")] long BalanceDate,
    [property: JsonPropertyName("org")] SimpleFinOrg? Org,
    [property: JsonPropertyName("transactions")] IReadOnlyList<SimpleFinTransaction>? Transactions);

public sealed record SimpleFinOrg(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("domain")] string? Domain);

public sealed record SimpleFinTransaction(
    [property: JsonPropertyName("id")] string Id,
    // Unix epoch seconds.
    [property: JsonPropertyName("posted")] long Posted,
    // Signed decimal string: negative = money out (expense), positive = money in (credit).
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("description")] string Description);
