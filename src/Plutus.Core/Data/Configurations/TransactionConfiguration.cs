using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasIndex(t => t.SimpleFinTransactionId).IsUnique();
        builder.Property(t => t.SimpleFinTransactionId).IsRequired();
        builder.Property(t => t.Description).IsRequired();

        // Indexes for the two hot reads: the review queue and the day-grouped history.
        builder.HasIndex(t => t.IsReviewed);
        builder.HasIndex(t => t.PostedDate);

        builder.HasOne(t => t.Category)
            .WithMany(c => c.Transactions)
            .HasForeignKey(t => t.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
