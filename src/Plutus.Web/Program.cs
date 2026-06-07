using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Plutus.Core;
using Plutus.Core.Data;
using Plutus.Core.SimpleFin;
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

// Headless provisioning: if a SimpleFIN setup token is supplied via configuration
// (Plutus:SimpleFin:SetupToken) and no connection exists yet, claim it on startup.
// Lets a container on a server connect without the browser-based Settings flow.
// The token is single-use; once a connection exists it is ignored.
var setupToken = app.Configuration["Plutus:SimpleFin:SetupToken"];
if (!string.IsNullOrWhiteSpace(setupToken))
{
    using var scope = app.Services.CreateScope();
    var connections = scope.ServiceProvider.GetRequiredService<ISimpleFinConnectionService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (await connections.GetConnectionAsync() is not null)
    {
        logger.LogInformation("SimpleFIN already connected; ignoring configured setup token.");
    }
    else
    {
        try
        {
            await connections.ConnectAsync(setupToken.Trim());
            logger.LogInformation("SimpleFIN connection provisioned from configuration.");
        }
        catch (Exception ex)
        {
            // Don't crash startup on a bad/expired token; surfaced in Settings.
            logger.LogError(ex, "Failed to provision SimpleFIN connection from configuration.");
        }
    }
}

// Honor X-Forwarded-* from the reverse proxy (Traefik) so the app sees the real
// scheme/client. Proxies live on a dynamic Docker network, so trust all here —
// the app isn't exposed directly, only via the proxy.
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

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
