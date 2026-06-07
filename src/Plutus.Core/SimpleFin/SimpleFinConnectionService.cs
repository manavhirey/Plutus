using Microsoft.EntityFrameworkCore;
using Plutus.Core.Abstractions;
using Plutus.Core.Data;
using Plutus.Core.Models;

namespace Plutus.Core.SimpleFin;

public sealed class SimpleFinConnectionService(
    PlutusDbContext db,
    ISimpleFinClient client,
    IConnectionProtector protector,
    TimeProvider timeProvider) : ISimpleFinConnectionService
{
    public async Task ConnectAsync(string setupToken, CancellationToken ct = default)
    {
        var accessUrl = await client.ClaimAsync(setupToken, ct);
        var encrypted = protector.Protect(accessUrl);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var existing = await db.SimpleFinConnections.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            db.SimpleFinConnections.Add(new SimpleFinConnection { AccessUrl = encrypted, CreatedAt = now });
        }
        else
        {
            // Re-connecting replaces the credential and resets the sync watermark.
            existing.AccessUrl = encrypted;
            existing.CreatedAt = now;
            existing.LastSyncedAt = null;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<SimpleFinConnection?> GetConnectionAsync(CancellationToken ct = default) =>
        db.SimpleFinConnections.FirstOrDefaultAsync(ct);
}
