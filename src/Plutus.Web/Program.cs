using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Plutus.Core;
using Plutus.Core.Data;
using Plutus.Web.BackgroundServices;
using Plutus.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Persist the Data Protection key ring to disk so the encrypted SimpleFIN access
// URL stays decryptable across restarts (this folder is a volume when containerized).
var keysPath = builder.Configuration["Plutus:DataProtectionKeysPath"] ?? "keys";
if (!Path.IsPathRooted(keysPath))
{
    keysPath = Path.Combine(builder.Environment.ContentRootPath, keysPath);
}

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Plutus");

var dbPath = builder.Configuration["Plutus:Database:Path"] ?? "plutus.db";
if (!Path.IsPathRooted(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, dbPath);
}

builder.Services.AddPlutusCore(builder.Configuration, $"Data Source={dbPath}");
builder.Services.AddHostedService<DailySyncScheduler>();

var app = builder.Build();

// Apply any pending migrations on startup (creates the SQLite DB on first run).
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
    await using var db = factory.CreateDbContext();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
