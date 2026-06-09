# SimpleFIN re-auth: prevent & heal duplicate accounts/transactions — design

**Date:** 2026-06-09
**Status:** Approved (design); pending implementation plan

## Problem

After the user re-authenticated their bank connections in the SimpleFIN bridge, the
bridge issued a **brand-new account ID** (`ACT-…`) for each re-linked account. Plutus
identifies accounts by `Account.SimpleFinAccountId` (upsert key) and transactions by
`Transaction.SimpleFinTransactionId` (dedupe key). Because **both IDs change on re-auth**,
the re-fetched data was inserted as new rows instead of updating the existing ones.

### Observed state (live DB, 2026-06-09)

- Accounts **6 → 11**: five accounts duplicated (old stale row + new fresh row) — Amex Blue
  Cash, Chase College, Chase Freedom, Chase Sapphire, Savor. BofA was not re-authed and is
  unaffected (same ID).
- **Net Worth inflated to $98,940.13** (should be ~$53,847.32; the stale duplicate Chase
  College double-counts +$46,126).
- Transactions **113 → 123**: only **one** is a true cross-account duplicate (a $2.40 MBTA
  charge, Plutus ids 45 on old acct 4 and 121 on new acct 10). The other 9 are genuinely new
  June 7–8 activity that landed on the new account rows.

Duplicate account pairs (old row with history ↔ new row with fresh balance):

| Account | old id | new id |
|---|---|---|
| Blue Cash Everyday® (2006) | 6 | 11 |
| CHASE COLLEGE (0670) | 3 | 9 |
| Chase Freedom Unlimited (7463) | 1 | 7 |
| Chase Sapphire Preferred (7795) | 2 | 8 |
| Savor (7496) | 4 | 10 |

## Decisions (locked)

- **Account identity:** two account rows are the same real account when their **`Name`
  matches exactly** (the name already carries the last-4, e.g. "Chase Freedom Unlimited
  (7463)"). Case-insensitive comparison.
- **Scope:** both **prevent** future duplicates and **heal** the 5 existing ones.
- **Transaction de-dupe key:** `(AccountId, Posted timestamp, Amount, Description)` — the
  full posted **date+time**, not just the date — with a **count check** (see ②).

## ① Prevention — account re-link (always-on, in sync ingest)

In `SyncService.IngestAsync`, accounts are currently mapped to existing rows only by
`SimpleFinAccountId`; a miss inserts a new row. New behavior:

1. Look up the incoming account by `SimpleFinAccountId` (unchanged fast path).
2. **On a miss**, look for an existing account whose `Name` equals the incoming account's
   name (case-insensitive). If found → it's a **re-link**: reuse that row and update its
   stored `SimpleFinAccountId` to the incoming value (name/org/balance/balance-date update as
   today).
3. Only when neither the ID nor the name matches → insert a genuinely new account.

Effect: a future re-auth updates the existing account in place — no duplicate row is created,
and that account's transactions all continue to hang off one row.

## ② Prevention — content-based transaction de-dupe (always-on)

Account re-link alone is not enough: after re-auth, the overlap-window re-fetch returns the
same transactions with **new** `SimpleFinTransactionId`s, which the ID-based dedupe would not
catch, producing duplicates on the (now single) account.

Add a content-based fallback that runs **after** the account a transaction belongs to is
resolved (so it compares against the canonical account's existing rows):

- **Fast path (unchanged):** if the incoming `SimpleFinTransactionId` already exists, skip.
- **Content fallback:** group both the already-stored transactions and the incoming ones by
  `(AccountId, Posted, Amount, Description)` where `Posted` is the full UTC timestamp. For
  each such key, insert incoming rows only **beyond the count already stored**. I.e. if the
  bridge reports N rows for a key and M already exist, insert `max(0, N − M)`.

Why the count check (not just the timestamp): SimpleFIN provides a real posted *time* only for
card swipes (e.g. "GOGI" 11:56:50). Bank-posted items are stamped **12:00:00 noon**
(date-only), so two genuine same-day identical charges (e.g. two $2.40 MBTA tickets) share an
identical key. The count check keeps **as many as the bridge reports**, so nothing legitimate
is dropped; only a re-auth re-import *beyond* that count is suppressed.

Both the existing-stored and incoming sides use the same grouping so the reconciliation is
symmetric and order-independent within a sync payload.

## ③ Heal the existing duplicates — one-off, config-gated

A run-once background service `AccountMergeBackfill`, following the existing
note/transfer/diagnostic pattern: gated behind `Plutus:Backfill:MergeAccounts`
(compose env `PLUTUS_BACKFILL_MERGE_ACCOUNTS`), inert by default.

On run, for each set of accounts sharing the same `Name` (case-insensitive) with more than one
row:

1. **Canonical row** = the row with the most transactions (preserves history); tie-break the
   lowest `Id`.
2. **Adopt freshness:** copy the most-recent (by `BalanceDate`) row's `SimpleFinAccountId`,
   `Balance`, and `BalanceDate` onto the canonical row — so the next sync matches the
   canonical row by the current SimpleFIN ID.
3. **Repoint transactions:** `UPDATE Transactions SET AccountId = canonical WHERE AccountId IN
   (other rows)`.
4. **Drop content-duplicate transactions** on the canonical account using the same multiset
   rule as ② (keep as many identical `(Posted, Amount, Description)` rows as the max of the
   groups being merged; delete the surplus). For the current data this removes exactly the one
   MBTA duplicate.
5. **Delete** the now-empty duplicate account rows.
6. Log a summary (groups merged, accounts deleted, transactions repointed/dropped).

Operational steps (manual, around the deploy): snapshot the prod DB first (copy
`plutus.db` + `-wal` + `-shm`), deploy with the flag set, verify the result (6 accounts, Net
Worth ~$53,847), then unset the flag and redeploy so it's inert again.

## Components & file structure

- `src/Plutus.Core/Sync/SyncService.cs` — `IngestAsync`: account re-link (①) and the
  content-based dedupe (②). Extract small **pure, testable helpers** for the two reusable
  pieces:
  - account match-by-name (given incoming name + existing accounts → existing row or null),
  - the multiset reconciliation `(existing rows, incoming rows) → rows to insert` keyed on
    `(Posted, Amount, Description)`.
  Reusing the multiset helper keeps ② and ③-step-4 consistent.
- `src/Plutus.Web/BackgroundServices/AccountMergeBackfill.cs` — new gated one-off service (③).
- `docker-compose.yml` — add the `Plutus__Backfill__MergeAccounts` env wiring.

No schema changes (existing columns suffice).

## Testing

- **Account re-link (unit, SyncService via the in-memory test DB):** incoming account with a
  new `SimpleFinAccountId` but an existing same-`Name` row → the existing row's ID is updated,
  no second row is inserted; a genuinely new name → a new row is inserted.
- **Content de-dupe (unit, pure multiset helper):** re-imported transaction with a new ID but
  identical `(Posted, Amount, Description)` → not inserted; two legitimate identical same-day
  noon charges → both kept; a charge with the same amount/desc but a different timestamp →
  kept.
- **Merge/heal (unit, backfill logic over the in-memory test DB):** two same-`Name` rows (one
  with history, one fresh) → merged to one row that has all transactions, the fresh
  `SimpleFinAccountId`/`Balance`/`BalanceDate`, one content-duplicate dropped, the empty row
  deleted.
- Existing 45 tests stay green.

## Out of scope

- Changing how staleness/sync are surfaced (already shipped).
- Any UI for manually merging accounts (the heal is a one-off backfill).
- Handling a bank genuinely closing one account and opening another with the same display
  name (treated as the same account by the name rule; acceptable for this single-user app).
