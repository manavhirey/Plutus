using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Plutus.Core.Data.Converters;

/// <summary>
/// Ensures every <see cref="DateTime"/> is persisted as UTC and re-materialized
/// with <see cref="DateTimeKind.Utc"/>, so values round-trip unambiguously.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
