namespace Plutus.Core.Models;

/// <summary>
/// The single SimpleFIN Bridge connection for this self-hosted instance.
/// Only one row is expected. <see cref="AccessUrl"/> is encrypted at rest via
/// ASP.NET Data Protection before it is written to the database.
/// </summary>
public class SimpleFinConnection
{
    public int Id { get; set; }

    /// <summary>
    /// The SimpleFIN access URL (form: https://user:pass@bridge.simplefin.org/simplefin).
    /// Stored encrypted; never logged.
    /// </summary>
    public required string AccessUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSyncedAt { get; set; }
}
