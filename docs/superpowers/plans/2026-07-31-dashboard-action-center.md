# Dashboard action center — implementation handoff

**Date:** 2026-07-31
**Status:** Approved plan; implementation not started; independent-review revisions incorporated
**Reference design:** [`../specs/2026-07-31-dashboard-action-center-design.md`](../specs/2026-07-31-dashboard-action-center-design.md)

## Status reconciliation and precedence

This is the current execution plan for the approved dashboard direction. It supersedes the dashboard hierarchy/report assumptions in the historical June plan while retaining its implemented transfer exclusion. The July 30 review/sync-health design and plan remain the foundation for truthful sync and enrichment-state work; this plan sequences their dependencies for the dashboard rather than duplicating them.

Do **not** reopen or schedule the completed OpenAI provider migration or its authenticated production deployment/access-control foundation. Preserve authentication and the current README production runbook for any later deployment. The current in-process sync/session coordinators are valid only for one app replica; do not scale out before shared coordination is designed.

## Starting point and source map

| Current surface | Behavior to replace or preserve |
| --- | --- |
| `src/Plutus.Web/Components/Pages/Home.razor` | Four equal cards; `!IsReviewed` means review; latest `Success` appears healthy; `DateTime.Now` drives reports; accounts/net worth have equal priority. |
| `src/Plutus.Web/Components/Layout/MainLayout.razor` | Dashboard-first navigation without the planned review badge. |
| `src/Plutus.Web/Components/Pages/Review.razor` | Note-change binding invokes `ICategorizer`; remove before explicit-submit review ships. |
| `src/Plutus.Web/Components/Pages/Transactions.razor`, `TransactionEditPanel.razor` | Legacy description/category edit needs final/raw provenance and atomic correction. |
| `src/Plutus.Core/Reporting/SpendingReport.cs` | Groups all in-window transactions, including proposals; calls null category Uncategorized. |
| `src/Plutus.Core/Reporting/NetWorthReport.cs` | Sums account balances across currencies. |
| `src/Plutus.Core/Models/{Transaction,SyncRun}.cs`, `src/Plutus.Core/Sync/SyncService.cs` | Legacy review fields, only Success/Failed, and model work in sync critical path. |
| `src/Plutus.Core/Sync/SyncOptions.cs`, `src/Plutus.Web/BackgroundServices/DailySyncScheduler.cs` | Existing stale/scheduling behavior; add explicit testable finance-zone boundary. |

Read the current design, prior July plan, and `CLAUDE.md` before each affected task. Historical June docs are context only.

## Dependency sequence

```text
Quick visual pass (conservative copy/a11y only; independent) ────┐
                                                                    │
finance zone validation/calendar ──→ scheduler-local behavior ────┤
                                                                    │
sync truth: outcomes → classification/recovery → coordinator → UI ┤
                                                                    ├─ dashboard read model
review truth: migration/state → unavailable enricher → worker ────┤
                                                                    │
review UI + final corrections → confirmed reports/finance time ───┤
                                                                    ▼
                                                          full action-centre UI
                                                                    ▼
                                                   accessibility, rollout, release
```

Do not ship a full state assertion, Review-first CTA, or confirmed-spending label before its projection can prove it. The quick pass is independent only because it leaves primary-action precedence untouched.

## Phase 0 — quick visual pass (safe now)

**Purpose:** Improve conservative presentation and accessibility without changing action precedence/badges, sync, enrichment, report predicate, finance calendar, model/worker behavior, persistence, API, or data semantics.

**Allowed:** Presentation, CSS, layout spacing, accessible labels, wording accurate for current data, and conservative currency display. **Forbidden:** New health classification, state selector, action precedence change, nav badge, changed report predicate, confirmed-total claim, finance calendar/configuration, model call, worker, migration, persistence/API change, or a pending-is-caught-up statement. This phase must not call legacy `Success` “Healthy” or current spending “Confirmed.” It must not make Review sole or first CTA: current data cannot prove that a manual sync is not running, a failure is unresolved, or a connection is absent.

### Exact implementation checklist

- [ ] Keep current primary-card/action parity and navigation order. Do not move Review ahead of Sync as a primary action, alter manual-sync availability, add a Review badge, or change the finance date/calendar. Improve spacing, heading hierarchy, and control accessibility only within the existing neutral card layout.
- [ ] Give Review neutral current-state wording: “Transactions awaiting review” for a nonzero legacy count; “No transactions currently awaiting review” for zero. The Review page's empty state uses “No transactions currently awaiting review” rather than “All caught up.” Neither screen says caught up or all clear.
- [ ] Reword sync card as **Latest sync attempt** and preserve literal `Success`/`Failed` plus timestamp. Always retain the existing Settings/setup route and manual-sync affordance. `Success` is not Healthy; `Failed` is not silently buried behind Review.
- [ ] Reword chart/summary as **Spending by category (current categorization)** or equivalent. Do not call it reviewed/finalized/confirmed. Preserve query and click behavior.
- [ ] Treat existing aggregate widgets as currency-sensitive presentation. Eligibility is exactly: dashboard account query returns at least one account; every returned `Account.Currency` is a non-empty uppercase member of the application's static ISO-4217 code set; and all codes are identical by ordinal comparison. Only then render aggregate spending, top-category, and net-worth widgets. Otherwise omit all aggregate widgets; individual account balances always include their ISO code. Do not add FX or alter report data.
- [ ] Replace use of `Money.Plain`/dollar-prefixed formatting for every Phase-0 rendered amount with one ISO-aware presentation formatter, e.g. `CurrencyAmountFormatter.Format(amount, currency) → "100.00 EUR"`. It uses the ISO suffix and never a `$` prefix for any currency (including USD, which renders `100.00 USD`). Chart axis/tick/tooltip/category values use the same formatter or omit the chart when not eligible.
- [ ] Replace/enlarge the icon-only sync affordance or provide visible label, focus, busy text, disabled reason, and 44×44 target. Do not alter invocation/coordination.
- [ ] Use existing CSS tokens/responsive grid; no horizontal scroll at 320px. Do not claim DOM-first Review ordering as action precedence while health is unknown.
- [ ] Add `bunit` to `tests/Plutus.Web.Tests` and create `DashboardPresentationTests` using a `TestContext` with a deterministic `TimeProvider`, disposable SQLite `IDbContextFactory`, and fake `ISyncService`. Build a real `AdministratorSessionGuard` from the existing `AdministratorSessionStore`, `AdministratorAuthenticationState`, `AdministratorSessionOperationCoordinator`, and a test `AuthenticationStateProvider`; seed a non-expired `AdministratorSession` and matching-fingerprint `ClaimsPrincipal` in the fixture. Seed only invented account/category/transaction names. Render Home/Review/MainLayout through this authenticated fixture; do not bypass authorization by rendering anonymous services.
- [ ] Automatic assertions cover exact conservative copy, literal Success/Failed (and absence of Healthy/Confirmed/Caught up), retained Settings/manual-sync route, zero/nonzero Review copy, exact eligibility rule, one-/multi-/zero-currency aggregate visibility, and ISO labels. A non-USD fixture asserts `100.00 EUR` and asserts no rendered `$`; USD fixture asserts `100.00 USD`. Also assert accessible control names/44px hooks, DOM structure, and responsive CSS hooks. Do not change legacy sync/report assertions.
- [ ] Manual visual QA is separate from automated DOM tests: authenticated local fixture at 320px, 768px, and desktop widths; check wrapping, focus visibility, no horizontal scroll, and no sensitive data in screenshots/output. Exercise no connection, zero/nonzero legacy review rows, latest Success, latest Failed, and manual-sync control.

**Exit:** Conservative, more usable presentation with no implied priority/health/currency claim. The true action hierarchy is deferred to tested `DashboardState`. It can deploy independently after normal tests/review.

## Phase 1 — finance boundary and truthful sync-health foundation

Implement July 30 Milestone 0 before full dashboard health use:

1. First add validated `Plutus:Finance:TimeZoneId` and injectable finance calendar/clock. When the setting is absent, bind `America/New_York`; when it is explicitly present but blank, malformed, or unsupported, fail closed at startup before `DailySyncScheduler` initializes. Never fall back to host local time. This boundary is implemented now and consumed across reports/pages in Phase 4.
2. Add Success/Degraded/Failed/Skipped, trigger/duration/window, safe reason codes, last-healthy watermark, completed cursor, account observations, and recovery metadata. Test EF migration from pre-feature SQLite.
3. Classify after logical account matching. Warnings, missing active expected accounts, and stale balance data are Degraded; partial data ingests; degraded/failed/skipped never advance healthy watermark. Bound recovery and require explicit backfill after window.
4. Create one single-replica coordinator for manual, scheduled, startup, reconnect, and recovery; report contention rather than overlapping SQLite writes. Add bounded transient retry and use the already validated finance calendar for deterministic scheduler-local date/DST behavior.
5. Put reconnect validation/replacement through it. Every user-initiated manual sync and reconnect acquires `AdministratorSessionGuard` before I/O and holds its lease across coordinator calls and database commit. Preserve known-good credential on invalid replacement; safely swap a valid-but-degraded one and recover.
6. Add `SyncHealthSummary`; update Settings then Home for Not connected/Attention/Failed/syncing, posted-only semantics, and resolution routes.

**Tests:** absent setting resolves America/New_York; explicitly blank, malformed, and unsupported setting each fail before scheduler startup; valid IANA zone/DST/host-zone-independent calendar behavior; migration/state fixture; first/reminted accounts; warning/missing/stale/retired/malformed/persistence; recovery/backfill boundary; coordinator collision/retry/auth; manual sync/reconnect logout-or-expiry cancellation during I/O and immediately before commit; health projection state coverage.

**Exit:** Home consumes explicit health projection; no `lastRun.Status == Success` health inference.

## Phase 2 — proposal/final transaction foundation

Implement July 30 Milestone 1:

1. Add proposal/final fields, `EnrichmentStatus`, retry/error/attempt, CAS lease/version, and queue indexes. Implement Pending/Processing/Ready/Failed/Finalized/SkippedTransfer and exact conservative legacy migration.
2. Add `ITransactionEnricher` with no-key/unavailable mode and the spec's pure `EnrichmentRequestBuilder`/`BoundedEnrichmentRequest` boundary. Provider migration is done: do not modify or re-deploy it. Outside stored/internal dedupe/transfer use, raw description may reach only the bounded request path or dedicated authenticated Review evidence projection; it may not enter generic DTOs/signals/diagnostics/logs/errors. Enforce scalar/UTF-8 description limits, category count/name/schema limits, total request budget, safe rune-boundary truncation, and bounded `CategoryInputTooLarge`/unavailable outcomes; do not silently drop category options.
3. Remove model work from bank ingest with durable bounded worker; new non-transfer rows persist Pending. Worker is explicitly outside user-session coordination and uses hosted-service cancellation plus durable leases. Require retry, stale-lease recovery, separate-context tests, and finalization-wins.
4. Add `ReviewQueueSummary` from status. Define manual-recoverable failure and restrict future badge to Ready + those failures.

**Tests:** actual legacy upgrade; migration/state matrix; raw immutability/transfers; authenticated evidence projection is the sole UI/circuit raw-description path; exact/multibyte description limits; safe truncation; category/input total-budget overflow; oversize/malicious instruction-like merchant text; only-bounded-request provider path; unavailable startup; offline/retry/lease/restart worker; two-context race; prompt/log/error/signal/generic-DTO capture; queue summary.

**Exit:** review state is meaningful beyond `IsReviewed`; bank sync works without the model.

## Phase 3 — review and correction integrity

1. Replace `Review.razor` bind-time `RefineFromNoteAsync` with guided proposal review. Unchanged approval makes zero model calls. Edited explicit submit makes at most one, exposes busy state, and leaves a recoverable failure unreviewed.
2. Add server-side `ReviewQueuePage`: fixed maximum 25, stable `ActionableAt DESC, TransactionId DESC` keyset ordering, opaque cursor, separate live aggregate counts, and load-more. Add transactional global monotonic actionability-revision allocation on every transition into Ready/recoverable Failed, plus authenticated-session-owned, expiring `ReviewQueueSnapshot`/`ReviewQueueSnapshotItem` schema. Materialize snapshot membership/order on first page and keyset over items thereafter, returning already-handled placeholders rather than omitting captured rows. Use accessible current/backlog/new-work/handled messages; never materialize an unbounded queue in the circuit.
3. Add manual category finalization, including intentional null-category finalization for Confirmed Uncategorized. Approval/finalization, retry, manual category, and final correction all acquire and hold `AdministratorSessionGuard` operation lease through external I/O and database commit.
4. Update history/edit panel to show `FinalDescription ?? Description`, raw source read-only, and send unfinalized edits to Review.
5. Add final correction command with concurrency token/CAS. It writes description/category atomically only after explicit model success or manual choice; conflict reloads, failures leave final data untouched, typing calls no model.

**Tests:** unchanged approval zero calls; edited submit one; rapid submit no duplicate finalization; failure persists edit; manual finalization; provenance; final correction success/manual/failure/conflict; each user command cancelled by logout/expiry during I/O and before commit; actionability-revision monotonic transition tests; snapshot materialization/order/max/cursor ownership/tampering/expiry tests; Ready→Processing→retry-after-snapshot excluded from old snapshot; other-tab/worker finalization yields exactly-once placeholder rather than duplicate/omission; honest summary; keyboard/busy/error/backlog flow.

**Exit:** Finalized has trustworthy meaning; history cannot create a reviewed description/category mismatch.

## Phase 4 — confirmed reports and finance calendar

1. Consume the validated finance calendar introduced in Phase 1 across Home, Review, Transactions, Settings/scheduler display, and reports; replace finance uses of `DateTime.Now`/`ToLocalTime()` while retaining UTC persistence.
2. Replace `SpendingReport` semantics with named confirmed-spending projection: finalized/reviewed, non-excluded, finance-zone period, final null bucket as Confirmed Uncategorized, grouped by account currency, and compatible currency-plus-category Transactions filter. A mixed-currency month renders separate currency buckets or no aggregate; no displayed or API total crosses currencies.
3. Replace unconditional net-worth contract with currency-safe summary. Preserve per-account amount/freshness; no FX conversion in scope.
4. Add dashboard composer taking health, queue, confirmed spending, and accounts/net-worth and selecting one primary action from the approved matrix. Pages must not duplicate precedence rules.

**Tests:** all finalization/exclusion types; final null category; currency grouping/drill-down parity; month boundaries/DST/host-zone independence using Phase 1 validation; equal/mixed/zero currencies; every primary-action precedence and safe empty state.

**Exit:** labels, totals, dates, and action selection have shared explicit semantics.

## Phase 5 — full action centre, navigation, and accessibility

1. Implement action-centre components from `DashboardState`: untrusted health first, then actionable review, processing non-success state, caught up last.
2. Add Review badge from `ReviewQueueSummary`; no layout-level ad hoc query. Route panels/drill-down to compatible Review, Settings, Transactions state.
3. Render confirmed spending plus text-equivalent breakdown. Health is compact only when trusted; accounts/net worth stay secondary/currency-safe.
4. Implement scoped `ICircuitRefreshCoordinator`/`IStateChangeSignal` exactly as specified: cancellable `PeriodicTimer`, coalesced serialized named loads, monotonic stale-result suppression, `Applied`/`NoChange`/`Busy`/`Cancelled`/`Failed` outcomes, and `IAsyncDisposable` circuit cleanup. `Subscribe` returns a per-consumer `IDisposable`; Home, MainLayout, and Review dispose their own registrations on navigation/component disposal so no callback can target a disposed component. Home/MainLayout share dashboard/queue snapshot; Review uses it plus paged queue loads. Automatic refresh preserves keyed-row/editor focus and uses only polite status messages.
5. Complete design accessibility/responsive criteria: semantic headings, focus/live/busy behavior, text-plus-colour status, 44px controls, chart alternative, 320px single column, reduced motion, wrapping.

**Tests:** component/render all matrix states; badge; coordinator timer/signal coalescing/serialization/stale suppression/outcome mapping/shared snapshot/focus preservation; per-consumer Home/MainLayout/Review unregistration on navigation/disposal with no callbacks afterward; circuit disposal/no callback after teardown; keyboard routes; accessibility regression; 320px/mobile/desktop visual checks for loading/error/disconnected/degraded/syncing/processing/long queue/caught-up/mixed currency, using sanitized fixtures.

## Verification, rollout, and metrics

### Test strategy

- Use pure/database-backed logic with injected `TimeProvider`/finance zone; use two `DbContext` instances for races.
- Use mocked model transport only. Captured request/log assertions never contain real merchant data, identifiers, secrets, raw prompt/response, or upstream errors.
- Run focused tests per phase, then full .NET 10 suite. Add component coverage for interaction timing.
- Manual testing uses disposable/local database and invented merchants only; production data stays out of source control/output.

### Rollout and gates

1. Phase 0 may ship separately after conservative-copy verification.
2. Ship Phases 1–4 behind a dashboard/read-model feature flag or complete them before enabling the full UI. For migrations use coherent quiesced database-plus-data-protection backup and existing authenticated release process.
3. Enable Phase 5 locally first, then production after post-migration health, queue, and confirmed-report parity checks. Preserve authentication and record README unauthenticated-denial verification.
4. Observe normal sync, approval, manual category, recoverable enrichment failure, and degraded/recovery paths with sanitized checks. Presentation rollback is safe; schema rollback requires explicit tested recovery.

### Aggregate-only observability

| Metric | Purpose | Guardrail |
| --- | --- | --- |
| Sync outcome/duration by trigger and safe reason | Detect degraded/failed/overlap behavior | No raw bridge error or credential. |
| Time since healthy data and unresolved-gap count | Detect false trust | No account identity. |
| Queue count/age by enrichment state | Detect stuck work and precedence | No transaction text. |
| Enrichment attempt/outcome/latency/manual-finalization count | Detect proposal availability | No prompts, responses, descriptions. |
| Confirmed-report reconciliation aggregate | Detect incorrect inclusion | Aggregate only. |
| Primary panel/action activation by state | Validate action centre | State only. |
| Accessibility/render pass rate | Prevent UI regression | Sanitized fixtures. |

Success is not merely loading the dashboard: matrix tests show no false Healthy/caught-up state, typing makes zero model calls, unfinalized transactions never enter confirmed spend, unlike currencies are never summed, and diagnostics contain no raw financial content.

## Implementation handoff

Start with Phase 0 only if immediate presentation improvement is wanted. For the full redesign follow the dependency order exactly: sync truth, durable proposal/final state, review/correction integrity, confirmed reports/finance calendar, then composed dashboard UI. Never merge a dashboard claim ahead of its projection, and do not reschedule the completed OpenAI migration/deployment work.
