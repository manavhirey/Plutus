using Plutus.Core.Models;

namespace Plutus.Core.Sync;

public interface ISyncService
{
    /// <summary>
    /// Runs one sync: pulls the configured window from SimpleFIN, upserts accounts,
    /// inserts new expenses (credits filtered, deduped), categorizes them, and records a
    /// <see cref="SyncRun"/>. Returns the recorded run, or <c>null</c> if no SimpleFIN
    /// connection is configured yet.
    /// </summary>
    Task<SyncRun?> RunAsync(CancellationToken ct = default);
}
