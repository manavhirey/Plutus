using System.Security.Claims;
using AngleSharp.Html.Parser;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Plutus.Core.Categorization;
using Plutus.Core.Data;
using Plutus.Core.Models;
using Plutus.Core.Reporting;
using Plutus.Core.Sync;
using Plutus.Web.Authentication;
using Plutus.Web.Components.Layout;
using Plutus.Web.Components.Pages;
using Radzen;
using Radzen.Blazor;

namespace Plutus.Web.Tests;

public sealed class DashboardPresentationTests
{
    [Fact]
    public async Task Dashboard_uses_neutral_copy_and_retains_the_existing_review_and_manual_sync_controls()
    {
        await using var fixture = await DashboardFixture.CreateAsync(
            [DashboardFixture.Account("EUR")],
            needsReview: 2,
            syncStatus: SyncStatus.Success,
            hasConnection: true);

        var cut = fixture.Context.RenderComponent<Home>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Transactions awaiting review", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Review transactions", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Latest sync attempt", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Success", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Sync now", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("100.00 EUR", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Healthy", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Confirmed", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("caught up", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('$', cut.Markup);
        });

        var reviewLink = cut.Find("a[href='review']");
        Assert.Equal("Review transactions →", reviewLink.TextContent.Trim());
        var syncButton = cut.Find("button.sync-now-btn");
        Assert.Contains("Sync now", syncButton.TextContent, StringComparison.Ordinal);
        Assert.Equal("sync-status-detail", syncButton.GetAttribute("aria-describedby"));
        Assert.Contains("dashboard-summary-grid", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_hides_aggregate_widgets_for_zero_mixed_or_invalid_account_currencies()
    {
        foreach (var accounts in new[]
                 {
                     Array.Empty<Account>(),
                     new[] { DashboardFixture.Account("USD"), DashboardFixture.Account("EUR", "second") },
                     new[] { DashboardFixture.Account("usd") },
                 })
        {
            await using var fixture = await DashboardFixture.CreateAsync(accounts, needsReview: 0);
            var cut = fixture.Context.RenderComponent<Home>();

            cut.WaitForAssertion(() =>
            {
                Assert.DoesNotContain("Net worth", cut.Markup, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Top category", cut.Markup, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Spending by category (current categorization)", cut.Markup, StringComparison.OrdinalIgnoreCase);
            });

            if (accounts.Length > 0)
            {
                Assert.Contains("Balances are shown by currency; no total is calculated.", cut.Markup, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Dashboard_renders_usd_with_an_iso_suffix_not_a_dollar_prefix()
    {
        await using var fixture = await DashboardFixture.CreateAsync([DashboardFixture.Account("USD")], needsReview: 0);
        var cut = fixture.Context.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("100.00 USD", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain('$', cut.Markup);
        });
    }

    [Fact]
    public async Task Dashboard_chart_uses_iso_axis_tooltip_fallback_and_existing_category_route()
    {
        await using var fixture = await DashboardFixture.CreateAsync(
            [DashboardFixture.Account("EUR")],
            needsReview: 0,
            hasSpending: true);
        var cut = fixture.Context.RenderComponent<Home>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.FindComponent<RadzenBarSeries<CategorySpend>>().Instance.TooltipTemplate);
        });

        var barSeries = cut.FindComponent<RadzenBarSeries<CategorySpend>>().Instance;
        var tooltip = fixture.Context.Render(barSeries.TooltipTemplate!(new CategorySpend(7, "Fixture category", "#123456", 100m)));
        Assert.Contains("Fixture category: 100.00 EUR", tooltip.Markup, StringComparison.Ordinal);

        var fallback = cut.Find("ul.chart-text-fallback");
        Assert.Equal("Spending by category values", fallback.GetAttribute("aria-label"));
        Assert.Contains("Fixture category: 100.00 EUR", fallback.TextContent, StringComparison.Ordinal);

        var axis = cut.FindComponent<RadzenValueAxis>().Instance;
        Assert.Equal("100.00 EUR", axis.Formatter!(100m));

        await cut.InvokeAsync(() => cut.FindComponent<RadzenChart>().Instance.SeriesClick.InvokeAsync(new SeriesClickEventArgs
        {
            Data = new CategorySpend(7, "Fixture category", "#123456", 100m),
        }));
        Assert.EndsWith("/transactions?category=7", fixture.Context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_sync_retains_literal_status_settings_route_and_manual_sync_busy_semantics()
    {
        await using var fixture = await DashboardFixture.CreateAsync(
            [DashboardFixture.Account("EUR")],
            needsReview: 0,
            syncStatus: SyncStatus.Failed,
            hasConnection: true,
            blockSync: true);
        var cut = fixture.Context.RenderComponent<Home>();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Failed", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("View sync settings", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Healthy", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal("settings", cut.Find("a.sync-settings-link").GetAttribute("href"));

        cut.Find("button.sync-now-btn").Click();
        await fixture.SyncService.WaitUntilStartedAsync();
        cut.WaitForAssertion(() =>
        {
            var syncButton = cut.Find("button.sync-now-btn");
            Assert.True(syncButton.HasAttribute("disabled"));
            Assert.Contains("Syncing", syncButton.TextContent, StringComparison.Ordinal);
        });
        fixture.SyncService.Complete();
    }

    [Fact]
    public async Task Review_empty_state_and_navigation_are_conservative_and_accessibly_named()
    {
        await using var fixture = await DashboardFixture.CreateAsync(Array.Empty<Account>(), needsReview: 0);

        var review = fixture.Context.RenderComponent<Review>();
        review.WaitForAssertion(() =>
        {
            Assert.Contains("No transactions currently awaiting review", review.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("All caught up", review.Markup, StringComparison.OrdinalIgnoreCase);
        });

        var layout = fixture.Context.RenderComponent<MainLayout>(parameters => parameters
            .Add(p => p.Body, _ => { }));
        var navigationText = layout.Find("nav").TextContent;
        Assert.Matches("Dashboard[\\s\\S]*Review[\\s\\S]*Transactions[\\s\\S]*Settings", navigationText);
        Assert.Contains("nav-icon", layout.Markup, StringComparison.Ordinal);
        Assert.Equal("/", layout.Find("a.sidebar-brand").GetAttribute("href"));
        var dashboardDocument = new HtmlParser().ParseDocument(await AuthenticationTests.GetAuthenticatedDashboardHtmlAsync());
        var signOutForm = dashboardDocument.QuerySelector("form.sidebar-signout");
        Assert.NotNull(signOutForm);
        Assert.Equal("post", signOutForm.GetAttribute("method"));
        Assert.Equal("/logout", signOutForm.GetAttribute("action"));
        var signOutButton = signOutForm.QuerySelector("button.nav-link-button");
        Assert.NotNull(signOutButton);
        Assert.Equal("submit", signOutButton.GetAttribute("type"));
        Assert.Equal("Sign out", signOutButton.TextContent.Trim());
        var antiforgeryToken = signOutForm.QuerySelector("input[type='hidden'][name='__RequestVerificationToken']");
        Assert.NotNull(antiforgeryToken);
    }

    [Fact]
    public void Currency_formatter_requires_an_uppercase_iso_code_and_never_uses_a_symbol()
    {
        Assert.True(CurrencyAmountFormatter.IsValidIso4217Code("EUR"));
        Assert.True(CurrencyAmountFormatter.IsValidIso4217Code("XCG"));
        Assert.True(CurrencyAmountFormatter.IsValidIso4217Code("ZWG"));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code("eur"));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code("HRK"));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code("ZWL"));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code(""));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code(" EUR "));
        Assert.False(CurrencyAmountFormatter.IsValidIso4217Code("QQQ"));
        Assert.Equal("100.00 EUR", CurrencyAmountFormatter.Format(100m, "EUR"));
        Assert.Equal("100.00 USD", CurrencyAmountFormatter.Format(100m, "USD"));
    }

    [Fact]
    public async Task Dashboard_hides_aggregates_for_blank_unknown_and_same_invalid_currency_codes()
    {
        foreach (var accounts in new[]
                 {
                     new[] { DashboardFixture.Account(""), DashboardFixture.Account("", "second") },
                     new[] { DashboardFixture.Account("QQQ"), DashboardFixture.Account("QQQ", "second") },
                     new[] { DashboardFixture.Account("EUR"), DashboardFixture.Account("QQQ", "second") },
                 })
        {
            await using var fixture = await DashboardFixture.CreateAsync(accounts, needsReview: 0);
            var cut = fixture.Context.RenderComponent<Home>();
            cut.WaitForAssertion(() => Assert.DoesNotContain("Net worth", cut.Markup, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Responsive_navigation_and_motion_css_keep_a_320px_two_row_layout_with_focus_hooks()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var cssPath = Path.Combine(projectRoot, "src", "Plutus.Web", "wwwroot", "app.css");
        var css = File.ReadAllText(cssPath);
        var mobile = CssSection(css, "@media (max-width: 768px)", "@media (max-width: 430px)");
        var narrow = CssSection(css, "@media (max-width: 430px)", "@media (max-width: 320px)");
        var phone320 = CssSection(css, "@media (max-width: 320px)", "@media (prefers-reduced-motion: reduce)");

        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", css, StringComparison.Ordinal);
        Assert.Contains(".sidebar-brand", mobile, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", mobile, StringComparison.Ordinal);
        Assert.Contains("align-self: stretch", mobile, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: 44px 44px", mobile, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(4, minmax(0, 1fr))", mobile, StringComparison.Ordinal);
        Assert.Contains(".sidebar-signout", mobile, StringComparison.Ordinal);
        Assert.Contains("grid-column: 2", mobile, StringComparison.Ordinal);
        Assert.Contains(".sidebar-signout .nav-link-button", mobile, StringComparison.Ordinal);
        Assert.Contains(".sidebar-nav a", narrow, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", narrow, StringComparison.Ordinal);
        Assert.Contains(".sidebar-nav a .nav-icon", narrow, StringComparison.Ordinal);
        Assert.Contains("display: none", narrow, StringComparison.Ordinal);
        Assert.Contains(".sidebar-brand", phone320, StringComparison.Ordinal);
        Assert.Contains("min-width: 0", phone320, StringComparison.Ordinal);
    }

    private static string CssSection(string css, string startMarker, string endMarker)
    {
        var start = css.IndexOf(startMarker, StringComparison.Ordinal);
        var end = css.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not find CSS section from {startMarker} to {endMarker}.");
        return css[start..end];
    }

    private sealed class DashboardFixture : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly SqliteConnection _keepAliveConnection;

        private DashboardFixture(TestContext context, string databasePath, SqliteConnection keepAliveConnection)
        {
            Context = context;
            _databasePath = databasePath;
            _keepAliveConnection = keepAliveConnection;
        }

        public TestContext Context { get; }
        public FixtureSyncService SyncService { get; private set; } = null!;

        public static async Task<DashboardFixture> CreateAsync(
            IReadOnlyCollection<Account> accounts,
            int needsReview,
            SyncStatus? syncStatus = null,
            bool hasConnection = false,
            bool hasSpending = false,
            bool blockSync = false)
        {
            var context = new TestContext();
            // Radzen creates its chart after render; the presentation assertions do
            // not depend on that browser-only geometry callback.
            var databasePath = Path.Combine(Path.GetTempPath(), $"plutus-dashboard-{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
            var options = new DbContextOptionsBuilder<PlutusDbContext>().UseSqlite(connectionString).Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Accounts.AddRange(accounts);
                if (syncStatus is not null)
                {
                    db.SyncRuns.Add(new SyncRun
                    {
                        RanAt = FixedTimeProvider.Now.UtcDateTime,
                        Status = syncStatus.Value,
                        NewTransactionCount = 1,
                    });
                }

                if (hasConnection)
                {
                    db.SimpleFinConnections.Add(new SimpleFinConnection
                    {
                        AccessUrl = "https://fixture.invalid/bridge",
                        CreatedAt = FixedTimeProvider.Now.UtcDateTime,
                    });
                }

                for (var index = 0; index < needsReview; index++)
                {
                    db.Transactions.Add(new Transaction
                    {
                        Account = accounts.FirstOrDefault() ?? Account("EUR", "review"),
                        SimpleFinTransactionId = $"fixture-transaction-{index}",
                        Amount = 12.34m,
                        Description = "Invented merchant",
                        PostedDate = FixedTimeProvider.Now.UtcDateTime,
                    });
                }

                var fingerprint = "fixture-fingerprint";
                var sessionId = Guid.NewGuid();
                db.AdministratorSessions.Add(new AdministratorSession
                {
                    Id = sessionId,
                    PasswordHashFingerprint = fingerprint,
                    IssuedAt = FixedTimeProvider.Now.UtcDateTime,
                    ExpiresAt = FixedTimeProvider.Now.AddHours(1).UtcDateTime,
                });
                await db.SaveChangesAsync();

                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(PlutusAuthentication.SessionIdClaimType, sessionId.ToString("N")),
                    new Claim(PlutusAuthentication.PasswordHashFingerprintClaimType, fingerprint),
                ], "fixture"));
                var syncService = new FixtureSyncService(blockSync);
                ConfigureServices(context, factory, principal, fingerprint, syncService, hasSpending);

                var keepAliveConnection = new SqliteConnection(connectionString);
                await keepAliveConnection.OpenAsync();
                return new DashboardFixture(context, databasePath, keepAliveConnection)
                {
                    SyncService = syncService,
                };
            }
        }

        public static Account Account(string currency, string suffix = "primary") => new()
        {
            SimpleFinAccountId = $"fixture-account-{suffix}",
            Name = $"Fixture {suffix}",
            Currency = currency,
            Balance = 100m,
            BalanceDate = FixedTimeProvider.Now.UtcDateTime,
        };

        private static void ConfigureServices(
            TestContext context,
            IDbContextFactory<PlutusDbContext> factory,
            ClaimsPrincipal principal,
            string fingerprint,
            FixtureSyncService syncService,
            bool hasSpending)
        {
            var time = new FixedTimeProvider();
            var stateProvider = new FixtureAuthenticationStateProvider(principal);
            context.Services.AddSingleton<TimeProvider>(time);
            context.Services.AddSingleton<IDbContextFactory<PlutusDbContext>>(factory);
            context.Services.AddSingleton<AuthenticationStateProvider>(stateProvider);
            context.Services.AddSingleton(new AdministratorAuthenticationState("fixture-hash", fingerprint));
            context.Services.AddSingleton<AdministratorSessionOperationCoordinator>();
            context.Services.AddScoped<AdministratorSessionStore>();
            context.Services.AddScoped<AdministratorSessionGuard>();
            context.Services.AddSingleton<IOptions<SyncOptions>>(Options.Create(new SyncOptions()));
            context.Services.AddSingleton<ISyncService>(syncService);
            context.Services.AddSingleton<ISpendingReport>(new FixtureSpendingReport(hasSpending));
            context.Services.AddSingleton<INetWorthReport>(new FixtureNetWorthReport());
            context.Services.AddSingleton<ICategorizer, FixtureCategorizer>();
            context.Services.AddSingleton<ILogger<Home>>(NullLogger<Home>.Instance);
            context.Services.AddRadzenComponents();
            if (hasSpending)
            {
                context.Services.AddSingleton<IJSRuntime, FixtureJsRuntime>();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _keepAliveConnection.DisposeAsync();
            Context.Dispose();
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlutusDbContext> options) : IDbContextFactory<PlutusDbContext>
    {
        public PlutusDbContext CreateDbContext() => new(options);

        public Task<PlutusDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlutusDbContext(options));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class FixtureSyncService(bool block)
        : ISyncService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SyncRun?> RunAsync(CancellationToken ct = default)
        {
            _started.TrySetResult();
            if (block)
            {
                await _completed.Task.WaitAsync(ct);
            }

            return null;
        }

        public Task WaitUntilStartedAsync() => _started.Task;
        public void Complete() => _completed.TrySetResult();
    }

    private sealed class FixtureSpendingReport(bool hasSpending) : ISpendingReport
    {
        public Task<IReadOnlyList<CategorySpend>> GetMonthlySpendingAsync(int year, int month, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CategorySpend>>(hasSpending
                ? [new CategorySpend(7, "Fixture category", "#123456", 100m)]
                : []);
    }

    private sealed class FixtureNetWorthReport : INetWorthReport
    {
        public Task<NetWorth> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new NetWorth(100m, 100m, 0m));
    }

    private sealed class FixtureCategorizer : ICategorizer
    {
        public Task<CategorizationResult?> CategorizeAsync(
            string description,
            string? note,
            IReadOnlyList<Category> categories,
            CancellationToken ct = default) => Task.FromResult<CategorizationResult?>(null);
    }

    private sealed class FixtureJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(CreateResult<TValue>());

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(CreateResult<TValue>());

        private static TValue CreateResult<TValue>() =>
            typeof(TValue).IsValueType
                ? Activator.CreateInstance<TValue>()
                : (TValue)Activator.CreateInstance(typeof(TValue))!;
    }
}
