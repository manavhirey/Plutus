namespace Plutus.Core.SimpleFin;

public interface ISimpleFinClient
{
    /// <summary>
    /// Performs the one-time SimpleFIN handshake: Base64-decodes the setup token to a
    /// claim URL, POSTs to it, and returns the long-lived access URL
    /// (form: https://user:pass@bridge.simplefin.org/simplefin). The setup token is single-use.
    /// </summary>
    Task<string> ClaimAsync(string setupToken, CancellationToken ct = default);

    /// <summary>
    /// Fetches accounts and their transactions for the given window. Basic-auth
    /// credentials are taken from the access URL's userinfo and applied per request.
    /// </summary>
    Task<SimpleFinAccountSet> GetAccountsAsync(
        string accessUrl,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default);
}
