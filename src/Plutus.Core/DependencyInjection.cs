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
        // A DbContext factory is the only registration: every consumer (Blazor components,
        // the sync services, the scheduler) creates a short-lived context per operation. A
        // single scoped DbContext would be shared across a whole Blazor circuit, and a
        // DbContext is not thread-safe — two overlapping async calls on one circuit would
        // throw "a second operation was started on this context".
        services.AddDbContextFactory<PlutusDbContext>(options => options.UseSqlite(connectionString));

        services.AddOptions<ClaudeOptions>().Bind(configuration.GetSection(ClaudeOptions.SectionName));
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        // API key comes from ANTHROPIC_API_KEY (env / user-secrets); never from config or DB.
        // Fail fast at startup rather than letting every categorization fail silently later.
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "ANTHROPIC_API_KEY is not set. Plutus needs it to categorize transactions — " +
                "provide it via environment variable or user-secrets before starting the app.");
        }

        services.AddSingleton(_ => new AnthropicClient { ApiKey = apiKey });

        services.AddHttpClient<ISimpleFinClient, SimpleFinClient>()
            .AddStandardResilienceHandler();

        services.AddSingleton<IConnectionProtector, DataProtectionConnectionProtector>();
        services.AddScoped<ICategorizer, ClaudeCategorizer>();
        services.AddScoped<ISimpleFinConnectionService, SimpleFinConnectionService>();
        services.AddScoped<ISyncService, SyncService>();

        return services;
    }
}
