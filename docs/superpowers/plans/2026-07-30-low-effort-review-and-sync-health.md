# Low-effort transaction review and sync health — implementation handoff

**Status:** Planned; do not begin a task until its predecessor has a GPT-5.6 Sol review.

**Current release prerequisite:** Application-level administrator authentication is already
implemented and tested. Every future deployment must preserve it and follow the current
[production authentication cutover checklist](../../../README.md#production-authentication-cutover-checklist)
for backup, restore, rollback, and access verification.

**Primary outcome:** Plutus becomes an approve-the-agent expense workflow. It accepts SimpleFIN data independently, proposes a clean description and category, requires only a short user confirmation, and never misrepresents incomplete SimpleFIN data as healthy.

**Reference design:** `docs/superpowers/specs/2026-07-30-low-effort-review-and-sync-health-design.md`

## Delegation contract

- Implementation: GPT-5.6 Terra, high reasoning (the closest available substitute for the requested GPT-5.5 Thinking).
- Independent code and acceptance review: GPT-5.6 Sol.
- Orchestrator: plans, assigns bounded tasks, consolidates review findings, and never writes implementation code.
- Every implementation task begins with a clean worktree, a `git fetch`/integration check, focused automated tests, and a review before the next dependent task begins.

## Milestone -1 — OpenAI provider migration

This migration is intentionally provider-only: it makes `OPENAI_API_KEY` the sole model credential without changing the review workflow yet.

- [ ] **-1.1 Replace the SDK and configuration boundary** — Replace Anthropic package references, client registration, options, categorizer implementation, tests, active docs, container environment wiring, and deployment configuration with OpenAI equivalents. Rename `Plutus:Claude:Model` to `Plutus:OpenAI:Model`; use `gpt-5.6-luna` as the configurable default.
  - Preserve `ICategorizer` and its category/note/confidence contract. Use the official OpenAI .NET SDK's Responses API with `store: false` and a strict category-enum JSON-schema output. Every request sets the configured model, low reasoning effort, and a 256-token output limit; no live model calls are permitted in tests.
  - Migrate these active surfaces only: `src/`, `tests/`, project files, `appsettings*`, `docker-compose.yml`, `docker-compose.dev.yml`, README, and CLAUDE.md. Historical dated plans/specs retain their original provider references. `OPENAI_API_KEY` is read only at runtime and never committed or logged.
  - Tests: captured mocked-transport assertion for model/Responses endpoint/`store:false`/strict schema/reasoning/output limit; category schema constraint; valid/invalid/unknown/refusal/cancellation/provider-error responses; confidence bounds; model configuration binding; missing OpenAI key failure message; and full Docker test suite.

- [ ] **-1.2 Independent provider-migration review** — GPT-5.6 Sol verifies no Anthropic runtime/configuration path remains, no secret can enter the repo or logs, structured category output is still constrained, documentation and Docker use `OPENAI_API_KEY`, and tests do not call the live API.

- [ ] **-1.3 Controlled deployment cutover** — Retain a known-good **authenticated** image and its secure environment configuration as rollback. Provision `OPENAI_API_KEY` only in the gitignored host secret environment file; verify a non-empty in-container value with a no-output presence check. Recreate the container, run one real sanitised categorization smoke check with an invented merchant string, then remove the obsolete Anthropic key only after success.
  - Never use `docker compose config` or a command that prints the resolved key. If startup/smoke fails, restore only a known-good authenticated image/configuration. The pre-authentication image must remain offline or behind temporary reverse-proxy access control until an authenticated replacement is running; follow the current README runbook.

## Milestone 0 — truthful and serialized SimpleFIN sync

This milestone goes first because the agent workflow cannot be trusted if the underlying transactions are silently stale or partial.

- [x] **0.0 Access-control foundation and verification plan** — Application-level administrator authentication and automated unauthorized-access coverage are already implemented. Before every public deployment, preserve that boundary and record the README runbook's unauthenticated-denial verification.
  - Guardrail: TLS is not an authorization mechanism; do not place credentials in source, tests, or task output.

- [ ] **0.1 Sync domain and migration** — Extend `SyncRun` (or a related run-detail model) with outcome, trigger, timing, safe reason codes, a last-healthy timestamp, a completed-fetch cursor, and bounded recovery-window metadata. Add the EF migration and deterministic tests for `Success`, `Degraded`, `Failed`, and `Skipped`.
  - Touch points: `SyncRun`, `SimpleFinConnection`, EF configurations/migrations, `SyncServiceTests`.
  - Guardrail: never persist access URLs, credentials, or raw merchant descriptions in diagnostics.

- [ ] **0.2 Response classification and recovery semantics** — Change `SimpleFinClient`/`SyncService` so SimpleFIN `errors[]`, missing expected logical accounts, and stale returned data yield `Degraded`; keep the last-healthy timestamp unchanged for degraded/failed results.
  - Define expected accounts after `AccountMatcher` resolves re-auth IDs: none on first successful connection; explicit active accounts thereafter; Settings-controlled retirement only.
  - Use the existing `StaleAfterHours` setting with validation. Ingest partial returned data safely, then replay a bounded recovery window rather than using the health timestamp as an unbounded fetch cursor. Require explicit backfill once the known gap exceeds that window.
  - Tests: partial bridge response, error payload, stale account data, retired account, first sync, reminted ID, malformed payload, persistence failure, bounded-recovery boundary.

- [ ] **0.3 Sync coordinator and scheduler recovery** — Introduce one coordinator for manual, scheduled, startup, and recovery triggers; prevent overlap, add bounded retry for transient failures, and make local-time startup/DST behavior deterministic.
  - Tests: manual/scheduled collision, retry exhaustion, startup exception containment, auth failure classification.

- [ ] **0.4 Safe reconnect workflow** — Run credential validation and replacement through the same coordinator as sync; validate a replacement setup token before discarding a known-good connection, atomically swap it after validation, then run a deduplicated recovery sync after success.
  - Treat claim failure, malformed/empty data, and 401/403 as validation failures that retain the old credential. Treat an authenticated parseable response with warnings/stale/missing data as a valid-but-degraded replacement: swap safely, record `Degraded`, and start bounded recovery.
  - Tests: invalid reconnect retains old connection; valid-but-degraded validation swaps and records degradation; successful reconnect runs recovery and preserves dedupe; scheduler/manual collision during validation observes one coherent connection state.

- [ ] **0.5 Sync health UI** — Replace ambiguous all-clear status with Healthy/Attention/Failed/Not connected; show affected-account freshness, a labelled Sync action, posted-only semantics, and Settings details.
  - Verification: normal, degraded, failed, reconnect-required, and already-running states.

## Milestone 1 — proposal/final transaction foundation

- [ ] **1.1 Transaction state and migration** — Add proposal/final description fields, proposal category/confidence timestamps, enrichment status/attempt/error fields, retry timestamp, processing lease/version, queue index, and a pure finalization helper. Implement the canonical `Pending → Processing → Ready/Failed → Finalized` state machine; transfers use terminal `SkippedTransfer`. Migrate legacy pending suggestions and transfers using the design's explicit mapping table without data loss.
  - Tests: migration from an actual legacy SQLite schema, reviewed/unreviewed/manual-edit/uncategorized/transfer mappings, allowed/forbidden state transitions, raw-description immutability, transfer skip, finalization idempotence, and safe error normalization.

- [ ] **1.2 Structured enrichment boundary and unavailable mode** — Add `ITransactionEnricher` and its OpenAI implementation with a constrained `description/category/confidence` schema. Keep the agent prompt factual and injection-resistant; add a no-op/unavailable implementation when no API key is configured so the app and bank sync still start.
  - Tests: valid schema, invalid/empty JSON, unknown category, missing categories, unavailable-key composition-root startup, sanitized failure, and no raw description/account-last-four in logs or prompts.

- [ ] **1.3 Durable enrichment worker** — Remove OpenAI work from the bank-ingestion critical path. Persist transactions as `Pending`, have a bounded hosted worker claim/process work with compare-and-set leases, retry transient agent failures, and skip transfers/finalized rows.
  - Tests: bank sync succeeds with agent offline, retries cap, expired-lease recovery, worker/review race across separate contexts, finalization wins, transfer bypass, restart-safe processing.

## Milestone 2 — one-action review and trustworthy history

- [ ] **2.1 Guided Review page** — Replace the routine category dropdown with an editable agent description, category chip, one-click approval, explicit edited-description categorization, hidden manual-category escape hatch, retry state, and keyboard flow.
  - Tests: unchanged approval makes no model call; one edit makes exactly one call on submit; failure preserves edit; manual override makes none. Add component-level coverage because interaction timing is core behavior.

- [ ] **2.2 Transaction history and corrections** — Display `FinalDescription ?? Description`, expose raw source text read-only in the slide-over, and allow a description correction to explicitly re-categorize unless the user chooses a manual category.
  - Tests: display fallback, raw provenance, manual category wins, corrected description behavior.

- [ ] **2.3 Report integrity** — Update report queries and dashboard cards to use final categories only; assert that pending proposals are excluded and finalized transactions appear immediately after approval.
  - Tests: reviewed/unreviewed/transfer combinations and month-boundary cases.

## Milestone 3 — dashboard around daily action

- [ ] **3.1 Action-first dashboard** — Make Review the primary action when pending work exists, add its navigation badge, show caught-up state when empty, and preserve useful category drill-down.

- [ ] **3.2 Spending and health hierarchy** — Present reviewed spending as the main financial snapshot, place data health before secondary account/net-worth detail when degraded, and keep the layout usable on the existing mobile viewport.

- [ ] **3.3 Visual acceptance pass** — Validate first-run, no connection, syncing, agent failure, empty review queue, long queue, degraded sync, and mobile layouts against the design. No new dashboard/reporting features are added in this task.

## Release gates

1. The existing suite plus all new focused tests pass using .NET 10.
2. A GPT-5.6 Sol reviewer finds no unresolved correctness, privacy, migration, or concurrency issue.
3. Manual verification uses a disposable/local database and sanitized representative merchant strings; no production credentials or financial data enter source control or task output.
4. Deployment starts with a non-destructive database backup and migration check. The released dashboard visibly reports the first post-deploy sync outcome.
5. The implemented application-level access control has a recorded unauthenticated-denial check before deployment.
