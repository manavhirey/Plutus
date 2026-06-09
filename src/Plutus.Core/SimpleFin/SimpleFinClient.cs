using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Plutus.Core.SimpleFin;

/// <summary>
/// Typed <see cref="HttpClient"/> over the SimpleFIN Bridge. Registered via
/// IHttpClientFactory with a standard resilience handler (retry + circuit breaker).
/// </summary>
public sealed class SimpleFinClient(HttpClient http, ILogger<SimpleFinClient> logger) : ISimpleFinClient
{
    public async Task<string> ClaimAsync(string setupToken, CancellationToken ct = default)
    {
        string claimUrl;
        try
        {
            claimUrl = Encoding.UTF8.GetString(Convert.FromBase64String(setupToken.Trim()));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The SimpleFIN setup token is not valid Base64.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, claimUrl)
        {
            Content = new ByteArrayContent([]),
        };
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var accessUrl = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (string.IsNullOrWhiteSpace(accessUrl))
        {
            throw new InvalidOperationException("SimpleFIN claim returned an empty access URL.");
        }

        logger.LogInformation("SimpleFIN connection claimed successfully.");
        return accessUrl;
    }

    public async Task<SimpleFinAccountSet> GetAccountsAsync(
        string accessUrl,
        DateTimeOffset start,
        DateTimeOffset end,
        bool includePending = false,
        CancellationToken ct = default)
    {
        var (baseUri, authHeader) = SplitAccessUrl(accessUrl);

        var requestUri =
            $"{baseUri}/accounts?start-date={start.ToUnixTimeSeconds()}&end-date={end.ToUnixTimeSeconds()}";
        if (includePending)
        {
            requestUri += "&pending=1";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = authHeader;

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SimpleFinAccountSet>(ct)
            ?? throw new InvalidOperationException("SimpleFIN returned an empty accounts response.");

        if (result.Errors is { Count: > 0 } errors)
        {
            logger.LogWarning("SimpleFIN reported errors: {Errors}", string.Join("; ", errors));
        }

        return result;
    }

    /// <summary>
    /// Splits an access URL into its credential-free base (scheme://host[:port]/path)
    /// and a Basic auth header built from the userinfo component.
    /// </summary>
    private static (string BaseUri, AuthenticationHeaderValue Auth) SplitAccessUrl(string accessUrl)
    {
        var uri = new Uri(accessUrl);
        if (string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("The SimpleFIN access URL is missing its credentials.");
        }

        var portSegment = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var baseUri = $"{uri.Scheme}://{uri.Host}{portSegment}{uri.AbsolutePath}".TrimEnd('/');

        // userinfo is percent-encoded "user:pass"; decode each half before re-encoding for Basic auth.
        var separator = uri.UserInfo.IndexOf(':');
        var user = separator >= 0 ? uri.UserInfo[..separator] : uri.UserInfo;
        var pass = separator >= 0 ? uri.UserInfo[(separator + 1)..] : string.Empty;
        var credentials = $"{Uri.UnescapeDataString(user)}:{Uri.UnescapeDataString(pass)}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

        return (baseUri, new AuthenticationHeaderValue("Basic", encoded));
    }
}
