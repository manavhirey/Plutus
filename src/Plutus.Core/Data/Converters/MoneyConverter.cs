using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Plutus.Core.Data.Converters;

/// <summary>
/// Stores <see cref="decimal"/> money values as invariant-culture TEXT in SQLite,
/// preserving full precision (SQLite has no native decimal type).
/// </summary>
public sealed class MoneyConverter : ValueConverter<decimal, string>
{
    public MoneyConverter()
        : base(
            v => v.ToString(CultureInfo.InvariantCulture),
            v => decimal.Parse(v, CultureInfo.InvariantCulture))
    {
    }
}
