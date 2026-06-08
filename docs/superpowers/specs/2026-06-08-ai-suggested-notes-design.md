# AI-Suggested Notes — Design

**Date:** 2026-06-08
**Status:** Approved (design); pending implementation plan
**Feature:** At sync, Claude suggests a cleaned-up plain-English **note** alongside the category; the user approves or edits both on the Review page.

## Summary

Today each new transaction gets an AI-suggested **category** at sync time, but the
**note** is user-typed. This feature extends the existing single sync-time
categorization call to also return a **note** — a concise, plain-English
description decoded from the cryptic bank string (e.g. `AMZN MKTP US*2K4...` →
"Amazon Marketplace purchase"). The suggestion is stored on the transaction and
pre-filled on the Review page, where the user approves or edits both the note and
the category with one **Confirm**.

## Decisions (from brainstorming)

| Question | Decision |
| --- | --- |
| What is the suggested note? | **A — Cleaned-up, factual description** decoded from the bank string. No invented personal context (the AI can't know "gift for Sam"). |
| When is it generated? | **A — At sync, in the same Claude call that already categorizes.** One call returns `{category, note, confidence}`. No extra API calls vs today; note and category are mutually consistent. |
| Edit behavior on Review | **A — Keep "note refines category".** Editing the note re-runs Claude to refine the category (unless the user also manually picked a category, which wins). Confirming the unchanged suggestion makes no API call. |

## Non-goals (v1 / YAGNI)

- No new database column for "AI-suggested vs user-edited" — inferred from state (see Architecture §4).
- No separate "regenerate note" button, no per-note model selection, no note history.
- No note display changes on the Past Transactions page (notes remain a Review-page concern there).
- No on-demand/lazy note generation (decided against in Q2).

## Architecture

### 1. Combined categorization output — `Plutus.Core` (`Categorization/`)

- **`CategorizationResult`** gains a `Note`: `record CategorizationResult(string Category, string? Note, double Confidence)`.
- **`ClaudeCategorizer`**:
  - The structured-output JSON schema gains a `note` property (`type: "string"`) added to `properties` and `required`, alongside the existing enum-constrained `category` and numeric `confidence`. The `category` enum remains built from the live category names (unchanged).
  - The system prompt is extended: in addition to choosing a category, produce a `note` — a short (≈3–8 word) plain-English description of what the transaction is, derived by decoding the bank `description`. Explicitly instruct: do not invent details that aren't inferable from the description (no guessed people/occasions); if the description is already clear, lightly normalize it.
  - Parsing reads `note` from the structured JSON and returns it on `CategorizationResult`. On failure (any exception / empty result), returns `null` as today.
- **Two call sites, unchanged contracts:**
  - **Sync pass** (`note` argument is null): the returned `Note` is the AI suggestion and is consumed by `SyncService`.
  - **Review refine** (`note` argument is the user's text): the call still returns a category to refine to; the returned `Note` is **ignored** here — the user's note is authoritative and is never overwritten by a refine.

### 2. Store the suggestion at sync — `Plutus.Core` (`Sync/SyncService.cs`)

- In the description-only categorization pass over newly-inserted transactions, when `CategorizeAsync` returns a result, set `transaction.Note = result.Note` (in addition to the existing `CategoryId`/`IsCategorized`/`CategorizedAt`).
- If categorization fails (`null`), `Note` stays null and the transaction is uncategorized — unchanged behavior. New transactions therefore arrive with `IsReviewed = false`, a suggested `CategoryId`, and a suggested `Note`.

### 3. Approve / edit on Review — `Plutus.Web` (`Components/Pages/Review.razor`)

- The note input is already pre-filled from `Transaction.Note`; it now carries the AI suggestion. Add an **"AI suggested"** badge on the Note field, shown while the field still matches the suggestion (i.e. the note is non-empty and the user hasn't edited it this session).
- `ReviewItem` gains `OriginalNote` (the value loaded from the DB) so edits can be detected.
- **Confirm (`SaveAsync`) re-categorization condition changes** from today's "note is non-null" to **"note was edited"**:
  - `noteEdited = normalized(currentNote) != normalized(OriginalNote)`.
  - If `!manualCategoryOverride && noteEdited` → call `Categorizer.CategorizeAsync(description, editedNote, categories)` and apply the refined category. Otherwise save the selected category as-is.
  - This fixes the current behavior, which re-calls Claude on *every* confirm (even when the user just accepts the unchanged suggestion).
  - Manual category override (user changed the dropdown) always wins and skips the API call, as today.
- Confirm saves `Note` (suggested or edited) + the resolved category, sets `IsReviewed = true` and `IsCategorized`/`CategorizedAt` accordingly, and removes the card from the queue — unchanged otherwise.

### 4. No schema change

Reuse `Transaction.Note`. The "AI suggested" badge is inferred from state — on Review every item is `IsReviewed = false`, so a non-empty note that equals `OriginalNote` is the AI suggestion. No `NoteIsAiSuggested` column is added (YAGNI); `IsReviewed` already distinguishes approved from pending.

## Data flow

```
Sync (new transaction)
  └─ ICategorizer.CategorizeAsync(description, note: null, categories)
       └─ Claude structured output → { category(enum), note(string), confidence }
  ⇒ Transaction { CategoryId, Note = suggestion, IsReviewed = false }

Review page
  ├─ shows Note (pre-filled, "AI suggested" badge) + Category (pre-selected)
  └─ Confirm:
       ├─ note unchanged & no manual category change → save as-is (no API call)
       ├─ note edited (no manual category)           → refine category via Claude(description, editedNote)
       └─ manual category change                     → use it, skip API
     ⇒ Transaction { Note, CategoryId, IsReviewed = true }
```

## Error handling & edge cases

- **AI call fails at sync** → `Note` null, transaction uncategorized; appears in Review with an empty note (today's behavior).
- **AI returns an empty note** → treated as no suggestion (null/empty); no badge, empty field.
- **User clears the note and confirms** → `noteEdited` is true; with no manual category change this triggers a refine on an empty note — guard so an empty edited note does **not** call Claude (nothing to refine); just save the selected category.
- **Refine returns a category not in the list / fails** → keep the currently-selected category (today's behavior).
- **Manual override + edited note together** → manual category wins, no API call.

## Testing

- **`ClaudeCategorizerSchemaTests`** (extend): assert the schema's `properties` includes a `note` of type `string` and that `required` contains `note` (plus the existing `category` enum from the supplied categories and `confidence`).
- **`SyncServiceTests`** (extend): `FakeCategorizer` returns a note; assert the new transaction's `Note` equals the suggested note after a sync, and that an uncategorized (null-result) path leaves `Note` null.
- **`Fakes.cs`**: `FakeCategorizer` constructor gains an optional `note` and returns it in `CategorizationResult`.
- Update any existing `CategorizationResult` construction for the new `Note` field.
- **Review edit-vs-unchanged logic**: covered indirectly by the categorizer/sync tests; the Razor interaction is verified manually after deploy (no bUnit harness exists yet).
- **Manual verification:** after deploy, confirm new/synced transactions show a plain-English AI note + "AI suggested" badge on Review; confirming unchanged makes no API call; editing the note refines the category; manual category change wins.

## Files touched

- **Modified (Core):** `src/Plutus.Core/Categorization/ICategorizer.cs` (add `Note` to `CategorizationResult`), `src/Plutus.Core/Categorization/ClaudeCategorizer.cs` (schema `note` + system prompt + parse), `src/Plutus.Core/Sync/SyncService.cs` (store `result.Note`).
- **Modified (Web):** `src/Plutus.Web/Components/Pages/Review.razor` (`OriginalNote`, edited-detection, "AI suggested" badge, refine-only-on-edit).
- **Modified (Tests):** `tests/Plutus.Core.Tests/Fakes.cs`, `tests/Plutus.Core.Tests/SyncServiceTests.cs`, `tests/Plutus.Core.Tests/ClaudeCategorizerSchemaTests.cs`.
