# Dashboard action center — approved design

**Date:** 2026-07-31
**Status:** Approved implementation baseline; independent-review revisions incorporated
**Scope:** Dashboard, navigation, review/report projections, and the supporting sync/enrichment semantics required to make their claims true.

## Status, authority, and precedence

This is the current dashboard design authority. It refines [`2026-07-30-low-effort-review-and-sync-health-design.md`](2026-07-30-low-effort-review-and-sync-health-design.md), which remains authoritative for its canonical enrichment and sync state machines except where this document makes a dashboard-specific decision.

The June dashboard documents are historical records, not implementation instructions: [`2026-06-08-dashboard-insights-design.md`](2026-06-08-dashboard-insights-design.md) and [`../plans/2026-06-08-dashboard-insights.md`](../plans/2026-06-08-dashboard-insights.md). Existing transfer exclusion remains valid; their equal-weight dashboard hierarchy, unconditional net-worth aggregation, and pre-review spending display are superseded here.

The OpenAI provider migration and its authenticated production deployment are complete. This design does **not** schedule, redo, or make deployment a prerequisite for that migration. Application authentication remains an existing release gate. Plutus currently has one application replica; all in-process coordination described here is single-replica only and must be replaced with shared coordination before scale-out.

## Product outcome

The dashboard is an action centre, not a wall of equal-weight reports. It tells the user, in order: what needs action, whether data is trustworthy, confirmed spending, and then compact account context. It must never turn missing information into an all-clear message: `Pending`/`Processing` enrichment is not caught up, a warned bridge response is not healthy, and unlike currencies do not form a net-worth total without FX.

## Decisions and invariants

| Concern | Decision |
| --- | --- |
| Primary action | Sync/data-health wins whenever disconnected, failed, degraded, known-gap, or currently syncing. Otherwise actionable Review wins. |
| Review queue | `Ready` plus manually recoverable `Failed` are actionable. `Pending`/`Processing` are visible processing work, not ready or caught up. `SkippedTransfer` is excluded. |
| Spending | Only finalized/reviewed transactions contribute. Finalized null category is **Confirmed Uncategorized**; unfinalized null category is excluded. Existing `ExcludeFromSpending` categories remain excluded. Every spending aggregate is partitioned by transaction-account currency; no cross-currency total exists without separately designed FX. |
| Dates | Finance date grouping, relative dates, monthly windows, and local scheduler display use validated `America/New_York` by default. Persist UTC; inject clock and zone in tests. |
| Raw provenance | `Transaction.Description` is immutable bank evidence. `FinalDescription ?? Description` is display text. Dedupe/transfer detection continue to use raw `Description`. |
| Corrections | Unfinalized edits go to Review. A finalized correction stays reviewed only after one atomic successful description/category update or explicit manual selection. No model call on typing. |
| Model privacy | Requests may include only a bounded request view of raw description and minimal allowed enrichment data; account identifiers are omitted by default. Raw descriptions, prompts, response bodies, credentials, and raw errors never enter logs. |
| Accounts | Per-account balance retains currency. Portfolio total/assets split is shown only when all included balances share one currency or a separately designed FX conversion exists. |

## Information architecture and state matrix

The first significant region is exactly one `Primary action` panel. It may include explanatory detail, but not competing primary CTAs. A compact health summary remains below it when Review is primary.

| Connection/data-health state | Review/enrichment state | First panel and action | Supporting copy/routes | Spending/accounts treatment |
| --- | --- | --- | --- | --- |
| No connection | Any | **Connect an account** → Settings | No bank connection; spending appears after posted transactions sync. | No confirmed-spending claim; account setup only. |
| Credential/reconnect required | Any | **Reconnect account** → Settings | State credential action, not data-health success. | Prior reports are secondary and marked last confirmed data. |
| Sync in progress/coordinator busy | Any | **Sync in progress**; no second sync action | Show safe trigger detail and a details route. | Existing confirmed data is “last confirmed,” not current. |
| Failed sync | Any | **Fix sync issue** → Settings/health | Safe reason, last healthy time where known, posted-only disclosure. | Secondary, based on last confirmed data; show freshness. |
| Degraded sync or known recovery gap | Any | **Review sync health** → Settings/health | Affected accounts/reason, earliest possibly incomplete date, recovery/backfill choice. | Secondary; never Healthy/current. |
| Healthy/complete sync | `Ready > 0` | **Review _n_ transactions** → Review | Ready count; mention recoverable failures as supporting detail. | Confirmed monthly spending then compact healthy status. |
| Healthy/complete sync | Ready zero, recoverable Failed > 0 | **Resolve _n_ review issue(s)** → Review | Agent work failed; retry/manual category available. | Confirmed spending follows; no caught-up state. |
| Healthy/complete sync | Ready/Failed zero, Pending + Processing > 0 | **Preparing _n_ transactions** | Do not claim work is done; link processing detail if useful. | Show confirmed spending, never “All caught up.” |
| Healthy/complete sync | no actionable or processing work | **You’re caught up** | “No transactions need review.” It is not proof of a new sync. | Confirmed spending then compact health. |
| No transactions after healthy sync | no review work | **You’re caught up** with neutral empty explanation | No posted expenses available for the period. | Empty confirmed spending and allowed account context. |

**Definitions:** Untrusted means no connection, credential action, active sync, failed/degraded latest relevant outcome, or unresolved gap. It outranks Review. Healthy/complete is an explicit health projection, never inferred from HTTP completion, missing runs, or balances. Caught up means no actionable **or in-flight** review work after health is trusted.

### Navigation

Order remains Dashboard, Review, Transactions, Settings. Review receives an accessible badge for `Ready + manual-recoverable Failed`; it is absent at zero and excludes Pending/Processing. Its name is, for example, “Review, 3 transactions need attention,” not an unexplained numeral. Settings owns connection recovery and health detail. Category drill-down routes to Transactions with an explicit compatible filter.

## Visual and interaction behavior

```text
Dashboard title + finance-date context
┌ Primary action / condition ──────────────────────────────────────┐
│ state headline · one primary action · factual detail              │
└──────────────────────────────────────────────────────────────────┘
┌ Confirmed spending — this month ─┐  ┌ Sync health / accounts ────┐
│ total + top category / empty      │  │ compact status + route     │
│ category breakdown → transactions │  │ no competing primary CTA   │
└──────────────────────────────────┘  └────────────────────────────┘
┌ Accounts / net worth (secondary and currency-safe) ──────────────┐
└──────────────────────────────────────────────────────────────────┘
```

- The primary panel has a state-specific icon, label, headline, safe detail, and one prominent action. Meaning is text plus icon, never colour alone. Healthy is compact, not a dominant success card.
- Confirmed spending identifies its finance-zone month and currency. With multiple currencies, it renders one currency bucket per currency (or no aggregate), never one combined figure. Empty states distinguish “No confirmed spending yet” from transactions being prepared; neither means “No spending.”
- The breakdown shows **Confirmed Uncategorized** where applicable. Each row/bar is a keyboard-operable link with category, amount, accessible text, and a matching filtered transaction view.
- Account cards show a safe display name, balance, ISO currency, and freshness. A shared-currency portfolio may show total/assets/liabilities. Mixed currencies show grouped balances/subtotals and “Balances are shown by currency; no total is calculated.”
- Review foregrounds editable proposed/final display text; raw description, finance-zone date, account label, and amount are evidence. Unchanged Ready approval makes no second model request. Edited submission makes at most one request; failure retains the edit and manual/retry paths.
- History displays `FinalDescription ?? Description`, makes raw `Description` read-only, and routes unfinalized edits back to Review. Finalized correction uses the atomic operation below; it cannot leave changed display text with an old category while reviewed.

## Data and API projections

Pages do not independently reconstruct trust or reporting rules. Introduce read/query boundaries; names are guidance, contracts are required.

| Projection/operation | Required contract | Consumers |
| --- | --- | --- |
| `DashboardState` | `PrimaryAction`, `SyncHealth`, `ReviewQueueSummary`, finance-local period, `ConfirmedSpendingSummary`, `AccountsSummary`; derived from a consistent snapshot where practical. | Home and component tests. |
| `SyncHealthSummary` | Connection/current coordinator state; latest outcome/trigger; last healthy/attempt; safe reasons; affected safe account names; earliest possible gap; recovery capability. Healthy is explicit, not latest Success. | Home, Settings, health detail. |
| `ReviewQueueSummary` | Ready, recoverable Failed, Pending, Processing counts and optional oldest actionable date from `EnrichmentStatus`, not merely `IsReviewed`. | Primary selector, nav badge, Review header. |
| `ConfirmedSpendingReport` | Finance-zone `[startUtc,endUtc)`; finalized/reviewed only; excluded categories omitted; final null category labelled Confirmed Uncategorized; groups totals/categories by the source account's currency; compatible currency-plus-category drill-down filter. | Dashboard/chart/Transactions. |
| `AccountSummary` | Safe label, balance, currency, balance date/freshness. | Compact account area. |
| `NetWorthSummary` | Comparable total only for one currency; otherwise by-currency subtotals and `AggregationUnavailable`. No implicit conversion. | Secondary area. |
| `FinalizeProposal` | Compare-and-set reviewable row to final description/category/review timestamp/status; reject stale/terminal state without partial write. | Approval/manual category. |
| `CorrectFinalizedTransaction` | One atomic update of final description/category after explicit model success or manual selection; failed model request leaves final state untouched. | History edit panel. |
| `ReviewQueuePage` | Server-bounded actionable rows, stable order, opaque keyset cursor, snapshot marker, `hasMore`, aggregate `ReviewQueueSummary`, and safe refresh/outcome state. | Review page and accessibility tests. |

### Persistence and query alignment

Build on the July design fields: suggested/final descriptions/categories, `ReviewedAt`, `EnrichmentStatus`, attempts, error, retry, and lease/version. `CategoryId`, `IsCategorized`, and `IsReviewed` remain compatibility representation until a separately designed consolidation. The dashboard predicate is:

```text
confirmed = IsReviewed && EnrichmentStatus == Finalized
            && transaction lies in the finance-local period
            && category is not ExcludeFromSpending

all grouping/totals are additionally partitioned by Account.Currency

confirmed null category = confirmed && CategoryId is null
unfinalized null category = excluded
```

Transfers use `SkippedTransfer` and existing transfer exclusion; they are neither review work nor confirmed spend. The July migration/state-machine tests must protect legacy rows before this becomes the report source of truth.

### Finance-time boundary

Add validated `Plutus:Finance:TimeZoneId`, default `America/New_York`, resolved through an injectable finance calendar/clock with `TimeProvider`. Do not use `DateTime.Now`, `ToLocalTime()`, or host local zone for finance periods, review/history labels, or scheduler-local display. Persist UTC. Convert local period boundaries to UTC at the boundary. Invalid/missing zone fails configuration validation with a safe startup error, not a host-zone fallback.

### Privacy boundary

The existing OpenAI provider remains sole provider. Enrichment receives only permitted raw description, amount/date when needed, and category names. Exclude account identifiers/numbers/last-four, balances, account history, notes, credentials, raw prompt/response, and raw upstream error by default. Persist/log bounded reason codes and aggregates only. Capture logs and payloads in tests to prove it.

#### Bounded enrichment request view

Raw `Transaction.Description` remains immutable evidence and must never be copied into a log, exception, diagnostic record, UI refresh payload, or a general-purpose DTO. The sole path permitted to receive it is a pure `EnrichmentRequestBuilder`, called immediately before the provider boundary. It creates a `BoundedEnrichmentRequest`; the provider accepts that type rather than `Transaction` or an unbounded string.

- Preserve the stored raw description byte-for-byte. The request view may contain at most 512 Unicode scalar values and 2,048 UTF-8 bytes. Truncate only on a scalar/UTF-8 boundary and append a fixed, budgeted truncation marker; do not split a surrogate pair or invalid UTF-8 sequence.
- The allowed-category input has at most 32 categories. Each name has at most 64 Unicode scalar values and 256 UTF-8 bytes; the complete category schema/list has an 8,192-byte UTF-8 budget. A category set that cannot fit is rejected with a bounded `CategoryInputTooLarge` outcome rather than silently dropping allowed categories.
- The complete application-controlled request view (description, amounts/dates, category schema, and fixed instructions) has a 12,288-byte UTF-8 budget. Builder failure produces a safe, retryable/unavailable enrichment outcome with a reason code, never a raw exception message.
- The model prompt treats description as untrusted data and ignores embedded instructions. The raw description is neither normalized nor altered in storage; only the bounded derived view is sent. Account identifiers are not an input field.

Tests cover exact boundaries, multi-byte/rune truncation, oversize category sets/names, malformed or malicious instruction-like descriptions, and an assertion that only `BoundedEnrichmentRequest` reaches the provider. Captured request, failure, and log data must contain neither an unbounded raw description nor an account identifier.

### Review queue paging

Review is server-paged, never an unbounded client list. `ReviewQueuePage` returns at most 25 actionable rows and a separately calculated aggregate summary. Its keyset order is `ActionableAt DESC, TransactionId DESC`; `ActionableAt` is set once when a row first becomes Ready or manually recoverable Failed, and is not recomputed during rendering. The opaque cursor contains only the last ordering key plus a server-issued snapshot marker; callers cannot choose a larger page size.

The first request establishes a `snapshotAt` cutoff. Load-more queries retain that cutoff and fetch only rows actionable at or before it, then apply the current actionable-state predicate. Rows finalized after the first page may disappear; new/retried work after the cutoff is not silently interleaved and is reported by the summary as “new review work is available.” The client deduplicates by transaction ID as a defensive rendering check and resets to the first page after an explicit refresh or a changed snapshot marker. `hasMore` is derived from the keyset query, not from the aggregate count.

Review exposes concise accessible backlog messages: e.g. “25 of 61 transactions ready for review; load more available,” “3 transactions are being prepared,” and “New review work is available; refresh list.” Counts are summaries, not a promise that a paged snapshot is immutable. Tests cover stable ordering, fixed maximum, cursor tampering/rejection, load-more duplicate/omission resistance when a worker changes state or another tab finalizes a row, and honest count/message updates.

### User operations and background work

Every user-triggered state-changing operation acquires an `AdministratorSessionGuard` operation lease before I/O and holds it through the external call (where applicable), database transaction, and commit: manual sync, reconnect, proposal approval/finalization, retry, manual category selection, and final correction. All database/remote calls receive the lease cancellation token. Logout, expiry, or password rotation between initiation and commit cancels the operation and prevents a partial or post-revocation write; the UI reports a neutral reload/sign-in outcome.

The enrichment worker is not a user operation and never acquires a user session lease. It uses hosted-service shutdown cancellation plus its own durable compare-and-set lease/state checks. Tests simulate logout/expiry during each user operation's I/O and immediately before commit, alongside worker/finalizer races, to prove no operation survives a revoked session and no finalization is partial.

### Circuit refresh boundary

Introduce a scoped, per-InteractiveServer-circuit `ICircuitRefreshCoordinator` shared by Home, MainLayout, and Review. It owns a cancellation-aware `PeriodicTimer` plus subscriptions to an in-process `IStateChangeSignal` published by sync coordination, enrichment transitions, and committed user commands. It is `IAsyncDisposable`; circuit disposal cancels timer/subscriptions/loads and prevents callbacks after navigation.

The coordinator coalesces signals, serializes each named load with `SemaphoreSlim`, and assigns monotonically increasing request IDs. Only the most recent non-cancelled result may update the immutable dashboard/queue snapshot; late results are discarded. A refresh command returns one of `Applied`, `NoChange`, `Busy`, `Cancelled`, or `Failed` with safe display detail. Home and navigation consume the same queue/health snapshot; Review consumes that summary plus its `ReviewQueuePage` query through the coordinator.

Automatic refresh never moves focus. Render keyed review rows by transaction ID, retain active editor drafts, and announce only a short polite “new work available”/“data refreshed” message when appropriate; user-initiated action controls retain or deliberately restore focus. Tests cover timer/signal coalescing, serialized loads, stale-result suppression, disposal cancellation/no post-dispose callback, outcome mapping, shared Home/nav consistency, and focus preservation across automatic refresh.

## Accessibility and responsive acceptance

- Semantic heading order: page, primary-action, sections. Use live status only for actual state transitions; loading must not reannounce the whole page.
- Controls and drill-down links have visible focus, text names, 44×44 CSS-pixel targets, and disabled reasons. Replace or enlarge the icon-only sync control.
- Status is text/icon plus colour. Chart values have a text/table alternative. Badge has accessible meaning; async refresh never steals focus.
- Labels, errors, busy state, retry outcome, and manual category controls are programmatically associated; model processing never traps focus.
- At 320px the order is action, confirmed spending, health, accounts. Wider screens may use two secondary columns. No horizontal scrolling or clipped amounts; long category/account/reason/date text wraps.
- Respect reduced motion and contrast requirements; spinners include text/busy semantics.

## Acceptance criteria

1. Every matrix row has its specified first panel and exactly one primary route/action.
2. Untrusted/unknown states are never labelled Healthy, All clear, or Caught up.
3. Pending/Processing prevents caught-up but does not inflate ready badge; Ready/recoverable Failed does.
4. Confirmed spend includes finalized categorized and null-category rows, excludes all unfinalized and transfer/excluded rows.
5. Dashboard total, chart, drill-down, and Transactions filter share period/finalization predicate.
6. `America/New_York` dates/months are deterministic across DST and host zones.
7. Raw description remains unchanged through approval/correction; dedupe/transfer detection still use it.
8. A finalized edit cannot remain reviewed without atomic category update/manual choice.
9. Typing makes zero model calls; raw financial content and account identifiers do not enter captured logs/default payloads.
10. Spending and net-worth aggregates are currency-partitioned or omitted; unlike currencies are never summed, and every account amount discloses currency/freshness.
11. The only model input path has the specified scalar/UTF-8/category/total budgets, preserves raw evidence, rejects unsafe category input safely, and logs neither raw description nor account identifier.
12. Review paging is server-bounded to 25, keyset-stable, and reports concurrent changes without duplicate/omitted rendered rows or an inaccessible backlog message.
13. Each listed user state change holds an administrator operation lease through I/O and commit; cancellation/revocation cannot leave a partial write. Workers remain outside user-session coordination.
14. Shared circuit refresh is cancellable/disposable, serializes loads, suppresses stale results, has defined command outcomes, and never steals focus.
15. Keyboard-only and 320px journeys complete every primary action without information loss.
