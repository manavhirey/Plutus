# Sync staleness visibility + manual "Sync now" — design

**Date:** 2026-06-09
**Status:** Approved (design); pending implementation plan

## Problem

Daily sync can "succeed" while returning zero new transactions because the upstream
SimpleFIN bridge is serving stale data (a bank connection stopped refreshing). On
2026-06-09 the bridge had no transactions newer than June 6: five of six accounts'
`BalanceDate` were frozen at June 7 21:10 UTC while only BofA was fresh. Plutus synced
correctly but gave the user **no visibility** into the staleness — the dashboard silently
showed old data and the "Last Sync" tile read *Success*.

This feature surfaces per-account staleness on the dashboard and adds a manual trigger so
the user can force a sync and immediately see the result. It does **not** attempt to fix the
upstream bridge; it makes the upstream problem visible.

Scope is **#1 (per-account staleness)** and **#3 (manual "Sync now")** only. Surfacing
SimpleFIN's raw `errors[]` strings (#2) is explicitly out of scope.

## Decisions (locked)

- **Sync now button:** a small ↻ icon button on the existing **Last Sync** stat card
  (`Home.razor`), not a header button or a Settings action.
- **Staleness presentation:** per-account only — no dashboard-wide banner.
- **Stale threshold:** an account is stale when its `BalanceDate` is older than **24h**
  (configurable). 24h matches the daily sync cadence.

## Feature 1 — Per-account staleness

Each account card on the dashboard gains an "Updated …" line derived from the account's
bridge refresh timestamp (`Account.BalanceDate`, stored each sync from SimpleFIN's
`balance-date`):

- **Fresh** (≤ threshold): muted grey, e.g. `Updated 5h ago`.
- **Stale** (> threshold): amber text with a ⚠️ prefix, e.g. `⚠️ Updated 2 days ago`.

With today's data, BofA renders normal and the five Chase/Amex/Capital One cards render the
amber warning — exactly the missing signal.

### AccountFreshness helper (Plutus.Core)

A small pure, unit-testable helper isolates the time logic from the Blazor view:

```csharp
public static class AccountFreshness
{
    // Stale when (now - balanceDate) > threshold.
    public static bool IsStale(DateTime balanceDateUtc, DateTimeOffset now, TimeSpan threshold);

    // Human relative description, e.g. "5h ago", "2 days ago", "just now".
    public static string Describe(DateTime balanceDateUtc, DateTimeOffset now);
}
```

- `now` is supplied by the caller (the dashboard passes `TimeProvider.GetUtcNow()`), so tests
  are deterministic — no wall-clock flakiness.
- `BalanceDate` is stored UTC; `Describe` returns coarse buckets (minutes / hours / days)
  suitable for a glance, not precise durations.

The threshold comes from `SyncOptions.StaleAfterHours` (new property, default `24`), bound
from the existing `Plutus:Sync` configuration section.

## Feature 2 — Manual "Sync now"

A small ↻ icon button on the **Last Sync** card:

- **Click:** sets an in-progress flag (disables the button, shows a spinner) and calls the
  existing `ISyncService.RunAsync()` — the same path the `DailySyncScheduler` uses. No new
  sync logic is introduced.
- **On completion:** reloads the dashboard's data (accounts, last-sync run, needs-review
  count, and — best effort — the spend/net-worth panels) so the new `BalanceDate`s, status,
  and "N new" count are reflected immediately. The existing Last Sync tile already renders
  status + time + new-count, so the outcome (including a `Failed` status) shows there.
- **Disabled/hidden** when no SimpleFIN connection is configured (mirrors the existing
  "Connect SimpleFIN in Settings" empty state).
- **Concurrency:** single-user app; the in-progress flag prevents double-clicks. A sync
  firing from the scheduler at the same moment is acceptable (each run uses its own DI scope
  and the ingest dedupes by transaction id); no extra locking is added.

### Error handling

`ISyncService.RunAsync()` already records failures as a `SyncRun` with `Status = Failed` and
never throws for sync-internal errors. The button handler still wraps the call defensively so
a thrown exception (e.g. the service itself faulting) clears the in-progress flag and leaves
the UI usable; the Last Sync tile reflects the recorded status on reload.

## Components & data flow

```
Home.razor (dashboard)
  ├─ Accounts cards ── AccountFreshness.Describe / IsStale (Core, pure) ── Account.BalanceDate
  └─ Last Sync card ── [↻ Sync now] ── ISyncService.RunAsync() ── reload OnInitialized data
```

- No schema changes (`Account.BalanceDate` already exists and is populated each sync).
- No new services beyond the Core helper; `ISyncService` and `SyncOptions` already exist.

## Testing

- **Unit (`AccountFreshness`):** fresh just under threshold; stale just over threshold;
  boundary at exactly the threshold; `Describe` buckets (just now / hours / days). All driven
  by an injected `now` — deterministic.
- **Existing suite:** the current 34 tests stay green; the new `SyncOptions.StaleAfterHours`
  has a default so existing bindings are unaffected.
- Manual verification on prod after deploy: confirm the stale Chase/Amex/Capital One cards
  show the amber warning and BofA does not, and that "Sync now" runs and refreshes the tile.

## Out of scope

- Surfacing SimpleFIN `errors[]` text (#2).
- Any change to how/whether pending transactions are ingested (the `includePending` param
  added during diagnosis stays unused by the sync path).
- Fixing the upstream bridge/bank re-authentication (user action in the SimpleFIN bridge).
