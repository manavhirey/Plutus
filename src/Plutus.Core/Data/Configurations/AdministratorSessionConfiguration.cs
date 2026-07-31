using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class AdministratorSessionConfiguration : IEntityTypeConfiguration<AdministratorSession>
{
    public void Configure(EntityTypeBuilder<AdministratorSession> builder)
    {
        builder.Property(session => session.PasswordHashFingerprint)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(session => new { session.ExpiresAt, session.RevokedAt });
    }
}
