using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Plutus.Core.Abstractions;
using Plutus.Core.Categorization;
using Plutus.Core.Data;
using Plutus.Core.Security;
using Plutus.Core.SimpleFin;
using Plutus.Core.Sync;

namespace Plutus.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers everything Plutus.Core provides: the DbContext, options, the SimpleFIN
    /// typed client (with resilience), the Claude categorizer, and the sync services.
    /// </summary>
    public static IServiceCollection AddPlutusCore(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        // Factory for Blazor components (one context per operation, no cross-render sharing),
        // plus a scoped registration so Core services can take PlutusDbContext directly.
        services.AddDbContextFactory<PlutusDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PlutusDbContext>>().CreateDbContext());

        services.AddOptions<ClaudeOptions>().Bind(configuration.GetSection(ClaudeOptions.SectionName));
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        // API key comes from ANTHROPIC_API_KEY (env / user-secrets); never from config or DB.
        services.AddSingleton(_ => new AnthropicClient
        {
            ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        });

        services.AddHttpClient<ISimpleFinClient, SimpleFinClient>()
            .AddStandardResilienceHandler();

        services.AddSingleton<IConnectionProtector, DataProtectionConnectionProtector>();
        services.AddScoped<ICategorizer, ClaudeCategorizer>();
        services.AddScoped<ISimpleFinConnectionService, SimpleFinConnectionService>();
        services.AddScoped<ISyncService, SyncService>();

        return services;
    }
}
