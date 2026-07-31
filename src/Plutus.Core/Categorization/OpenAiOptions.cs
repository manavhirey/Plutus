namespace Plutus.Core.Categorization;

/// <summary>Bound from the <c>Plutus:OpenAI</c> configuration section.</summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "Plutus:OpenAI";

    /// <summary>Model used for categorization.</summary>
    public string Model { get; set; } = "gpt-5.6-luna";
}
