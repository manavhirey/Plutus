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

        Assert.False(format.Schema["additionalProperties"].GetBoolean());
    }
}
