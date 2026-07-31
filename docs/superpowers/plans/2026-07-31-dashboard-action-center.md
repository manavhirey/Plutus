# Dashboard action center — implementation handoff

**Date:** 2026-07-31
**Status:** Approved plan; implementation not started
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
Quick visual pass (presentation-only; independent) ──────────────┐
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

Do not ship a full state assertion or confirmed-spending label before its projection can prove it. The quick pass is independent only because it conservatively rearranges existing data.

## Phase 0 — quick visual pass (safe now)

**Purpose:** Improve hierarchy/navigation without changing sync, enrichment, reporting, persistence, API, or data semantics.

**Allowed:** Presentation, CSS, layout, link placement, accessible labels, and wording accurate for current data. **Forbidden:** New health classification, changed report predicate, confirmed-total claim, model call, worker, migration, or a pending-is-caught-up statement. This phase must not call legacy `Success` “Healthy” or current spending “Confirmed.”

### Exact implementation checklist

- [ ] In `Home.razor`, put existing review content before sync, spending, net worth, and accounts. With legacy unreviewed rows, give it the sole prominent “Review transactions” route and neutral text “Transactions awaiting review.”
- [ ] With legacy review count zero, replace “All clear” with “No transactions currently awaiting review.” Do not say caught up because legacy state cannot distinguish pending/processing.
- [ ] Reword current sync card as **Latest sync attempt**. Preserve literal Success/Failed and timestamp; never label Success Healthy. Retain Settings/setup route.
- [ ] Reword chart/summary as **Spending by category (current categorization)** or equivalent. Do not call it reviewed/finalized/confirmed. Preserve query and click behavior.
- [ ] Move net worth/accounts below review/sync/spending as secondary content. Retain values and currency on every account; do not strengthen aggregate claims or change calculation.
- [ ] Keep Review immediately after Dashboard. Do not add a dynamic count badge until it has one shared `ReviewQueueSummary`; an ad hoc layout query or inaccurate badge is forbidden.
- [ ] Replace/enlarge the icon-only sync affordance or provide visible label, focus, busy text, disabled reason, and 44×44 target. Do not alter invocation/coordination.
- [ ] Use existing CSS tokens/responsive grid. Review content is first in DOM/tab order; no horizontal scroll at 320px.
- [ ] Add focused render/UI checks only for wording, route, DOM order, keyboard name, and responsive hooks. Do not change legacy sync/report assertions.
- [ ] Manually check no connection, zero/nonzero legacy review rows, latest Success, latest Failed, and 320px with sanitized/disposable data.

**Exit:** Review-first, keyboard/mobile improvement with no semantic overclaim. It can deploy independently after normal tests/review.

## Phase 1 — truthful sync-health foundation

Implement July 30 Milestone 0 before full dashboard health use:

1. Add Success/Degraded/Failed/Skipped, trigger/duration/window, safe reason codes, last-healthy watermark, completed cursor, account observations, and recovery metadata. Test EF migration from pre-feature SQLite.
2. Classify after logical account matching. Warnings, missing active expected accounts, and stale balance data are Degraded; partial data ingests; degraded/failed/skipped never advance healthy watermark. Bound recovery and require explicit backfill after window.
3. Create one single-replica coordinator for manual, scheduled, startup, reconnect, and recovery; report contention rather than overlapping SQLite writes. Add bounded transient retry and deterministic `America/New_York` scheduler/local-date behavior.
4. Put reconnect validation/replacement through it. Preserve known-good credential on invalid replacement; safely swap a valid-but-degraded one and recover.
5. Add `SyncHealthSummary`; update Settings then Home for Not connected/Attention/Failed/syncing, posted-only semantics, and resolution routes.

**Tests:** migration/state fixture; first/reminted accounts; warning/missing/stale/retired/malformed/persistence; recovery/backfill boundary; coordinator collision/retry/auth; DST; health projection state coverage.

**Exit:** Home consumes explicit health projection; no `lastRun.Status == Success` health inference.

## Phase 2 — proposal/final transaction foundation

Implement July 30 Milestone 1:

1. Add proposal/final fields, `EnrichmentStatus`, retry/error/attempt, CAS lease/version, and queue indexes. Implement Pending/Processing/Ready/Failed/Finalized/SkippedTransfer and exact conservative legacy migration.
2. Add `ITransactionEnricher` with no-key/unavailable mode. Provider migration is done: do not modify or re-deploy it. Payload/log tests prove raw description is not logged and identifiers are omitted by default.
3. Remove model work from bank ingest with durable bounded worker; new non-transfer rows persist Pending. Require leases, retry, stale-lease recovery, separate-context tests, and finalization-wins.
4. Add `ReviewQueueSummary` from status. Define manual-recoverable failure and restrict future badge to Ready + those failures.

**Tests:** actual legacy upgrade; migration/state matrix; raw immutability/transfers; unavailable startup; offline/retry/lease/restart worker; two-context race; prompt/log capture; queue summary.

**Exit:** review state is meaningful beyond `IsReviewed`; bank sync works without the model.

## Phase 3 — review and correction integrity

1. Replace `Review.razor` bind-time `RefineFromNoteAsync` with guided proposal review. Unchanged approval makes zero model calls. Edited explicit submit makes at most one, exposes busy state, and leaves a recoverable failure unreviewed.
2. Add manual category finalization, including intentional null-category finalization for Confirmed Uncategorized.
3. Update history/edit panel to show `FinalDescription ?? Description`, raw source read-only, and send unfinalized edits to Review.
4. Add final correction command with concurrency token/CAS. It writes description/category atomically only after explicit model success or manual choice; conflict reloads, failures leave final data untouched, typing calls no model.

**Tests:** unchanged approval zero calls; edited submit one; rapid submit no duplicate finalization; failure persists edit; manual finalization; provenance; final correction success/manual/failure/conflict; keyboard/busy/error flow.

**Exit:** Finalized has trustworthy meaning; history cannot create a reviewed description/category mismatch.

## Phase 4 — confirmed reports and finance calendar

1. Add validated finance options/calendar with `America/New_York` default. Replace finance uses of `DateTime.Now`/`ToLocalTime()` in Home, Review, Transactions, Settings/scheduler display, and reports. Keep UTC persistence.
2. Replace `SpendingReport` semantics with named confirmed-spending projection: finalized/reviewed, non-excluded, finance-zone period, final null bucket as Confirmed Uncategorized, and compatible Transactions filter.
3. Replace unconditional net-worth contract with currency-safe summary. Preserve per-account amount/freshness; no FX conversion in scope.
4. Add dashboard composer taking health, queue, confirmed spending, and accounts/net-worth and selecting one primary action from the approved matrix. Pages must not duplicate precedence rules.

**Tests:** all finalization/exclusion types; final null category; drill-down parity; month boundaries/DST/host-zone independence/invalid config; equal/mixed currencies; every primary-action precedence and safe empty state.

**Exit:** labels, totals, dates, and action selection have shared explicit semantics.

## Phase 5 — full action centre, navigation, and accessibility

1. Implement action-centre components from `DashboardState`: untrusted health first, then actionable review, processing non-success state, caught up last.
2. Add Review badge from `ReviewQueueSummary`; no layout-level ad hoc query. Route panels/drill-down to compatible Review, Settings, Transactions state.
3. Render confirmed spending plus text-equivalent breakdown. Health is compact only when trusted; accounts/net worth stay secondary/currency-safe.
4. Complete design accessibility/responsive criteria: semantic headings, focus/live/busy behavior, text-plus-colour status, 44px controls, chart alternative, 320px single column, reduced motion, wrapping.

**Tests:** component/render all matrix states; badge; keyboard routes; accessibility regression; 320px/mobile/desktop visual checks for loading/error/disconnected/degraded/syncing/processing/long queue/caught-up/mixed currency, using sanitized fixtures.

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
