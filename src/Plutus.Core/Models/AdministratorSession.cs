namespace Plutus.Core.Models;

/// <summary>
/// Server-side validity record for a single administrator browser session. The
/// browser cookie contains only this random identifier and a non-secret password
/// hash fingerprint; no password material is persisted here.
/// </summary>
public class AdministratorSession
{
    public Guid Id { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string PasswordHashFingerprint { get; set; } = "";
}
