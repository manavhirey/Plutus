namespace Plutus.Core.Categorization;

/// <summary>Bound from the <c>Plutus:Claude</c> configuration section.</summary>
public sealed class ClaudeOptions
{
    public const string SectionName = "Plutus:Claude";

    /// <summary>
    /// Model used for categorization. Default is the most capable model;
    /// <c>claude-haiku-4-5</c> is the low-cost option for this high-volume task.
    /// </summary>
    public string Model { get; set; } = "claude-opus-4-8";
}
