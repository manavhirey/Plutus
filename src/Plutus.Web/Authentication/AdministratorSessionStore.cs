using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data;
using Plutus.Core.Models;

namespace Plutus.Web.Authentication;

/// <summary>
/// Persists server-side session validity so cookie deletion alone is not the
/// security boundary. This lets logout, expiry, and password rotation invalidate
/// every browser tab that presents the same session ticket.
/// </summary>
public sealed class AdministratorSessionStore(
    IDbContextFactory<PlutusDbContext> dbFactory,
    TimeProvider timeProvider)
{
    public async Task<AdministratorSession> CreateAsync(
        string passwordHashFingerprint,
        DateTime expiresAt,
        CancellationToken cancellationToken = default)
    {
        var session = new AdministratorSession
        {
            Id = Guid.NewGuid(),
            IssuedAt = timeProvider.GetUtcNow().UtcDateTime,
            ExpiresAt = expiresAt,
            PasswordHashFingerprint = passwordHashFingerprint,
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AdministratorSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<bool> IsValidAsync(
        Guid sessionId,
        string passwordHashFingerprint,
        CancellationToken cancellationToken = default)
    {
        return await GetActiveAsync(sessionId, passwordHashFingerprint, cancellationToken) is not null;
    }

    public async Task<AdministratorSession?> GetActiveAsync(
        Guid sessionId,
        string passwordHashFingerprint,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AdministratorSessions.AsNoTracking().SingleOrDefaultAsync(
            session => session.Id == sessionId &&
                       session.RevokedAt == null &&
                       session.ExpiresAt > now &&
                       session.PasswordHashFingerprint == passwordHashFingerprint,
            cancellationToken);
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AdministratorSessions
            .Where(session => session.Id == sessionId && session.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);
    }

    /// <summary>
    /// Makes a configured password-hash rotation durable. This runs before the
    /// application exposes endpoints, so rolling back to a previously used hash
    /// cannot resurrect any session invalidated by an intervening rotation.
    /// </summary>
    public async Task RevokeActiveSessionsWithDifferentFingerprintAsync(
        string passwordHashFingerprint,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.AdministratorSessions
            .Where(session => session.RevokedAt == null &&
                              session.ExpiresAt > now &&
                              session.PasswordHashFingerprint != passwordHashFingerprint)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAt, now), cancellationToken);
    }
}
