# Plutus — Personal Finance App: Design Spec

**Date:** 2026-06-07
**Status:** Approved design, pre-implementation

## Overview

Plutus is a single-user, self-hosted personal finance application built with
Blazor (.NET 10). It connects to bank accounts through **SimpleFIN Bridge**,
pulls each day's transactions automatically, classifies expenses into categories
using the **Claude API**, and lets the user write a note per expense that refines
the category. A "Past Transactions" view shows all expenses grouped by day.

### Goals
- Automatically ingest daily transactions from SimpleFIN with no manual trigger.
- Classify each expense into a category using Claude, constrained to a
  user-managed category list.
- Let the user add a note per expense; the note refines the category.
- Browse all past expenses grouped by day (description · category · amount).

### Non-goals (v1)
- Multi-user / public sign-up / authentication beyond what self-hosting needs.
- Tracking income (credits are filtered out — expenses only).
- A manual "Sync now" button (sync is automatic: scheduled + catch-up on startup).
- Budgets, reports, charts, mobile apps.

### Key decisions
- **Audience/hosting:** single user, self-hosted. SQLite storage, no heavy auth.
- **Categorization:** LLM-powered via Claude API, constrained to existing categories.
- **Note role:** the note refines the category (re-categorize on note save).
- **Sync trigger:** automatic daily (scheduled) + catch-up on startup. No manual button.
- **Categories:** built-in starter set, editable in the UI.
- **Income:** expenses only; credits filtered at sync time.
- **Architecture:** two projects — `Plutus.Core` (domain/data/services) + `Plutus.Web` (Blazor UI).

## Architecture

### Solution structure

```
Plutus.sln
├── Plutus.Core/                 (class library, net10.0)
│   ├── Models/                  Account, Transaction, Category, SyncRun, SimpleFinConnection
│   ├── Data/                    PlutusDbContext (EF Core + SQLite), EF configs, migrations
│   ├── SimpleFin/               ISimpleFinClient, SimpleFinClient, response DTOs
│   ├── Categorization/          ICategorizer, ClaudeCategorizer
│   ├── Sync/                    ISyncService, SyncService
│   └── Abstractions/            small interfaces, Result types
└── Plutus.Web/                  (Blazor Web App, net10.0, InteractiveServer)
    ├── Components/              Pages (Review, PastTransactions, Settings, Dashboard), layout
    ├── BackgroundServices/      DailySyncScheduler (BackgroundService)
    ├── appsettings.json + user-secrets
    └── Program.cs              DI wiring, EF, HttpClients, hosted service
```

`Plutus.Web` references `Plutus.Core`. All business logic, data access, and
external calls live in Core (unit-testable). Web is UI + composition root +
the background scheduler. Render mode: `InteractiveServer` (keeps the SimpleFIN
access URL and Claude key server-side; single-user so no scaling concern).

## Data model (EF Core, SQLite)

- **SimpleFinConnection** — `Id`, `AccessUrl` (encrypted at rest via ASP.NET
  Data Protection), `CreatedAt`, `LastSyncedAt`. Single row (the one bridge
  connection).
- **Account** — `Id`, `SimpleFinAccountId` (unique, used for upsert), `Name`,
  `Org`, `Currency`, `Balance`, `BalanceDate`.
- **Category** — `Id`, `Name`, `Color`, `IsSystem`, `SortOrder`. Seeded with a
  starter set; user-editable.
- **Transaction** — `Id`, `SimpleFinTransactionId` (unique, used for dedupe),
  `AccountId` (FK), `PostedDate`, `Amount` (expense / outflow), `Description`,
  `Note` (nullable), `CategoryId` (nullable FK), `IsCategorized`, `IsReviewed`,
  `CategorizedAt`. Only expense rows are persisted (credits filtered at sync).
- **SyncRun** — `Id`, `RanAt`, `Status` (Success/Failed), `NewTransactionCount`,
  `Error` (nullable). Audit trail; drives catch-up-on-startup.

Money stored with EF `decimal` value conversion; dates stored UTC.
Categorization is decoupled from persistence — a transaction may remain
`IsCategorized = false` if Claude fails, and is surfaced for retry in Review.

## SimpleFIN integration

SimpleFIN Bridge uses a one-time **setup token → claim → access URL** handshake.

1. **Onboarding (one-time, in Settings):** user pastes a *setup token* (Base64).
   The app Base64-decodes it to a *claim URL*, does `POST {claimUrl}` (empty
   body), and receives an **access URL** of the form
   `https://<user>:<pass>@bridge.simplefin.org/simplefin`. Stored encrypted
   (Data Protection) in `SimpleFinConnection`. The setup token is single-use;
   only the access URL persists.
2. **Fetching:** `GET {accessUrl}/accounts?start-date={unix}&end-date={unix}`
   returns `{ accounts: [{ id, name, org, currency, balance, balance-date,
   transactions: [{ id, posted, amount, description, ... }] }] }`.

`ISimpleFinClient` exposes `ClaimAsync(setupToken)` and
`GetAccountsAsync(start, end)` → typed DTOs. Implemented as a typed `HttpClient`
via `IHttpClientFactory` with `AddStandardResilienceHandler()` (retry + circuit
breaker). Basic-auth credentials come from the access URL userinfo, applied
per request.

## Categorization (Claude)

`ICategorizer.CategorizeAsync(description, note, IReadOnlyList<Category>
categories)` → `(string categoryName, double confidence)`.

`ClaudeCategorizer` uses the official **`Anthropic`** NuGet SDK with
**structured outputs**: a JSON schema whose `category` field is an **enum built
from the current category names**, so the model can only return a valid existing
category (no free-text drift). Schema: `{ category: enum[...categories...],
confidence: number }`. System prompt frames it as a personal-finance
categorizer; user content is the bank description (plus the note when refining).

- **Model:** configurable (`Plutus:Claude:Model`), default `claude-opus-4-8`;
  `claude-haiku-4-5` documented as the low-cost option for this high-volume
  classification. `thinking: adaptive`, low effort.
- **API key:** from user-secrets / env (`ANTHROPIC_API_KEY`); never in DB or source.
- **Two call sites:** (a) at sync, categorize from description alone → sets
  suggested `CategoryId`; (b) on note save, re-categorize with description + note
  → updates `CategoryId`. Manual override writes `CategoryId` directly (no API call).
- **Failure handling:** on any API error, leave `IsCategorized = false` and
  continue — the transaction appears uncategorized in Review. Sync never fails
  because Claude is unavailable.

## Daily sync

`SyncService.RunAsync(ct)`:
1. Load `SimpleFinConnection`; compute date range (`LastSyncedAt` minus a small
   overlap → now; first run uses a configurable look-back, e.g. 30 days).
2. `GetAccountsAsync` → upsert `Account` rows by `SimpleFinAccountId`.
3. For each transaction: **filter to expenses only** (drop credits), dedupe by
   `SimpleFinTransactionId`, insert new ones with `IsCategorized = false`.
4. Categorize each new transaction (description-only pass).
5. Write a `SyncRun`; update `LastSyncedAt`.

`DailySyncScheduler : BackgroundService` (in Plutus.Web):
- On startup, **catch-up**: if no successful `SyncRun` exists for today, run one
  immediately (covers the app not running at the scheduled time — still fully
  automatic, no manual button).
- Then loops: compute delay until next configured run time
  (`Plutus:Sync:DailyTime`, e.g. `06:00`), `await Task.Delay`, run, repeat.
- Each run opens its own DI scope for scoped `DbContext`/services.

## UI / pages (Blazor, InteractiveServer)

**`/` — Dashboard:** account cards (name, balance), a "needs review" count, last
sync time + status (from latest `SyncRun`). Read-only.

**`/review` — Daily review (note + category flow):** lists transactions where
`IsReviewed = false` (newest first), editable inline:

```
┌──────────────────────────────────────────────────────────────┐
│  Jun 07   AMZN MKTP US*2K4...        −$42.18                   │
│  Category: [ Shopping        ▼ ]   (suggested, conf 0.86)      │
│  Note:     [ birthday gift for Sam_________________ ]         │
│                                          [ Save & next ]       │
└──────────────────────────────────────────────────────────────┘
```

Saving writes the note → triggers the refine call (description + note → Claude)
→ updates the category → marks `IsReviewed = true`. The dropdown is editable, so
the user can override the suggestion (override skips the API call).

**`/transactions` — Past transactions:** all transactions grouped by day, newest
first, with a category filter:

```
  Past Transactions            Filter: [ All categories ▼ ]

  ── Saturday, Jun 7, 2026 ───────────────────────  −$71.40 ──
   AMZN MKTP US*2K4...        Shopping            −$42.18
   BLUE BOTTLE COFFEE         Dining              −$6.22
   SHELL OIL 574...           Transport           −$23.00

  ── Friday, Jun 6, 2026 ─────────────────────────  −$118.65 ──
   WHOLEFDS MKT #102          Groceries           −$118.65
```

Date-grouped headers with a daily total; each row shows description · category ·
amount. No editing here in v1 (review happens on `/review`).

**`/settings`:** ① SimpleFIN setup-token entry (paste → claim → store) +
connection status; ② category management (add/rename/delete, reorder); ③ sync
info (configured time, run history from `SyncRun`).

## Configuration & secrets
- `appsettings.json` (non-secret): `Plutus:Sync:DailyTime`, `Plutus:Claude:Model`,
  look-back days, SQLite path.
- Secrets via user-secrets / env: `ANTHROPIC_API_KEY`.
- SimpleFIN access URL stored encrypted in SQLite via Data Protection.
- SQLite file under the app content root (configurable).

## Error handling
- SimpleFIN: resilience handler retries transient failures; a hard failure
  records `SyncRun` as `Failed` with the error and retries next cycle. Surfaced
  on Dashboard/Settings.
- Claude: failures leave transactions uncategorized (visible in Review), never
  block sync.
- Service calls return a lightweight `Result` where the UI needs success/failure;
  exceptions logged via `ILogger`.

## Testing (Plutus.Core is the primary target)
- **SimpleFinClient:** parse a captured bridge JSON sample; verify claim flow
  with a mocked `HttpMessageHandler`.
- **ClaudeCategorizer:** mock the Anthropic client; assert the enum schema is
  built from the supplied categories and a valid category is returned.
- **SyncService:** SQLite/in-memory DbContext — verify expense-only filtering,
  dedupe by `SimpleFinTransactionId`, and `SyncRun` recording.
- Optional `bUnit` component tests for the Review save flow.
- Coverage kept proportional, not exhaustive.
