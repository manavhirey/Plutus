using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Plutus.Core.Models;

namespace Plutus.Core.Data.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasIndex(c => c.Name).IsUnique();
        builder.Property(c => c.Name).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Color).HasMaxLength(16);

        builder.HasData(StarterCategories());
    }

    /// <summary>Built-in starter set. Editable in the UI after seeding.</summary>
    private static IEnumerable<Category> StarterCategories()
    {
        string[] names =
        [
            "Groceries", "Dining", "Transport", "Shopping", "Utilities",
            "Housing", "Health", "Entertainment", "Travel", "Subscriptions",
            "Fees", "Other",
        ];

        string[] colors =
        [
            "#16A34A", "#EA580C", "#0891B2", "#7C3AED", "#CA8A04",
            "#0D9488", "#DC2626", "#DB2777", "#2563EB", "#9333EA",
            "#64748B", "#6B7280",
        ];

        for (int i = 0; i < names.Length; i++)
        {
            yield return new Category
            {
                Id = i + 1,
                Name = names[i],
                Color = colors[i],
                IsSystem = true,
                SortOrder = i,
            };
        }

        yield return new Category
        {
            Id = names.Length + 1, // 13
            Name = "Transfer",
            Color = "#94A3B8", // muted slate
            IsSystem = true,
            ExcludeFromSpending = true,
            SortOrder = names.Length, // 12
        };
    }
}
