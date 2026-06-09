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

    /// <summary>
    /// Names of unsynced credit cards whose bill payments should be treated as transfers
    /// (excluded from spending), even though Plutus doesn't sync the card itself. The synced-card
    /// transfer rule can't catch these because there's no matching account. Matched as a
    /// case-insensitive substring of the payment description (e.g. "BILT" → "BILT CARD PMT ...").
    /// </summary>
    public string[] ExternalCardPayees { get; set; } = ["BILT"];

    /// <summary>
    /// An account is shown as "stale" on the dashboard when its SimpleFIN balance-date is
    /// older than this many hours. Defaults to 24h, matching the daily sync cadence.
    /// </summary>
    public int StaleAfterHours { get; set; } = 24;
}
