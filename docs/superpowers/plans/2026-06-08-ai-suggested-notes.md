# AI-Suggested Notes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** At sync, the existing Claude categorization call also returns a cleaned-up plain-English note; the Review page pre-fills note + category as editable "AI suggested" values and re-categorizes only when the note is actually edited.

**Architecture:** Extend the single structured-output categorization call to return `{category, note, confidence}`. `SyncService` stores the note on the transaction. `Review.razor` pre-fills it, badges it, and refines the category only on a real note edit. No DB schema change — reuse `Transaction.Note`.

**Tech Stack:** .NET 10, C# 14, EF Core/SQLite, Blazor (InteractiveServer), Anthropic SDK (structured outputs), xUnit + in-memory SQLite.

**Environment note:** the .NET 10 SDK is not on PATH — prefix dotnet commands with `export PATH="$HOME/.dotnet:$PATH"`. Build: `dotnet build`. Test: `dotnet test`.

---

## File Structure

- `src/Plutus.Core/Categorization/ICategorizer.cs` — `CategorizationResult` gains `Note`.
- `src/Plutus.Core/Categorization/ClaudeCategorizer.cs` — schema `note` property, system prompt, parse `note`.
- `src/Plutus.Core/Sync/SyncService.cs` — store `result.Note` on new transactions.
- `src/Plutus.Web/Components/Pages/Review.razor` — `OriginalNote`, edited-detection, refine-only-on-edit, "AI suggested" note badge.
- `tests/Plutus.Core.Tests/ClaudeCategorizerSchemaTests.cs` — assert `note` in schema.
- `tests/Plutus.Core.Tests/Fakes.cs` — `FakeCategorizer` carries a note.
- `tests/Plutus.Core.Tests/SyncServiceTests.cs` — assert the note is stored.

---

### Task 1: Combined `{category, note, confidence}` categorization output (Core)

**Files:**
- Modify: `src/Plutus.Core/Categorization/ICategorizer.cs`
- Modify: `src/Plutus.Core/Categorization/ClaudeCategorizer.cs`
- Modify: `tests/Plutus.Core.Tests/Fakes.cs`
- Test: `tests/Plutus.Core.Tests/ClaudeCategorizerSchemaTests.cs`

- [ ] **Step 1: Extend the schema test to require a `note` field (failing test)**

In `ClaudeCategorizerSchemaTests.cs`, add assertions after the existing ones:

```csharp
// note: cleaned-up description, required string
Assert.Equal("string", properties.GetProperty("note").GetProperty("type").GetString());
Assert.Contains("note", required);
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/Plutus.Core.Tests/Plutus.Core.Tests.csproj`
Expected: FAIL — the schema has no `note` property yet (`KeyNotFound`/assert failure).

- [ ] **Step 3: Add `Note` to `CategorizationResult`**

In `ICategorizer.cs`:

```csharp
public sealed record CategorizationResult(string Category, string? Note, double Confidence);
```

- [ ] **Step 4: Add `note` to the schema and the system prompt; parse it**

In `ClaudeCategorizer.cs`:

Update the system prompt constant to also request a note:

```csharp
private const string SystemPrompt =
    "You are a personal-finance assistant. For a single bank transaction, do two things: " +
    "(1) choose the closest-fit category from the allowed list; " +
    "(2) write a short note: a concise, plain-English description (about 3-8 words) of what the " +
    "transaction is, decoded from the bank description (e.g. \"AMZN MKTP US*2K4...\" -> \"Amazon Marketplace purchase\"). " +
    "Do not invent details you can't infer from the description (no guessed people, occasions, or amounts); " +
    "if a description is already clear, lightly normalize it. When a user note is provided, weight it heavily for the category. " +
    "Set confidence to your probability (0-1) that the category is correct.";
```

In `BuildSchema`, add the `note` property and mark it required:

```csharp
["properties"] = JsonSerializer.SerializeToElement(new
{
    category = new { type = "string", @enum = names },
    note = new { type = "string" },
    confidence = new { type = "number" },
}),
["required"] = JsonSerializer.SerializeToElement(new[] { "category", "note", "confidence" }),
```

Update the parse record and the return:

```csharp
private sealed record CategorizationJson(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("confidence")] double Confidence);
```

```csharp
return new CategorizationResult(match.Name, parsed.Note, parsed.Confidence);
```

- [ ] **Step 5: Update `FakeCategorizer` to carry a note**

In `Fakes.cs`:

```csharp
internal sealed class FakeCategorizer(string category, string? note = null, double confidence = 0.9) : ICategorizer
{
    public int Calls { get; private set; }

    public Task<CategorizationResult?> CategorizeAsync(string description, string? note2, IReadOnlyList<Category> categories, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<CategorizationResult?>(new CategorizationResult(category, note, confidence));
    }
}
```

(Keep the existing parameter name for the interface method's note argument as it is in the file; only the constructor gains `note`. If the interface arg is named `note`, rename the field/ctor param to avoid the clash, e.g. `suggestedNote`.)

- [ ] **Step 6: Run the full suite to verify green**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build && dotnet test`
Expected: PASS — all 14 existing tests + the extended schema assertions.

- [ ] **Step 7: Commit**

```bash
git add src/Plutus.Core/Categorization tests/Plutus.Core.Tests/Fakes.cs tests/Plutus.Core.Tests/ClaudeCategorizerSchemaTests.cs
git commit -m "feat(core): categorizer returns a suggested note alongside category"
```

---

### Task 2: Store the suggested note at sync (Core)

**Files:**
- Modify: `src/Plutus.Core/Sync/SyncService.cs`
- Test: `tests/Plutus.Core.Tests/SyncServiceTests.cs`

- [ ] **Step 1: Extend the sync test to assert the note is stored (failing test)**

In `SyncServiceTests.cs`, in `RunAsync_filters_credits_dedupes_and_categorizes`, change the categorizer to return a note and assert it lands on the new transaction:

```csharp
var categorizer = new FakeCategorizer("Dining", note: "Coffee shop");
```

After the existing assertions on `added`:

```csharp
Assert.Equal("Coffee shop", added.Note);
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test tests/Plutus.Core.Tests/Plutus.Core.Tests.csproj`
Expected: FAIL — `added.Note` is null (SyncService doesn't store the note yet).

- [ ] **Step 3: Store the note in the categorization pass**

In `SyncService.cs`, in the per-transaction categorization loop, set the note when a result is returned. Change the block to:

```csharp
var result = await categorizer.CategorizeAsync(transaction.Description, note: null, categories, ct);
if (result is not null)
{
    transaction.Note = result.Note;
    if (byName.TryGetValue(result.Category, out var category))
    {
        transaction.CategoryId = category.Id;
        transaction.IsCategorized = true;
        transaction.CategorizedAt = categorizedAt;
    }
}
```

- [ ] **Step 4: Run to verify green**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build && dotnet test`
Expected: PASS — all tests including the new `added.Note` assertion.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Sync/SyncService.cs tests/Plutus.Core.Tests/SyncServiceTests.cs
git commit -m "feat(core): store the AI-suggested note on synced transactions"
```

---

### Task 3: Review page — pre-fill, badge, refine-only-on-edit (Web)

**Files:**
- Modify: `src/Plutus.Web/Components/Pages/Review.razor`

No automated test (no bUnit harness); verify by build + manual reasoning. READ the current file first to match its exact markup/fields.

- [ ] **Step 1: Track the original note and a note-suggestion flag on `ReviewItem`**

In the `ReviewItem` class add:

```csharp
public string? OriginalNote { get; init; }
public bool HasNoteSuggestion { get; init; }
```

In `OnInitializedAsync`, when building each `ReviewItem`, set them from the loaded transaction:

```csharp
OriginalNote = t.Note,
HasNoteSuggestion = !string.IsNullOrWhiteSpace(t.Note),
```

- [ ] **Step 2: Add the "AI suggested" badge to the Note label**

In the Note `<div class="field">`, change the label so the badge shows while the note still equals the suggestion:

```razor
<label>
    Note
    @if (item.HasNoteSuggestion && string.Equals(item.Note, item.OriginalNote, StringComparison.Ordinal))
    {
        <span class="suggested-badge">AI suggested</span>
    }
</label>
```

(Use the same `suggested-badge` class the Category label already uses.)

- [ ] **Step 3: Refine the category only when the note was actually edited**

In `SaveAsync`, replace the re-categorization guard. The current code refines when `!manualOverride && note is not null`. Change to compute `noteEdited` and require it:

```csharp
var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note!.Trim();
var originalNote = string.IsNullOrWhiteSpace(item.OriginalNote) ? null : item.OriginalNote!.Trim();
var noteEdited = !string.Equals(note, originalNote, StringComparison.Ordinal);
var manualOverride = item.SelectedCategoryId != item.OriginalCategoryId;
int? categoryId = item.SelectedCategoryId == 0 ? null : item.SelectedCategoryId;

// Refine only when the user actually changed the note (and didn't manually pick a
// category). An emptied note (note == null) has nothing to refine on, so skip the call.
if (!manualOverride && noteEdited && note is not null)
{
    var result = await Categorizer.CategorizeAsync(item.Description, note, _categories);
    if (result is not null)
    {
        var match = _categories.FirstOrDefault(
            c => string.Equals(c.Name, result.Category, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            categoryId = match.Id;
        }
    }
}
```

The rest of `SaveAsync` (writing `Note`/`CategoryId`/`IsReviewed`/`CategorizedAt`, removing the item) is unchanged.

- [ ] **Step 4: Build and verify**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build && dotnet test`
Expected: PASS — 0 errors/0 warnings; 14 tests still green (no test changes here).

Manually reason through: a synced transaction shows its AI note + badge; confirming unchanged makes no Claude call; editing the note (and not the category) refines the category; clearing the note makes no call; changing the dropdown wins and skips the call.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Web/Components/Pages/Review.razor
git commit -m "feat(web): pre-fill + badge AI note on Review; refine only on note edit"
```

---

## Notes for the executor
- After all tasks: run the full `dotnet build && dotnet test` once more, then `/code-review`, then deploy per the project's container flow (rebuild image + `sg docker -c "docker compose up -d"`).
- Do not add a DB column or a migration — the feature reuses `Transaction.Note`.
- If a subagent hits a genuine decision/ambiguity, surface it to the user (do not decide silently).
