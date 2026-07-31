namespace Plutus.Web;

/// <summary>Legacy display helpers used outside the dashboard presentation.</summary>
public static class Money
{
    /// <summary>An expense (stored as a positive magnitude) rendered as an outflow.</summary>
    public static string Expense(decimal value) => $"−${value:N2}";

    /// <summary>A plain signed/positive amount (e.g. account balance).</summary>
    public static string Plain(decimal value) => $"${value:N2}";
}

/// <summary>
/// Currency-safe presentation for dashboard values. Amounts always use an ISO code
/// suffix so a value is never visually mistaken for a different currency.
/// </summary>
public static class CurrencyAmountFormatter
{
    // Current ISO 4217 List One (Currency & Funds), published 2026-01-01 by
    // SIX, the ISO 4217 Maintenance Agency:
    // https://www.six-group.com/dam/download/financial-information/data-center/iso-currrency/lists/list-one.xml
    // This pinned application snapshot is deliberately offline at runtime. Update it
    // from that source whenever SIX publishes a new List One; it includes ZWG/XCG
    // and excludes historic HRK/ZWL.
    private static readonly HashSet<string> Iso4217Codes = new(StringComparer.Ordinal)
    {
        "AED", "AFN", "ALL", "AMD", "AOA", "ARS", "AUD", "AWG", "AZN", "BAM",
        "BBD", "BDT", "BHD", "BIF", "BMD", "BND", "BOB", "BOV", "BRL", "BSD",
        "BTN", "BWP", "BYN", "BZD", "CAD", "CDF", "CHE", "CHF", "CHW", "CLF",
        "CLP", "CNY", "COP", "COU", "CRC", "CUP", "CVE", "CZK", "DJF", "DKK",
        "DOP", "DZD", "EGP", "ERN", "ETB", "EUR", "FJD", "FKP", "GBP", "GEL",
        "GHS", "GIP", "GMD", "GNF", "GTQ", "GYD", "HKD", "HNL", "HTG", "HUF",
        "IDR", "ILS", "INR", "IQD", "IRR", "ISK", "JMD", "JOD", "JPY", "KES",
        "KGS", "KHR", "KMF", "KPW", "KRW", "KWD", "KYD", "KZT", "LAK", "LBP",
        "LKR", "LRD", "LSL", "LYD", "MAD", "MDL", "MGA", "MKD", "MMK", "MNT",
        "MOP", "MRU", "MUR", "MVR", "MWK", "MXN", "MXV", "MYR", "MZN", "NAD",
        "NGN", "NIO", "NOK", "NPR", "NZD", "OMR", "PAB", "PEN", "PGK", "PHP",
        "PKR", "PLN", "PYG", "QAR", "RON", "RSD", "RUB", "RWF", "SAR", "SBD",
        "SCR", "SDG", "SEK", "SGD", "SHP", "SLE", "SOS", "SRD", "SSP", "STN",
        "SVC", "SYP", "SZL", "THB", "TJS", "TMT", "TND", "TOP", "TRY", "TTD",
        "TWD", "TZS", "UAH", "UGX", "USD", "USN", "UYI", "UYU", "UYW", "UZS",
        "VED", "VES", "VND", "VUV", "WST", "XAD", "XAF", "XAG", "XAU", "XBA",
        "XBB", "XBC", "XBD", "XCD", "XCG", "XDR", "XOF", "XPD", "XPF", "XPT",
        "XSU", "XTS", "XUA", "XXX", "YER", "ZAR", "ZMW", "ZWG",
    };

    public static bool IsValidIso4217Code(string? currency) =>
        currency is not null && Iso4217Codes.Contains(currency);

    public static string Format(decimal amount, string? currency) =>
        $"{amount:N2} {DisplayCode(currency)}";

    public static string FormatExpense(decimal amount, string? currency) =>
        $"−{Format(amount, currency)}";

    private static string DisplayCode(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? "Unknown" : currency;
}
