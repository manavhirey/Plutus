namespace Plutus.Core.Sync;

/// <summary>Bound from the <c>Plutus:Sync</c> configuration section.</summary>
public sealed class SyncOptions
{
    public const string SectionName = "Plutus:Sync";

    /// <summary>How far back the first-ever sync reaches when there is no prior sync timestamp.</summary>
    public int LookBackDays { get; set; } = 30;

    /// <summary>
    /// Window overlap applied to subsequent syncs (re-fetch the last N days) so transactions
    /// that posted late aren't missed. Dedupe makes the overlap harmless.
    /// </summary>
    public int OverlapDays { get; set; } = 3;

    /// <summary>Local time of day for the daily scheduled run, "HH:mm".</summary>
    public string DailyTime { get; set; } = "06:00";
}
