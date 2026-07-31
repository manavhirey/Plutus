using Microsoft.AspNetCore.Components.Authorization;

namespace Plutus.Web.Authentication;

/// <summary>
/// Gate used by state-changing InteractiveServer handlers. Circuit revalidation
/// removes invalid UI promptly; this guard also makes each mutation fail closed
/// in the short interval before that revalidation runs.
/// </summary>
public sealed class AdministratorSessionGuard(
    AuthenticationStateProvider authenticationStateProvider,
    AdministratorSessionStore sessions,
    AdministratorAuthenticationState authenticationState,
    AdministratorSessionOperationCoordinator coordinator)
{
    public async Task<AdministratorSessionOperationCoordinator.AdministratorSessionOperationLease?> AcquireLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var principal = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
            return PlutusAuthentication.TryGetSessionId(principal, out var sessionId) &&
                   PlutusAuthentication.HasExpectedFingerprint(principal, authenticationState.PasswordHashFingerprint)
                ? await coordinator.TryAcquireAsync(sessionId, authenticationState.PasswordHashFingerprint, sessions, cancellationToken)
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
