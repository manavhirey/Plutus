using Microsoft.EntityFrameworkCore;
using Plutus.Core.Data.Converters;
using Plutus.Core.Models;

namespace Plutus.Core.Data;

public class PlutusDbContext(DbContextOptions<PlutusDbContext> options) : DbContext(options)
{
    public DbSet<SimpleFinConnection> SimpleFinConnections => Set<SimpleFinConnection>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlutusDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money: full-precision TEXT; DateTimes: always UTC. Applied globally so
        // every decimal/DateTime property (including nullable) is handled consistently.
        configurationBuilder.Properties<decimal>().HaveConversion<MoneyConverter>();
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }
}
