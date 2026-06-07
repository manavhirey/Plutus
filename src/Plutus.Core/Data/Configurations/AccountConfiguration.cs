using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.HasIndex(a => a.SimpleFinAccountId).IsUnique();
        builder.Property(a => a.SimpleFinAccountId).IsRequired();
        builder.Property(a => a.Name).IsRequired();
        builder.Property(a => a.Currency).IsRequired().HasMaxLength(8);

        builder.HasMany(a => a.Transactions)
            .WithOne(t => t.Account!)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
