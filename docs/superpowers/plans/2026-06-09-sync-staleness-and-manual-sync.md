# Sync Staleness Visibility + Manual "Sync now" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show per-account data staleness on the dashboard and add a manual "Sync now" button, so the user can see when a bank connection has stopped refreshing and force a sync on demand.

**Architecture:** A small pure helper (`AccountFreshness`) in `Plutus.Core` computes staleness and a relative-time label from each account's `BalanceDate` (the SimpleFIN bridge's last-refresh stamp). The Blazor dashboard (`Home.razor`) renders that per account card, and a ↻ button on the "Last Sync" card calls the existing `ISyncService.RunAsync()` then reloads the dashboard data. No schema changes, no new sync logic.

**Tech Stack:** .NET 10, Blazor (InteractiveServer), xUnit, EF Core (SQLite). Build with `export PATH="$HOME/.dotnet:$PATH"` first.

**Reference spec:** `docs/superpowers/specs/2026-06-09-sync-staleness-and-manual-sync-design.md`

---

### Task 1: `AccountFreshness` helper (Plutus.Core)

A pure, deterministic helper for staleness + relative-time, driven by a caller-supplied `now`.

**Files:**
- Create: `src/Plutus.Core/Reporting/AccountFreshness.cs`
- Test: `tests/Plutus.Core.Tests/AccountFreshnessTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Plutus.Core.Tests/AccountFreshnessTests.cs`:

```csharp
using Plutus.Core.Reporting;

namespace Plutus.Core.Tests;

public sealed class AccountFreshnessTests
{
    // Fixed reference "now" so every case is deterministic.
    private static readonly DateTimeOffset Now = new(2026, 06, 09, 12, 00, 00, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(24);

    [Fact]
    public void Not_stale_just_under_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-23); // 23h old
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Fact]
    public void Not_stale_exactly_at_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-24); // exactly 24h old
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Fact]
    public void Stale_just_over_threshold()
    {
        var balanceDate = Now.UtcDateTime.AddHours(-24).AddMinutes(-1); // 24h01m old
        Assert.True(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }

    [Theory]
    [InlineData(-30, "just now")]            // 30 seconds ago (value is seconds)
    public void Describe_seconds(int seconds, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddSeconds(seconds);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-5, "5m ago")]
    [InlineData(-45, "45m ago")]
    public void Describe_minutes(int minutes, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddMinutes(minutes);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-1, "1h ago")]
    [InlineData(-5, "5h ago")]
    public void Describe_hours(int hours, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddHours(hours);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Theory]
    [InlineData(-25, "1 day ago")]
    [InlineData(-50, "2 days ago")]
    public void Describe_days(int hours, string expected)
    {
        var balanceDate = Now.UtcDateTime.AddHours(hours);
        Assert.Equal(expected, AccountFreshness.Describe(balanceDate, Now));
    }

    [Fact]
    public void Future_balance_date_reads_just_now()
    {
        var balanceDate = Now.UtcDateTime.AddMinutes(5); // clock skew, in the future
        Assert.Equal("just now", AccountFreshness.Describe(balanceDate, Now));
        Assert.False(AccountFreshness.IsStale(balanceDate, Now, Threshold));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — `AccountFreshness` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Plutus.Core/Reporting/AccountFreshness.cs`:

```csharp
namespace Plutus.Core.Reporting;

/// <summary>
/// Pure helpers for presenting how fresh an account's data is, based on its SimpleFIN
/// <c>BalanceDate</c> (the bridge's last-refresh stamp). The caller supplies <c>now</c> so
/// the logic is deterministic and unit-testable. <c>balanceDateUtc</c> is stored UTC.
/// </summary>
public static class AccountFreshness
{
    /// <summary>True when the account hasn't refreshed within <paramref name="threshold"/>.</summary>
    public static bool IsStale(DateTime balanceDateUtc, DateTimeOffset now, TimeSpan threshold) =>
        Age(balanceDateUtc, now) > threshold;

    /// <summary>Coarse relative-time label for a glance, e.g. "just now", "5m ago", "2 days ago".</summary>
    public static string Describe(DateTime balanceDateUtc, DateTimeOffset now)
    {
        var age = Age(balanceDateUtc, now);

        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        var days = (int)age.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    // Age is clamped at zero so a future BalanceDate (clock skew) reads as "just now", not stale.
    private static TimeSpan Age(DateTime balanceDateUtc, DateTimeOffset now)
    {
        var age = now.UtcDateTime - DateTime.SpecifyKind(balanceDateUtc, DateTimeKind.Utc);
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: PASS — all `AccountFreshnessTests` green, and the existing 34 tests still pass (total 43).

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Reporting/AccountFreshness.cs tests/Plutus.Core.Tests/AccountFreshnessTests.cs
git commit -m "feat(core): add AccountFreshness helper for staleness + relative time"
```

---

### Task 2: Configurable stale threshold (`SyncOptions.StaleAfterHours`)

**Files:**
- Modify: `src/Plutus.Core/Sync/SyncOptions.cs`

- [ ] **Step 1: Add the property**

In `src/Plutus.Core/Sync/SyncOptions.cs`, add after the `ExternalCardPayees` property (keep everything else unchanged):

```csharp
    /// <summary>
    /// An account is shown as "stale" on the dashboard when its SimpleFIN balance-date is
    /// older than this many hours. Defaults to 24h, matching the daily sync cadence.
    /// </summary>
    public int StaleAfterHours { get; set; } = 24;
```

- [ ] **Step 2: Build to verify it compiles**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/Plutus.Core/Sync/SyncOptions.cs
git commit -m "feat(core): add SyncOptions.StaleAfterHours (default 24h)"
```

---

### Task 3: Staleness + Sync-now CSS

Adds a warning amber token (the brand's old `--amber` was remapped to Azure, so we add a dedicated one) and styles for the per-account "Updated" line and the ↻ button.

**Files:**
- Modify: `src/Plutus.Web/wwwroot/app.css`

- [ ] **Step 1: Add the warning token**

In `src/Plutus.Web/wwwroot/app.css`, inside the `:root { … }` block, immediately after the `--crimson-soft` line (around line 39), add:

```css
    --warn:         #B45309;                     /* amber-700, readable on paper */
    --warn-soft:    rgba(180, 83, 9, 0.10);
```

- [ ] **Step 2: Add component styles**

Append to the end of `src/Plutus.Web/wwwroot/app.css`:

```css
/* ── Account freshness + Sync now ─────────────────────────── */
.acct-updated {
    display: block;
    margin-top: .35rem;
    font-size: .72rem;
    font-weight: 600;
    letter-spacing: .02em;
    color: var(--gray);
}

.acct-updated.stale {
    color: var(--warn);
}

.sync-now-btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 26px;
    height: 26px;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    background: var(--white);
    color: var(--accent);
    font-size: 1rem;
    line-height: 1;
    cursor: pointer;
    transition: var(--transition);
}

.sync-now-btn:hover:not(:disabled) {
    background: var(--accent-soft);
    border-color: var(--accent);
}

.sync-now-btn:disabled {
    opacity: .55;
    cursor: default;
}

.btn-spinner {
    width: 13px;
    height: 13px;
    border: 2px solid var(--accent-soft);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: btn-spin .7s linear infinite;
}

@keyframes btn-spin {
    to { transform: rotate(360deg); }
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Plutus.Web/wwwroot/app.css
git commit -m "feat(web): add staleness + sync-now styles"
```

---

### Task 4: Dashboard wiring — staleness display, Sync-now button, data-load refactor

Wires the helper into the account cards, adds the ↻ button to the Last Sync card, and refactors the dashboard data-load so the button can reuse it without flipping the page-level loading state.

**Files:**
- Modify: `src/Plutus.Web/Components/Pages/Home.razor`

- [ ] **Step 1: Add injects and usings**

At the top of `src/Plutus.Web/Components/Pages/Home.razor`, after the existing `@inject ILogger<Home> Logger` line, add:

```razor
@inject ISyncService SyncService
@inject TimeProvider Time
@inject Microsoft.Extensions.Options.IOptions<Plutus.Core.Sync.SyncOptions> SyncOpts
```

(`Plutus.Core.Sync` and `Plutus.Core.Reporting` are already imported via `_Imports.razor`.)

- [ ] **Step 2: Add the ↻ button to the Last Sync card**

In `Home.razor`, replace the Last Sync card's label line:

```razor
        <div class="stat-card">
            <div class="stat-label">Last Sync</div>
```

with:

```razor
        <div class="stat-card">
            <div class="stat-label" style="display:flex; justify-content:space-between; align-items:center">
                <span>Last Sync</span>
                @if (_hasConnection)
                {
                    <button class="sync-now-btn" disabled="@_syncing" @onclick="SyncNowAsync"
                            title="Sync now" aria-label="Sync now">
                        @if (_syncing)
                        {
                            <span class="btn-spinner"></span>
                        }
                        else
                        {
                            <span>↻</span>
                        }
                    </button>
                }
            </div>
```

(Leave the rest of the card — the `@if (_lastRun is null)` block — unchanged.)

- [ ] **Step 3: Add the "Updated …" line to each account card**

In `Home.razor`, replace the account card body:

```razor
                <div class="stat-card">
                    <div class="stat-label">@account.Name</div>
                    <div class="stat-value amount" style="font-size:1.4rem">@Money.Plain(account.Balance)</div>
                    <span class="muted">@(account.Org ?? account.Currency)</span>
                </div>
```

with:

```razor
                <div class="stat-card">
                    <div class="stat-label">@account.Name</div>
                    <div class="stat-value amount" style="font-size:1.4rem">@Money.Plain(account.Balance)</div>
                    <span class="muted">@(account.Org ?? account.Currency)</span>
                    @{
                        var stale = AccountFreshness.IsStale(account.BalanceDate, _now, _staleThreshold);
                    }
                    <span class="acct-updated @(stale ? "stale" : "")">
                        @(stale ? "⚠️ " : "")Updated @AccountFreshness.Describe(account.BalanceDate, _now)
                    </span>
                </div>
```

- [ ] **Step 4: Add fields, refactor the data-load, add the handler**

In the `@code { … }` block of `Home.razor`:

(a) Add these fields next to the existing private fields (after `private TopCategory? _topCategory;`):

```csharp
    private bool _syncing;
    private bool _hasConnection;
    private DateTimeOffset _now;
    private TimeSpan _staleThreshold;
```

(b) Replace the entire `OnInitializedAsync` method:

```csharp
    protected override async Task OnInitializedAsync()
    {
        try
        {
            await LoadDataAsync();
        }
        finally
        {
            _loading = false;
        }
    }
```

(c) Add a `LoadDataAsync` method containing the data-load logic (this is the old body of `OnInitializedAsync` minus the `_loading` handling, plus the new `_now`, `_staleThreshold`, and `_hasConnection` loads). Place it immediately after `OnInitializedAsync`:

```csharp
    private async Task LoadDataAsync()
    {
        _now = Time.GetUtcNow();
        _staleThreshold = TimeSpan.FromHours(SyncOpts.Value.StaleAfterHours);

        await using var db = await DbFactory.CreateDbContextAsync();
        _accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        _needsReview = await db.Transactions.CountAsync(t => !t.IsReviewed);
        _lastRun = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.RanAt).FirstOrDefaultAsync();
        _hasConnection = await db.SimpleFinConnections.AnyAsync();

        try
        {
            var now = DateTime.Now;
            _spend = await SpendingReport.GetMonthlySpendingAsync(now.Year, now.Month);
            _fills = _spend.Select(s => s.Color ?? GrayFallback).ToList();
            _chartStyle = $"height:{_spend.Count * 44 + 60}px";
            _topCategory = SpendingInsights.Top(_spend);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load monthly spending for the dashboard chart.");
            _spend = [];
            _fills = [];
        }

        try
        {
            _netWorth = await NetWorthReport.GetAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load net worth for the dashboard.");
            _netWorth = null;
        }
    }

    private async Task SyncNowAsync()
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            await SyncService.RunAsync();
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            // RunAsync already records failures as a SyncRun; this just keeps the UI usable.
            Logger.LogError(ex, "Manual sync failed.");
        }
        finally
        {
            _syncing = false;
        }
    }
```

(d) Delete the old data-load body that remained inside `OnInitializedAsync` (the `await using var db = …` through the net-worth try/catch) — it now lives in `LoadDataAsync`. Verify `OnInitializedAsync` is exactly the version from step (b).

- [ ] **Step 5: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build && dotnet test`
Expected: 0 errors, 0 warnings; all 43 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Plutus.Web/Components/Pages/Home.razor
git commit -m "feat(web): show per-account staleness + add Sync now button on dashboard"
```

---

### Task 5: Manual verification + deploy

**Files:** none (verification + deploy).

- [ ] **Step 1: Run locally and eyeball the dashboard**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project src/Plutus.Web`
(needs `ANTHROPIC_API_KEY` in env). Open the dashboard and confirm:
- Each account card shows an "Updated …" line.
- Accounts older than 24h render amber with a ⚠️; fresh ones render muted grey.
- The Last Sync card shows a ↻ button (only when a SimpleFIN connection exists); clicking it disables the button + shows a spinner, then the tile and account "Updated …" lines refresh.

Stop with Ctrl+C.

- [ ] **Step 2: Build the container image and deploy**

```bash
sg docker -c 'export PATH="$HOME/.dotnet:$PATH" && dotnet publish src/Plutus.Web -c Release /t:PublishContainer'
sg docker -c "docker compose up -d"
```

- [ ] **Step 3: Verify on prod**

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://plutus.kunigami.cloud
```
Expected: `200`. Then load the live dashboard and confirm the five stale Chase/Amex/Capital One accounts show the amber ⚠️ warning while BofA does not, and that "Sync now" runs.

- [ ] **Step 4: Push**

```bash
git push origin main
```

---

## Self-Review notes

- **Spec coverage:** #1 staleness (Tasks 1, 3, 4), configurable threshold (Task 2), #3 Sync now (Tasks 3, 4), pure unit-tested helper (Task 1), reuse of `ISyncService` with no new sync logic (Task 4), no schema change (uses existing `Account.BalanceDate`). #2 (raw `errors[]`) intentionally excluded — matches the spec's out-of-scope.
- **Type consistency:** `AccountFreshness.IsStale(DateTime, DateTimeOffset, TimeSpan)` and `Describe(DateTime, DateTimeOffset)` are used with the same signatures in `Home.razor` (`_now` is `DateTimeOffset`, `_staleThreshold` is `TimeSpan`, `account.BalanceDate` is `DateTime`).
- **No placeholders:** every step contains the actual code/commands.
