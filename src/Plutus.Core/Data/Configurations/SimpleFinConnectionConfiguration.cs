using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class SimpleFinConnectionConfiguration : IEntityTypeConfiguration<SimpleFinConnection>
{
    public void Configure(EntityTypeBuilder<SimpleFinConnection> builder)
    {
        builder.Property(c => c.AccessUrl).IsRequired();
    }
}
