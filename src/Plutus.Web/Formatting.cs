namespace Plutus.Web;

/// <summary>Display helpers for money. v1 assumes a single-currency ($) presentation.</summary>
public static class Money
{
    /// <summary>An expense (stored as a positive magnitude) rendered as an outflow.</summary>
    public static string Expense(decimal value) => $"−${value:N2}";

    /// <summary>A plain signed/positive amount (e.g. account balance).</summary>
    public static string Plain(decimal value) => $"${value:N2}";
}
