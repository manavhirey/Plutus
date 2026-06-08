using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Plutus.Core.Models;
using Category = Plutus.Core.Models.Category;

namespace Plutus.Core.Categorization;

/// <summary>
/// Categorizes transactions with the Claude API using structured outputs. The schema's
/// <c>category</c> field is an enum built from the current category names, so the model
/// can only return an existing category (no free-text drift).
/// </summary>
public sealed class ClaudeCategorizer(
    AnthropicClient client,
    IOptions<ClaudeOptions> options,
    ILogger<ClaudeCategorizer> logger) : ICategorizer
{
    private const string SystemPrompt =
        "You are a personal-finance assistant that classifies a single bank transaction into " +
        "exactly one spending category. Choose the closest fit from the allowed categories. " +
        "When a user note is provided, weight it heavily — it describes what the purchase actually was. " +
        "Set confidence to your probability (0–1) that the category is correct. " +
        "Also produce a 'note': a concise 3–8 word plain-English description decoded from the bank description " +
        "(e.g. 'AMZN MKTP US*2K4...' → 'Amazon Marketplace purchase'). " +
        "If the description is already clear, lightly normalize it. " +
        "Do NOT invent context that cannot be inferred — no guessed people, occasions, or specific amounts. " +
        "If a user note is provided, use it to inform the category but still base the note on the bank description.";

    public async Task<CategorizationResult?> CategorizeAsync(
        string description,
        string? note,
        IReadOnlyList<Category> categories,
        CancellationToken ct = default)
    {
        if (categories.Count == 0)
        {
            return null;
        }

        try
        {
            var parameters = new MessageCreateParams
            {
                Model = options.Value.Model,
                MaxTokens = 256,
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig
                {
                    Effort = Effort.Low,
                    Format = BuildSchema(categories),
                },
                System = SystemPrompt,
                Messages = [new() { Role = Role.User, Content = BuildUserContent(description, note) }],
            };

            var response = await client.Messages.Create(parameters);

            var json = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(json))
            {
                logger.LogWarning("Claude categorization returned no text content.");
                return null;
            }

            var parsed = JsonSerializer.Deserialize<CategorizationJson>(json);
            if (parsed is null)
            {
                return null;
            }

            // Enum-constrained, but match case-insensitively against the real list to be safe.
            var match = categories.FirstOrDefault(
                c => string.Equals(c.Name, parsed.Category, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                logger.LogWarning("Claude returned unknown category '{Category}'.", parsed.Category);
                return null;
            }

            var modelNote = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note.Trim();
            return new CategorizationResult(match.Name, modelNote, parsed.Confidence);
        }
        catch (Exception ex)
        {
            // Never let a categorization failure break a sync — leave the transaction uncategorized.
            logger.LogWarning(ex, "Claude categorization failed for description '{Description}'.", description);
            return null;
        }
    }

    private static string BuildUserContent(string description, string? note)
    {
        var content = $"Bank description: {description}";
        if (!string.IsNullOrWhiteSpace(note))
        {
            content += $"\nUser note: {note}";
        }

        return content;
    }

    internal static JsonOutputFormat BuildSchema(IReadOnlyList<Category> categories)
    {
        var names = categories.Select(c => c.Name).ToArray();

        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new
            {
                category = new { type = "string", @enum = names },
                note = new { type = "string" },
                confidence = new { type = "number" },
            }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "category", "note", "confidence" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };

        return new JsonOutputFormat { Schema = schema };
    }

    private sealed record CategorizationJson(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("note")] string? Note,
        [property: JsonPropertyName("confidence")] double Confidence);
}
