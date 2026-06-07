namespace Plutus.Core.Models;

public enum SyncStatus
{
    Success,
    Failed,
}

/// <summary>
/// Audit record for one sync attempt. Drives catch-up-on-startup (was there a
/// successful run today?) and the run history shown in Settings.
/// </summary>
public class SyncRun
{
    public int Id { get; set; }

    public DateTime RanAt { get; set; }

    public SyncStatus Status { get; set; }

    public int NewTransactionCount { get; set; }

    public string? Error { get; set; }
}
