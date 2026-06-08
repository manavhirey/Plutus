# Spending-by-Category Bar Chart — Design

**Date:** 2026-06-08
**Status:** Approved (design); pending implementation plan
**Feature:** A bar chart on the Dashboard showing total amount spent per category for the current month.

## Summary

Add a horizontal bar chart to the **Dashboard** (Home page) that shows total spend
per category for the **current calendar month**. Bars are sorted descending by
amount, colored with each category's brand color, and include a muted
"Uncategorized" bar so the chart reflects true total spend. Clicking a bar
navigates to the Transactions page pre-filtered to that category. Charting uses
the **Radzen.Blazor** component library (`RadzenChart` + `RadzenBarSeries`).

## Decisions (from brainstorming)

| Question | Decision |
| --- | --- |
| Time period | **Current calendar month** (always; no period selector in v1) |
| Placement | **On the Dashboard**, directly under the top stat cards |
| Uncategorized spend | **Shown as its own muted bar** (totals stay honest) |
| Interactivity | **Click a bar → Transactions filtered to that category** (plus built-in tooltips) |
| Charting tech | **Radzen.Blazor** (`RadzenChart`/`RadzenBarSeries`), per user request |
| Bar orientation | **Horizontal bars** (category names read better than under vertical columns) |

## Non-goals (v1 / YAGNI)

- No selectable time range (month/30d/year/all) — current month only.
- No trend-over-time, no pie/donut, no budgets/targets.
- No new "Insights" page — it lives on the existing Dashboard.

## Architecture

### 1. Data & query — `Plutus.Core`

A small, unit-testable query type rather than inline LINQ in the page (keeps the
page thin; matches the project's clean layering).

- **`record CategorySpend(int? CategoryId, string Name, string? Color, decimal Total)`**
- **`ISpendingReport`** with:
  `Task<IReadOnlyList<CategorySpend>> GetMonthlySpendingAsync(int year, int month, CancellationToken ct = default)`
- **`SpendingReport`** implementation:
  - Depends on `IDbContextFactory<PlutusDbContext>` (consistent with the existing
    factory-per-operation pattern), creates a short-lived context.
  - Computes the month window in **local time** (`new DateTime(year, month, 1)` →
    first day of next month) and converts those bounds to **UTC** for the query,
    because `Transaction.PostedDate` is stored in UTC.
  - Filters `Transactions` to `PostedDate >= startUtc && PostedDate < endUtc`,
    groups by `CategoryId`, sums `Amount` (amounts are stored as positive
    expense magnitudes).
  - Resolves category `Name`/`Color` from the `Categories` table for non-null ids.
  - Null `CategoryId` rows collapse into a single entry: `Name = "Uncategorized"`,
    `Color = null` (a neutral/muted color is applied at render time).
  - Returns **descending by `Total`**, with the **Uncategorized entry sorted last**
    regardless of its magnitude.
  - Read-only — uses `AsNoTracking()`.
- Registered **scoped** in `Plutus.Core/DependencyInjection.cs` (consistent with the
  other Core services; it is stateless and only resolves a context per call).

### 2. Dashboard chart — `Plutus.Web` (`Components/Pages/Home.razor`)

- New section directly under the top stat cards, inside a brand `.card`, with a
  Space Mono section-label: `THIS MONTH · SPENDING BY CATEGORY`.
- Loads `CategorySpend` data in `OnInitializedAsync` via the injected
  `ISpendingReport`, using `DateTime.Now.Year`/`.Month` for the current month.
- Renders a **`RadzenChart`** containing a **horizontal `RadzenBarSeries`**:
  - `Data` = the `CategorySpend` list, `CategoryProperty = nameof(CategorySpend.Name)`,
    `ValueProperty = nameof(CategorySpend.Total)`.
  - **`Fills`** = the ordered list of category colors (each category's `Color`,
    with a muted gray, e.g. `--gray` `#808184`, substituted for null/Uncategorized).
  - Built-in tooltip shows category name + amount; the value axis is formatted as
    currency (consistent with the `Money` helper's `$` presentation).
- **Empty state:** if the list is empty (no spend this month), render the brand
  `.empty-state` with a short message instead of the chart.
- **Click-to-filter:** handle `SeriesClick`; from the clicked data item navigate via
  `NavigationManager.NavigateTo("/transactions?category={CategoryId}")`, or
  `"/transactions?category=uncategorized"` when `CategoryId` is null.

### 3. Transactions deep-link — `Components/Pages/Transactions.razor`

- Add `[SupplyParameterFromQuery(Name = "category")] public string? CategoryParam { get; set; }`.
- On initialization, map the param to the existing filter:
  - numeric id → filter `CategoryId == id`
  - `"uncategorized"` → filter `CategoryId == null` (i.e. `!IsCategorized` rows with no category)
  - absent / `"0"` / unparseable → all (current default)
- Extend the in-page filter state to represent three cases (all / specific id /
  uncategorized) — e.g. keep the `int` filter with a sentinel (`0` = all,
  `-1` = uncategorized, `>0` = category id) and add an **"Uncategorized"** option to
  the existing dropdown for parity. The dropdown continues to drive filtering as today.

### 4. Dependencies & setup

- Add **`Radzen.Blazor` 10.4.7** (MIT, targets `net10.0`) to `Plutus.Web.csproj`.
- `_Imports.razor`: `@using Radzen` and `@using Radzen.Blazor`.
- `Program.cs`: `builder.Services.AddRadzenComponents();`
- `App.razor`: add `<RadzenTheme Theme="material" />` (a light theme; final choice
  verified against the brand during implementation) in `<head>` and
  `<script src="_content/Radzen.Blazor/Radzen.Blazor.js"></script>` after
  `blazor.web.js`.
- **Theme-clash watch-item:** Radzen's theme CSS is global. Pick a light theme and
  verify it does not disturb the already-brand-styled controls (selects, buttons,
  inputs). Radzen styles are `.rz-*`-prefixed, so conflicts should be minimal;
  override or scope if anything regresses. The chart's bar colors come from the
  category palette regardless of theme.

## Data flow

```
Home.OnInitializedAsync
  └─ ISpendingReport.GetMonthlySpendingAsync(year, month)
       └─ IDbContextFactory → PlutusDbContext (AsNoTracking)
            └─ Transactions [month window, UTC] → GroupBy CategoryId → Sum Amount
                 └─ resolve Name/Color, collapse Uncategorized, sort desc
       ⇒ IReadOnlyList<CategorySpend>
  └─ RadzenChart / RadzenBarSeries (Fills = colors)
       └─ SeriesClick ⇒ NavigationManager → /transactions?category=…
```

## Error handling & edge cases

- **No transactions this month** → empty `CategorySpend` list → brand empty-state
  (no chart).
- **All spend uncategorized** → a single "Uncategorized" bar.
- **Category with null `Color`** → muted gray fill.
- **Month boundary / timezone** → month bounds computed in local time, converted to
  UTC for the query; transactions in adjacent months are excluded. Covered by tests.
- **Deep-link with an invalid/unknown `category` value** → falls back to "all".

## Testing

- **Unit tests (`Plutus.Core.Tests`, existing in-memory SQLite harness):**
  - Seeds categories + transactions spanning the previous, current, and next month.
  - Asserts: current-month per-category totals are correct; results are descending
    by total; the uncategorized bucket aggregates null-category transactions and is
    sorted last; prior/next-month transactions are excluded (month-boundary
    correctness); empty result when no spend in the month.
- **Manual verification:** after deploy, confirm the chart renders with brand
  colors, tooltips work, the empty state shows when appropriate, and clicking a bar
  (including Uncategorized) lands on the correctly filtered Transactions page.

## Files touched

- **New:** `src/Plutus.Core/Reporting/CategorySpend.cs`,
  `src/Plutus.Core/Reporting/ISpendingReport.cs`,
  `src/Plutus.Core/Reporting/SpendingReport.cs` (namespace `Plutus.Core.Reporting`);
  `tests/Plutus.Core.Tests/SpendingReportTests.cs`.
- **Modified:** `src/Plutus.Core/DependencyInjection.cs`;
  `src/Plutus.Web/Plutus.Web.csproj`, `_Imports.razor`, `Program.cs`,
  `Components/App.razor`, `Components/Pages/Home.razor`,
  `Components/Pages/Transactions.razor`.
