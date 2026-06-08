using Microsoft.EntityFrameworkCore;
using Plutus.Core.Categorization;
using Plutus.Core.Data;

namespace Plutus.Web.BackgroundServices;

/// <summary>
/// One-off background pass that generates AI notes (and optionally categorizes) every
/// transaction whose <c>Note</c> is still <c>null</c>.  Runs once on startup, then exits.
/// Enabled only when <c>Plutus:Backfill:Notes</c> is <c>true</c> in configuration;
/// otherwise it is a no-op so the feature is inert in production by default.
/// </summary>
public sealed class NoteBackfillService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<NoteBackfillService> logger) : BackgroundService
{
    private const int SaveBatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Plutus:Backfill:Notes"))
        {
            logger.LogInformation("Note backfill is disabled (Plutus:Backfill:Notes is not set). Skipping.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlutusDbContext>();
        var categorizer = scope.ServiceProvider.GetRequiredService<ICategorizer>();

        var categories = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.SortOrder)
            .ToListAsync(stoppingToken);

        var byName = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var pending = await db.Transactions
            .Where(t => t.Note == null)
            .ToListAsync(stoppingToken);

        var count = pending.Count;
        logger.LogInformation("Note backfill starting: {Count} transactions.", count);

        int updated = 0;
        int processedSinceSave = 0;

        for (int i = 0; i < pending.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var t = pending[i];

            try
            {
                var result = await categorizer.CategorizeAsync(t.Description, note: null, categories, stoppingToken);

                if (result is not null)
                {
                    t.Note = result.Note;

                    if (!t.IsCategorized && byName.TryGetValue(result.Category, out var category))
                    {
                        t.CategoryId = category.Id;
                        t.IsCategorized = true;
                        t.CategorizedAt = timeProvider.GetUtcNow().UtcDateTime;
                    }

                    updated++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Note backfill: failed to process transaction {Id} ('{Description}'). Skipping.",
                    t.Id, t.Description);
            }

            processedSinceSave++;

            if (processedSinceSave >= SaveBatchSize)
            {
                await db.SaveChangesAsync(stoppingToken);
                processedSinceSave = 0;
            }
        }

        if (processedSinceSave > 0)
        {
            await db.SaveChangesAsync(stoppingToken);
        }

        logger.LogInformation("Note backfill complete: {Updated} of {Count} updated.", updated, count);
    }
}
