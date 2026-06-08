using Microsoft.EntityFrameworkCore;
using Plutus.Core.Abstractions;
using Plutus.Core.Categorization;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.SimpleFin;

namespace Plutus.Core.Tests;

/// <summary>Hands out fresh contexts over the same (shared in-memory) options, like production.</summary>
internal sealed class TestDbContextFactory(DbContextOptions<PlutusDbContext> options)
    : IDbContextFactory<PlutusDbContext>
{
    public PlutusDbContext CreateDbContext() => new(options);
}

/// <summary>Returns a canned account set (or throws) without any network I/O.</summary>
internal sealed class FakeSimpleFinClient(SimpleFinAccountSet? result = null, Exception? throws = null) : ISimpleFinClient
{
    public string? LastAccessUrl { get; private set; }

    public Task<string> ClaimAsync(string setupToken, CancellationToken ct = default) =>
        Task.FromResult("https://user:pass@bridge.simplefin.org/simplefin");

    public Task<SimpleFinAccountSet> GetAccountsAsync(string accessUrl, DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        LastAccessUrl = accessUrl;
        if (throws is not null)
        {
            throw throws;
        }

        return Task.FromResult(result ?? new SimpleFinAccountSet([], null));
    }
}

/// <summary>Always returns the same category with a fixed confidence.</summary>
internal sealed class FakeCategorizer(string category, double confidence = 0.9) : ICategorizer
{
    public int Calls { get; private set; }

    public Task<CategorizationResult?> CategorizeAsync(string description, string? note, IReadOnlyList<Category> categories, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<CategorizationResult?>(new CategorizationResult(category, confidence));
    }
}

/// <summary>Identity protector — keeps tests free of Data Protection setup.</summary>
internal sealed class PassthroughProtector : IConnectionProtector
{
    public string Protect(string plaintext) => plaintext;

    public string Unprotect(string ciphertext) => ciphertext;
}

/// <summary>A TimeProvider pinned to a fixed instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
