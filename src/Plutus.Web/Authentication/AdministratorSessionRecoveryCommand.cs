using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plutus.Core.Data;

namespace Plutus.Web.Authentication;

/// <summary>
/// Explicit, non-hosting recovery operation for invalidating sessions after a
/// database restore. It is intentionally a command, not a persistent setting.
/// </summary>
public static class AdministratorSessionRecoveryCommand
{
    public const string Command = "--revoke-all-sessions";

    public static bool IsMentioned(string[] args) => args.Any(argument =>
        string.Equals(argument, Command, StringComparison.Ordinal) ||
        argument.StartsWith($"{Command}=", StringComparison.Ordinal));

    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == Command;

    /// <summary>
    /// Executes the recovery operation without exposing provider details. The
    /// caller can use the return code directly as a process exit code.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RunAsync(services, output, cancellationToken);
            return 0;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Session recovery failed; Plutus was not started.");
            return 2;
        }
    }

    private static async Task RunAsync(
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);

        var sessions = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
        await sessions.RevokeAllSessionsAsync(cancellationToken);
        await output.WriteLineAsync("All Plutus administrator sessions were revoked. Start the authenticated service normally.");
    }
}
