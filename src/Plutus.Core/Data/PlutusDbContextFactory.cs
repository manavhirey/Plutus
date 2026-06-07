using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plutus.Core.Data;

/// <summary>
/// Design-time factory used only by the EF Core CLI tools (migrations / scaffolding).
/// At runtime the context is configured through DI in Plutus.Web. The connection
/// string here is a throwaway used to build the model; no database is touched.
/// </summary>
public sealed class PlutusDbContextFactory : IDesignTimeDbContextFactory<PlutusDbContext>
{
    public PlutusDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlutusDbContext>()
            .UseSqlite("Data Source=plutus-design.db")
            .Options;

        return new PlutusDbContext(options);
    }
}
