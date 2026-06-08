# Dashboard Insights + Transfer Exclusion — Design

**Date:** 2026-06-08
**Status:** Draft for review
**Author:** Claude (brainstormed with user)

## Problem

The dashboard (`/`) shows *Needs Review*, *Last Sync*, a *Spending by Category*
bar chart, and account balances. Two gaps:

1. **Missing insight.** No at-a-glance **net worth**, and no callout of the
   **top spending category** for the month.
2. **Spending is double-counted.** Only outflows are persisted as expenses
   (credits are dropped at sync). So a credit-card **purchase** (outflow on the
   card) *and* the **bill payment** that pays that card off (outflow on
   checking) are both counted — inflating spend. Confirmed in live data:
   payments from *Chase College* to *Chase 7463/7795*, *Amex*, and *Capital One*
   are each counted on top of those cards' already-synced purchases (and are
   currently mis-categorized as "Fees"). The **BILT** card is **not** connected,
   so its bill payment is the *only* record of that spend and must stay counted.

## Goals

- Add a **Net Worth** widget and a **Top Category (this month)** widget.
- Stop counting credit-card bill payments to **synced** cards as spending, while
  **keeping** payments to **unsynced** cards (BILT) as real spend.
- Correct the existing 113 transactions (one-off backfill).

## Non-Goals (YAGNI — possible follow-ups)

Budgets, month-over-month trend, income tracking, per-account "is a credit card"
settings UI. None in this pass.

## Design

### 1. Data model (one migration)

- **`Category.ExcludeFromSpending`** (bool, default `false`). Categories with
  this flag are omitted from all spending math (reports, chart, top category).
  Future-proof: the user could later flag other categories too.
- **Seed a system category `Transfer`** (`IsSystem = true`,
  `ExcludeFromSpending = true`, muted grey color). This is where detected
  card-payment transfers land.
- **No `Account` changes.** SimpleFIN provides no account *type*, so we avoid a
  fragile classification field (see net worth + detection below).

### 2. Transfer detection (`TransferDetector`)

A pure, testable function: given a transaction (description, source `AccountId`)
and the set of synced accounts (id, name, org), decide if it is a payment to a
**synced** account other than its own.

Rule — flag as **Transfer** when **both** hold:
- **Payment gate:** description contains a payment marker
  (`PMT`, `PAYMENT`, `AUTOPAY`, `ACH PMT`, `EPAY`/`E-PAYMENT`), case-insensitive.
- **Target match:** the description matches some *other* synced account by
  - its **last-4** (parsed from the account name, e.g. `Chase Freedom Unlimited (7463)`
    → `7463`, or text like "ending in 7463"), **or**
  - its **issuer org** (e.g. account org `American Express` ↔ `AMERICAN EXPRESS ACH PMT`;
    `Capital One` ↔ `CAPITAL ONE MOBILE PMT`).

Worked against live data:
- `Payment to Chase card ending in 7463` → last-4 7463 = Freedom → **Transfer** ✅
- `AMERICAN EXPRESS ACH PMT` → org Amex → **Transfer** ✅
- `CAPITAL ONE MOBILE PMT` → org Capital One → **Transfer** ✅
- `BILT CARD PMT` → no synced account matches → **counted** ✅
- `Online Payment … To Fensdale Property Trust` (rent) → matches no synced
  account → **counted** ✅

Detection is heuristic and **user-overridable**: detected transfers stay visible
in the Transactions list (tagged), so a mistake is correctable by recategorizing.

### 3. Where detection runs

- **At sync time:** before AI categorization, run the detector. If it flags a
  transfer, assign the `Transfer` category, set `IsCategorized = true` and
  `IsReviewed = true` (keeps transfers out of the Review queue), and **skip the
  Claude call** (saves an API request).
- **One-off backfill:** config-gated `PLUTUS_BACKFILL_TRANSFERS=true` (mirrors the
  existing `PLUTUS_BACKFILL_NOTES` pattern — inert by default). Re-runs the
  detector over all existing transactions and moves matches to `Transfer`. Run
  once after deploy, then unset.

### 4. Reporting changes

- **`SpendingReport`** (chart + top category): exclude transactions whose
  category has `ExcludeFromSpending = true`. Resolve excluded category ids once,
  then filter the group query.
- **`NetWorthReport`** (new): from `Accounts`,
  - `Total = Σ Balance`,
  - `Assets = Σ Balance where Balance > 0`,
  - `Cards = Σ Balance where Balance < 0`.
  Sign-based split needs no schema (cards already carry negative balances; a $0
  card contributes nothing either way). Current data → Total ≈ $54,432,
  Assets ≈ $55,465, Cards ≈ -$1,033.
- **Top Category:** reuse the corrected monthly spending list (already sorted
  desc); take the first row. Expose `Name`, `Total`, and
  `Share = Total / Σ(all corrected spend)`.

### 5. UI

- **`Home.razor`** top card-grid gains two stat cards alongside *Needs Review* /
  *Last Sync*:
  - **Net Worth** — big amount, sub-line `Assets $55,465 · Cards -$1,033`.
  - **Top Category** — category name, amount, and `NN% of spend`. Empty-state
    when no spend yet.
- **`Transactions.razor`** — rows in the `Transfer` category render a muted
  "Transfer" badge so excluded items are visibly distinct (not silently hidden).

### 6. Testing (`tests/Plutus.Core.Tests`)

- `TransferDetector`: matches by last-4; matches by issuer org (Amex, Capital
  One); does **not** match BILT; does **not** match rent (Fensdale); does **not**
  match a normal purchase (fails the payment gate); never matches the source
  account.
- `SpendingReport` omits `ExcludeFromSpending` categories.
- `NetWorthReport` total / assets / cards math (incl. a $0 card and a negative card).
- Top-category selection + share calculation.

## Rollout

1. Migration + seed `Transfer` category.
2. Implement detector, wire into sync, add reports + UI, add tests.
3. Deploy image; set `PLUTUS_BACKFILL_TRANSFERS=true`, deploy, verify the Fees
   bucket drops and net worth/top category look right, then unset and redeploy.
4. `/code-review`, then merge.

## Resolved decisions

- **Transfer badge styling:** reuse the existing category-chip style (the same
  chip used for categories elsewhere), rendered for the `Transfer` category. No
  dedicated/bespoke treatment.
