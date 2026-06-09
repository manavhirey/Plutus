# Edit a Transaction (slide-over) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user click any transaction on `/transactions` to open a right-side slide-over panel and edit its category and note (manual, no AI), persisting the change in place.

**Architecture:** A pure, unit-tested `TransactionEditor.ApplyEdit` in `Plutus.Core` holds the persistence rule; a self-contained `TransactionEditPanel.razor` (InteractiveServer) owns the slide-over UI + load/save; `Transactions.razor` just tracks which row is open and reloads on save. CSS adds the backdrop + slide-over.

**Tech Stack:** .NET 10, Blazor (InteractiveServer), EF Core (SQLite), xUnit. Always `export PATH="$HOME/.dotnet:$PATH"` before dotnet commands.

**Reference spec:** `docs/superpowers/specs/2026-06-09-edit-transaction-design.md`

**Reuse note:** `Money` (namespace `Plutus.Web`), and the CSS classes `.field`, `.btn`, `.btn-primary`, `.review-date`, `.review-desc`, `.review-amount`, plus `input[type=text]`/`select` styling, already exist — reuse them. `_Imports.razor` already imports `Microsoft.EntityFrameworkCore`, `Plutus.Core.Data`, `Plutus.Core.Models`, `Microsoft.AspNetCore.Components.Web`, and `Plutus.Web.Components`.

---

### Task 1: `TransactionEditor` pure helper (Plutus.Core)

The single source of truth for the save rule, so it's unit-testable without a UI.

**Files:**
- Create: `src/Plutus.Core/Transactions/TransactionEditor.cs`
- Test: `tests/Plutus.Core.Tests/TransactionEditorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Plutus.Core.Tests/TransactionEditorTests.cs`:

```csharp
using Plutus.Core.Models;
using Plutus.Core.Transactions;

namespace Plutus.Core.Tests;

public sealed class TransactionEditorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 18, 0, 0, TimeSpan.Zero);

    private static Transaction Sample() => new()
    {
        SimpleFinTransactionId = "t1",
        Description = "AMZN",
        Amount = 12.34m,
        CategoryId = null,
        IsCategorized = false,
        IsReviewed = true, // already reviewed — editing must not change this
        Note = null,
    };

    [Fact]
    public void Sets_category_note_and_categorized_stamp()
    {
        var t = Sample();
        TransactionEditor.ApplyEdit(t, categoryId: 5, note: "  gift for Sam  ", now: Now);

        Assert.Equal(5, t.CategoryId);
        Assert.True(t.IsCategorized);
        Assert.Equal(Now.UtcDateTime, t.CategorizedAt);
        Assert.Equal("gift for Sam", t.Note); // trimmed
    }

    [Fact]
    public void Leaves_IsReviewed_unchanged()
    {
        var reviewed = Sample(); // IsReviewed = true
        TransactionEditor.ApplyEdit(reviewed, categoryId: 5, note: "x", now: Now);
        Assert.True(reviewed.IsReviewed);

        var unreviewed = Sample();
        unreviewed.IsReviewed = false;
        TransactionEditor.ApplyEdit(unreviewed, categoryId: 5, note: "x", now: Now);
        Assert.False(unreviewed.IsReviewed);
    }

    [Fact]
    public void Clearing_category_to_uncategorized_nulls_it()
    {
        var t = Sample();
        t.CategoryId = 5;
        t.IsCategorized = true;
        t.CategorizedAt = Now.UtcDateTime.AddDays(-1);

        TransactionEditor.ApplyEdit(t, categoryId: null, note: "still noted", now: Now);

        Assert.Null(t.CategoryId);
        Assert.False(t.IsCategorized);
        Assert.Equal(Now.UtcDateTime.AddDays(-1), t.CategorizedAt); // unchanged when no category set
        Assert.Equal("still noted", t.Note);
    }

    [Fact]
    public void Blank_note_becomes_null()
    {
        var t = Sample();
        TransactionEditor.ApplyEdit(t, categoryId: 5, note: "   ", now: Now);
        Assert.Null(t.Note);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: FAIL — `TransactionEditor` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Plutus.Core/Transactions/TransactionEditor.cs`:

```csharp
using Plutus.Core.Models;

namespace Plutus.Core.Transactions;

/// <summary>
/// Applies a manual category/note edit to a transaction (the Transactions-list slide-over).
/// Mirrors the Review save minus the AI refinement and minus marking the row reviewed.
/// </summary>
public static class TransactionEditor
{
    /// <summary>
    /// <paramref name="categoryId"/> null means Uncategorized. Sets <see cref="Transaction.IsCategorized"/>
    /// and stamps <see cref="Transaction.CategorizedAt"/> only when a category is assigned; never touches
    /// <see cref="Transaction.IsReviewed"/>. A blank note is stored as null.
    /// </summary>
    public static void ApplyEdit(Transaction transaction, int? categoryId, string? note, DateTimeOffset now)
    {
        transaction.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        transaction.CategoryId = categoryId;
        transaction.IsCategorized = categoryId is not null;
        if (categoryId is not null)
        {
            transaction.CategorizedAt = now.UtcDateTime;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet test`
Expected: PASS — all `TransactionEditorTests` green; existing tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Core/Transactions/TransactionEditor.cs tests/Plutus.Core.Tests/TransactionEditorTests.cs
git commit -m "feat(core): add TransactionEditor.ApplyEdit (manual category/note edit rule)"
```

---

### Task 2: `TransactionEditPanel` component

**Files:**
- Create: `src/Plutus.Web/Components/TransactionEditPanel.razor`

- [ ] **Step 1: Create the component**

Create `src/Plutus.Web/Components/TransactionEditPanel.razor`:

```razor
@inject IDbContextFactory<PlutusDbContext> DbFactory
@inject TimeProvider Time
@inject ILogger<TransactionEditPanel> Logger
@using Plutus.Core.Transactions

@if (_loaded && TransactionId is not null)
{
    <div class="panel-backdrop" @onclick="CloseAsync"></div>
    <div class="slideover" tabindex="0" @ref="_panelRef" @onkeydown="HandleKeyDownAsync">
        <div class="slideover-head">
            <div class="slideover-title">Edit transaction</div>
            <button class="slideover-close" @onclick="CloseAsync" aria-label="Close">✕</button>
        </div>

        <div class="slideover-meta">
            <span class="review-date">@_postedDate.ToLocalTime().ToString("MMM d, yyyy")</span>
            <span class="review-desc">@_description</span>
            <span class="review-amount">@Money.Expense(_amount)</span>
        </div>

        <div class="field">
            <label>Category</label>
            <select @bind="_selectedCategoryId">
                <option value="0">Uncategorized</option>
                @foreach (var category in Categories)
                {
                    <option value="@category.Id">@category.Name</option>
                }
            </select>
        </div>

        <div class="field">
            <label>Note</label>
            <input type="text" @bind="_note" placeholder="e.g. birthday gift for Sam" />
        </div>

        <div class="slideover-actions">
            <button class="btn" @onclick="CloseAsync" disabled="@_saving">Cancel</button>
            <button class="btn btn-primary" @onclick="SaveAsync" disabled="@_saving">@(_saving ? "Saving…" : "Save")</button>
        </div>
    </div>
}

@code {
    [Parameter] public int? TransactionId { get; set; }
    [Parameter] public IReadOnlyList<Category> Categories { get; set; } = [];
    [Parameter] public EventCallback OnSaved { get; set; }
    [Parameter] public EventCallback OnClosed { get; set; }

    private int? _loadedId;
    private bool _loaded;
    private bool _saving;
    private bool _focusPending;

    private string _description = "";
    private decimal _amount;
    private DateTime _postedDate;
    private int _selectedCategoryId; // 0 = Uncategorized
    private string? _note;

    private ElementReference _panelRef;

    protected override async Task OnParametersSetAsync()
    {
        if (TransactionId == _loadedId)
        {
            return; // no change
        }

        _loadedId = TransactionId;
        _loaded = false;

        if (TransactionId is null)
        {
            return; // closed
        }

        await using var db = await DbFactory.CreateDbContextAsync();
        var txn = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == TransactionId.Value);
        if (txn is null)
        {
            await OnClosed.InvokeAsync(); // vanished — treat as closed
            return;
        }

        _description = txn.Description;
        _amount = txn.Amount;
        _postedDate = txn.PostedDate;
        _selectedCategoryId = txn.CategoryId ?? 0;
        _note = txn.Note;
        _loaded = true;
        _focusPending = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPending && _loaded)
        {
            _focusPending = false;
            try { await _panelRef.FocusAsync(); } catch { /* element may already be gone */ }
        }
    }

    private async Task HandleKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            await CloseAsync();
        }
    }

    private async Task CloseAsync()
    {
        if (_saving)
        {
            return;
        }
        await OnClosed.InvokeAsync();
    }

    private async Task SaveAsync()
    {
        if (TransactionId is null || _saving)
        {
            return;
        }

        _saving = true;
        try
        {
            int? categoryId = _selectedCategoryId == 0 ? null : _selectedCategoryId;
            await using var db = await DbFactory.CreateDbContextAsync();
            var txn = await db.Transactions.FirstOrDefaultAsync(t => t.Id == TransactionId.Value);
            if (txn is not null)
            {
                TransactionEditor.ApplyEdit(txn, categoryId, _note, Time.GetUtcNow());
                await db.SaveChangesAsync();
            }
            await OnSaved.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save transaction edit for {Id}.", TransactionId);
        }
        finally
        {
            _saving = false;
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build`
Expected: 0 errors, 0 warnings. (The component isn't referenced yet; Task 3 wires it in.)

- [ ] **Step 3: Commit**

```bash
git add src/Plutus.Web/Components/TransactionEditPanel.razor
git commit -m "feat(web): add TransactionEditPanel slide-over component"
```

---

### Task 3: Wire the panel into `Transactions.razor`

**Files:**
- Modify: `src/Plutus.Web/Components/Pages/Transactions.razor`

- [ ] **Step 1: Make each row clickable**

In `src/Plutus.Web/Components/Pages/Transactions.razor`, change the transaction row opening tag:

```razor
                        <tr class="txn-row">
```

to:

```razor
                        <tr class="txn-row txn-row-clickable" @onclick="() => _editingId = txn.Id">
```

- [ ] **Step 2: Render the panel at page level**

Find the end of the main render block — the `}` that closes the final `else { … }` (the block that renders the day groups), immediately before the `@code {` line. Insert the panel between them so it reads:

```razor
}

<TransactionEditPanel TransactionId="_editingId"
                      Categories="_categories"
                      OnSaved="OnEditSavedAsync"
                      OnClosed="() => _editingId = null" />

@code {
```

- [ ] **Step 3: Add the field + saved handler**

In the `@code` block, add the field next to the other private fields (after `private List<Transaction> _transactions = [];`):

```csharp
    private int? _editingId;
```

And add this method inside the `@code` block (e.g. after `LoadAsync`):

```csharp
    private async Task OnEditSavedAsync()
    {
        _editingId = null;
        await LoadAsync(); // re-applies the current filter; a row moved out of it drops
    }
```

- [ ] **Step 4: Build and run all tests**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet build && dotnet test`
Expected: 0 warnings/errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Plutus.Web/Components/Pages/Transactions.razor
git commit -m "feat(web): open edit panel from a transaction row + reload on save"
```

---

### Task 4: Slide-over CSS

**Files:**
- Modify: `src/Plutus.Web/wwwroot/app.css`

- [ ] **Step 1: Append the styles**

Append to the end of `src/Plutus.Web/wwwroot/app.css`:

```css
/* ── Transaction edit slide-over ──────────────────────────── */
.txn-row-clickable { cursor: pointer; }
.txn-row-clickable:hover { background: var(--accent-soft); }

.panel-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(31, 42, 64, .35);
    z-index: 40;
}

.slideover {
    position: fixed;
    top: 0;
    right: 0;
    height: 100vh;
    width: min(420px, 92vw);
    background: var(--card-bg);
    box-shadow: var(--shadow-lg);
    z-index: 41;
    padding: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
    overflow-y: auto;
    outline: none;
    animation: slideInRight .18s ease-out;
}

@keyframes slideInRight {
    from { transform: translateX(100%); }
    to   { transform: translateX(0); }
}

.slideover-head {
    display: flex;
    justify-content: space-between;
    align-items: center;
}

.slideover-title {
    font-family: var(--font-display);
    font-weight: 600;
    font-size: 1.05rem;
    color: var(--text);
}

.slideover-close {
    border: none;
    background: transparent;
    font-size: 1.05rem;
    line-height: 1;
    cursor: pointer;
    color: var(--gray);
    padding: .25rem;
}

.slideover-meta {
    display: flex;
    flex-direction: column;
    gap: .15rem;
    padding-bottom: .5rem;
    border-bottom: 1px solid var(--border);
}

.slideover-actions {
    display: flex;
    gap: .5rem;
    justify-content: flex-end;
    margin-top: auto;
}
```

- [ ] **Step 2: Commit**

```bash
git add src/Plutus.Web/wwwroot/app.css
git commit -m "feat(web): add slide-over + clickable-row styles for transaction edit"
```

---

### Task 5: Manual verification + deploy

**Files:** none (verification + deploy).

- [ ] **Step 1: Run locally and exercise the flow**

Run: `export PATH="$HOME/.dotnet:$PATH" && dotnet run --project src/Plutus.Web`
(needs `ANTHROPIC_API_KEY` in env). On `/transactions`:
- A row shows a pointer cursor + hover highlight; clicking opens the slide-over from the right over a dimmed backdrop.
- The header shows date/description/amount (read-only); the category dropdown and note are editable.
- Clicking the backdrop, the ✕, or pressing Esc closes it without saving.
- Changing the category and/or note and clicking Save closes the panel and the row updates in place; with a category filter active, changing the category to one outside the filter drops the row from the list.

Stop with Ctrl+C.

- [ ] **Step 2: Build the image and deploy**

```bash
sg docker -c 'export PATH="$HOME/.dotnet:$PATH" && dotnet publish src/Plutus.Web -c Release /t:PublishContainer'
sg docker -c "docker compose up -d"
```

- [ ] **Step 3: Verify on prod**

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://plutus.kunigami.cloud
```
Expected: `200`. Open `/transactions` on the live site and confirm clicking a row opens the editor and Save persists.

- [ ] **Step 4: Push**

```bash
git push origin main
```

---

## Self-Review notes

- **Spec coverage:** slide-over interaction (Tasks 2, 4); manual category + note edit (Task 2); read-only header (Task 2); save rule incl. `IsReviewed` untouched + `CategorizedAt`/`IsCategorized` (Task 1, used by Task 2); close via backdrop/✕/Esc (Task 2); in-place row refresh respecting the active filter (Task 3 `OnEditSavedAsync` → `LoadAsync`); self-contained `TransactionEditPanel` (Task 2); vanished-transaction + blank-note edge cases (Tasks 1, 2); save-path test via the extracted helper (Task 1). Out-of-scope items (no AI, no date/amount edit, no bulk) are simply not built.
- **Type consistency:** `TransactionEditor.ApplyEdit(Transaction, int?, string?, DateTimeOffset)` is defined in Task 1 and called identically in Task 2. Panel params (`TransactionId` int?, `Categories` IReadOnlyList<Category>, `OnSaved`/`OnClosed` EventCallback) match the usage in Task 3. `_editingId` (int?) matches `TransactionId`.
- **No placeholders:** every step has concrete code/commands.
