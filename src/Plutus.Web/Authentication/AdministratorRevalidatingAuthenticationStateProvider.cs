using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Plutus.Web.Authentication;

/// <summary>
/// Rechecks the durable session record while an InteractiveServer circuit is
/// connected. A failed check changes the circuit to anonymous, allowing the
/// session watcher to force a fresh login and terminate the live circuit.
/// </summary>
public sealed class AdministratorRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    AdministratorAuthenticationState authenticationState)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(10);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState state,
        CancellationToken cancellationToken)
    {
        if (!PlutusAuthentication.TryGetSessionId(state.User, out var sessionId) ||
            !PlutusAuthentication.HasExpectedFingerprint(state.User, authenticationState.PasswordHashFingerprint))
        {
            return false;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
            return await sessions.IsValidAsync(sessionId, authenticationState.PasswordHashFingerprint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
