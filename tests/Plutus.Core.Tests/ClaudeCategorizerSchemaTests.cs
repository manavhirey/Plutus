using System.Text.Json;
using Plutus.Core.Categorization;
using Plutus.Core.Models;

namespace Plutus.Core.Tests;

public sealed class ClaudeCategorizerSchemaTests
{
    [Fact]
    public void BuildSchema_constrains_category_to_the_supplied_names()
    {
        var categories = new List<Category>
        {
            new() { Id = 1, Name = "Groceries" },
            new() { Id = 2, Name = "Dining" },
            new() { Id = 3, Name = "Transport" },
        };

        var format = ClaudeCategorizer.BuildSchema(categories);

        var properties = format.Schema["properties"];
        var enumValues = properties.GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "Groceries", "Dining", "Transport" }, enumValues);
        Assert.Equal("string", properties.GetProperty("category").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("confidence").GetProperty("type").GetString());

        var required = format.Schema["required"].EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("category", required);
        Assert.Contains("confidence", required);

        Assert.Equal("string", properties.GetProperty("note").GetProperty("type").GetString());
        Assert.Contains("note", required);

        Assert.False(format.Schema["additionalProperties"].GetBoolean());
    }

    [Fact]
    public void NormalizeNote_returns_null_for_null_empty_and_whitespace()
    {
        Assert.Null(ClaudeCategorizer.NormalizeNote(null));
        Assert.Null(ClaudeCategorizer.NormalizeNote(""));
        Assert.Null(ClaudeCategorizer.NormalizeNote("   "));
    }

    [Fact]
    public void NormalizeNote_trims_and_preserves_inner_content()
    {
        Assert.Equal("Coffee", ClaudeCategorizer.NormalizeNote("  Coffee  "));
        Assert.Equal("a b", ClaudeCategorizer.NormalizeNote("a b"));
    }
}
