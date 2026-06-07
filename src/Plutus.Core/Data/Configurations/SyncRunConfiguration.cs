using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        // Store the enum as text for a readable audit trail.
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);

        // Catch-up-on-startup queries the most recent successful run by date.
        builder.HasIndex(s => s.RanAt);
    }
}
