using Plutus.Core.Models;

namespace Plutus.Core.SimpleFin;

public interface ISimpleFinConnectionService
{
    /// <summary>Claims a setup token, encrypts the resulting access URL, and stores the single connection row.</summary>
    Task ConnectAsync(string setupToken, CancellationToken ct = default);

    /// <summary>Returns the stored connection (with encrypted access URL), or <c>null</c> if not configured.</summary>
    Task<SimpleFinConnection?> GetConnectionAsync(CancellationToken ct = default);
}
