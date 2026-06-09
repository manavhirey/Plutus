# Edit a transaction from the Transactions list — design

**Date:** 2026-06-09
**Status:** Approved (design); pending implementation plan

## Problem

`/review` lets the user edit a transaction's category and note, but **only while it is
unreviewed** — once confirmed, it disappears from Review and `/transactions` shows it
read-only with no way to fix a category or note later. This feature adds in-place editing of
any transaction from the Transactions list.

## Decisions (locked)

- **Interaction:** clicking a transaction row opens a **slide-over panel** from the right
  (Q1). Not a modal or inline expand.
- **Editing:** **manual only** (Q2) — the user picks the category from a dropdown and edits
  the note text; no Claude API call is made.
- **Editable fields:** Category and Note. Date, full description, and amount are read-only
  (they come from the bank).

## Interaction

- Each row on `/transactions` becomes clickable (pointer cursor + hover highlight).
- Clicking opens a slide-over panel anchored to the right edge over a dimmed backdrop.
- The panel closes **without saving** when the user clicks the backdrop, clicks the ✕, or
  presses **Esc**.
- **Save** persists the changes, closes the panel, and updates the row in place. If a category
  filter is active and the edit moved the transaction out of that filter, the row drops from
  the list.

## Panel contents

- **Read-only header:** posted date, full description, amount (`Money.Expense`).
- **Category** — a `<select>` with the same options as Review: `Uncategorized` (value 0) plus
  every category ordered by `SortOrder` (includes `Transfer`).
- **Note** — a free-text `<input>`.
- **Save** and **Cancel** buttons.

## Save behavior

Mirrors `Review.razor`'s save, minus the AI refinement and minus marking reviewed:

- Load the transaction by id in a fresh `DbContext`.
- `Note` ← trimmed text, or `null` if blank.
- `CategoryId` ← selected id, or `null` when `Uncategorized` (value 0) is chosen.
- `IsCategorized` ← `CategoryId is not null`.
- `CategorizedAt` ← `TimeProvider.GetUtcNow().UtcDateTime` when a category is set; otherwise
  leave the existing value.
- **`IsReviewed` is left unchanged** (editing an already-reviewed transaction must not flip its
  reviewed state, and editing an unreviewed one shouldn't silently mark it reviewed — that's
  Review's job).
- `SaveChangesAsync`.

## Components & structure

- **New** `src/Plutus.Web/Components/TransactionEditPanel.razor` — a self-contained
  InteractiveServer component: the backdrop + slide-over markup, the category/note form, and
  the save logic. Parameters:
  - `[Parameter] int? TransactionId` — when non-null the panel is open for that transaction;
    `null` means closed.
  - `[Parameter] IReadOnlyList<Category> Categories` — passed in so the panel doesn't re-query.
  - `[Parameter] EventCallback OnSaved` — raised after a successful save so the parent
    reloads.
  - `[Parameter] EventCallback OnClosed` — raised when dismissed without saving.
  On open (parameter set), it loads the transaction's current Description/Amount/PostedDate/
  Note/CategoryId via `IDbContextFactory` to populate the read-only header and the form.
- **Modify** `src/Plutus.Web/Components/Pages/Transactions.razor` — make each `.txn-row`
  clickable (`@onclick` sets the selected transaction id), render `<TransactionEditPanel>` with
  that id, and on `OnSaved` re-run `LoadAsync()` (which re-applies the current filter, so a
  row that moved out of an active filter drops). `OnClosed` clears the selected id.
- **Modify** `src/Plutus.Web/wwwroot/app.css` — add slide-over styles (`.txn-row` clickable
  affordance, a `.panel-backdrop`, a right-anchored `.slideover` with a slide-in transition,
  and form/button layout). No existing modal/slide-over styles to reuse.

Keeping the panel self-contained means `Transactions.razor` only owns "which row is open" and a
reload; the panel owns its own form state and persistence.

## Edge cases & error handling

- **Closed state:** `TransactionId == null` renders nothing (no backdrop, no DOM).
- **Empty note:** stored as `null`, consistent with sync/Review.
- **Transaction vanished** (deleted between list load and open): the load returns null → the
  panel shows nothing and closes; no crash.
- **Esc / backdrop / ✕** all route to `OnClosed` without persisting.
- Save failures are unlikely (single-user, local SQLite); a thrown exception is caught and
  logged, leaving the panel open so the user can retry.

## Testing

- A small service/logic-level test for the save path: setting category + note persists them,
  sets `IsCategorized`/`CategorizedAt`, and **leaves `IsReviewed` unchanged**; clearing the
  category to `Uncategorized` nulls `CategoryId` and `IsCategorized` is false. Use the existing
  real-in-memory-SQLite test pattern (as in `SyncServiceTests`).
- If the save logic lives inside the `.razor` component and isn't directly unit-testable,
  extract it into a tiny pure/DB helper the test can call (preferred), so the persistence rule
  is covered without a UI harness.
- Existing tests stay green.

## Out of scope

- Editing date, description, or amount (bank-owned).
- AI re-categorization from the note (that's Review's behavior; explicitly excluded here).
- Bulk edit / multi-select.
- Editing from the dashboard or Review (Review already edits; the dashboard has no row list).
