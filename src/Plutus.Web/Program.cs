using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Plutus.Core;
using Plutus.Core.Data;
using Plutus.Core.SimpleFin;
using Plutus.Web.Authentication;
using Plutus.Web.BackgroundServices;
using Plutus.Web.Components;
using Radzen;

if (PasswordHashGenerator.IsRequested(args))
{
    try
    {
        PasswordHashGenerator.Run(Console.In, Console.Out);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 2;
    }

    return;
}

// Deliberately validate this before registering external services or touching the
// database. A deployment with a missing or malformed admin hash never starts.
var builder = WebApplication.CreateBuilder(args);
var passwordHash = PlutusAuthentication.GetRequiredPasswordHash();
var revokeAllSessionsOnStartup = PlutusAuthentication.GetRevokeAllSessionsOnStartup(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddRadzenComponents();
builder.Services.AddSingleAdministratorAuthentication(passwordHash);

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
builder.Services.AddHostedService<NoteBackfillService>();
builder.Services.AddHostedService<TransferBackfillService>();
builder.Services.AddHostedService<SyncDiagnosticService>();
builder.Services.AddHostedService<AccountMergeBackfill>();

var app = builder.Build();

// Apply any pending migrations on startup (creates the SQLite DB on first run).
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
    await using var db = factory.CreateDbContext();
    await db.Database.MigrateAsync();

    // This must complete before endpoints are mapped or hosted. If storage is
    // unavailable, startup fails rather than allowing a prior hash generation to
    // become valid again after a configuration rollback.
    var sessions = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
    if (revokeAllSessionsOnStartup)
    {
        // Recovery-only flag: a restored database can otherwise contain active
        // session records for cookies that were valid before the backup was made.
        // Any persistence failure aborts startup before endpoints are hosted.
        await sessions.RevokeAllSessionsAsync();
    }

    await sessions.RevokeActiveSessionsWithDifferentFingerprintAsync(
        PlutusAuthentication.GetPasswordHashFingerprint(passwordHash));
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

// Honor X-Forwarded-* only from the reverse proxy on the private Docker network —
// not from arbitrary clients, which could otherwise spoof scheme/client IP.
// ForwardLimit = 1 trusts only the last hop (the host-managed Caddy proxy).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
// RFC1918 ranges: the Docker bridge / compose network lives here.
forwardedHeaders.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
forwardedHeaders.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
forwardedHeaders.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
app.UseForwardedHeaders(forwardedHeaders);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapSingleAdministratorAuthentication();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .CloseConnectionsOnAuthenticationExpiration()
    .RequireAuthorization();

app.Run();
