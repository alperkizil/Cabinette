# Stage 1 bug hunt (Fable 5.1 reviewer, 2026-10-09)

- **Reviewer:** an independent Fable 5.1 agent.
- **Method:** read-only review of `main` at commit f472e29.
- **"Confirmed"** means it was reproduced by a test in `ReviewReproTests.cs.txt` (kept next to this file). Copy those tests into `tests/Cabinette.Core.Tests` when fixing, so they become regression tests.

**Status: all 22 fixed on `main` (2026-10-09).**
- Core regression tests: `tests/Cabinette.Core.Tests/ReviewRegressionTests.cs` (`F<n>_…`).
- App and Windows tests are in `tests/Cabinette.App.Tests` and `tests/Cabinette.Windows.Tests`.
- The behavior choices are recorded in SPEC.md as **[my default]**.

## High
1. **A live pointer gets folded into a missing one.**
   - Where: `LibraryService.Adding.cs` `ProcessPendingHashAsync`; `LibraryService.Items.cs` `FindByHash` ignores `is_missing`.
   - Background hashing deletes a newly added live pointer when its hash matches an existing pointer marked missing. The user's add vanishes.
   - Confirmed.
   - Fix: on a hash match with a missing pointer, relocate that item to the new path instead.
2. **A missing pointer blocks re-adding the same content.**
   - Same root cause, on the synchronous add path (copy or pointer).
   - The user sees "Already in Cabinette", the existing item is the dead one, and nothing is added.
   - Confirmed.
   - Fix: treat it as an automatic re-link ("found again"), not as a duplicate.

## Medium
3. **Choosing a non-empty folder as the vault adopts existing subfolders** (e.g. `Images\`). Deleting that category later recycles the user's own files.
   - Confirmed.
   - Fix: warn or require an empty folder on first run; number the folder if a stray one exists when a category is created; on category delete, recycle only known files and leave foreign ones.
4. **Category delete with "Delete files" also recycles pointer originals,** but the dialog doesn't say so.
   - Fix: reuse the item-delete consequence text in that dialog.
5. **On drives without a Recycle Bin** (network shares, some USB sticks), deletes always fail with default settings.
   - Fix: offer "No Recycle Bin here, delete permanently?" and retry.
6. **Performance: opening a thread reloads too much.** `MarkCategoryViewedAsync` raises a Categories change, which reloads the thread twice and all category icon blobs.
   - Fix: a narrower change kind; `EnsureDefaultIconsAsync` should query only `icon_png IS NULL`.
7. **The "Notes" type filter never matches notes created in Cabinette** (they are `.md` files, so they count as "text").
   - Confirmed.
   - Fix: `kind = 1` for notes, `kind = 3` for links, `kind = 2` for images.
8. **Ctrl+V in the composer when the clipboard has text plus a bitmap** (e.g. Excel cells) adds a "Pasted image" instead of pasting the text.
   - Fix: a focused text box with Unicode text wins.
9. **Large pasted images are decoded and encoded on the UI thread,** plus up to about 375 ms of retry sleeps.
   - Fix: do the decode/encode in `Task.Run`.
10. **`DeleteItemsAsync` deletes files before the transaction commits,** and inside the write lock.
    - Fix: commit the rows first, then delete files after the commit (like category delete does).

## Low
11. **Renaming a pointer while it is being hashed** leaves it unhashed until the next start. Confirmed.
12. **Deleting a category while a copy into it is in flight** leaves an empty stray folder. Confirmed.
13. **Files can be added straight into "Deleted"** (by dropping on its row or thread). Confirmed.
14. **The thread filter's extension box doesn't strip `*`** (`*.pdf` matches nothing). Confirmed.
15. **The shell menu intercepts Delete and Rename but not Cut.** Cut + paste in Explorer moves a vault file out, and the item becomes missing.
16. **Confirming a thread dialog after switching threads silently does nothing** (the old view model's lifetime cancels the call).
17. **The "more results" note can never show** (Core caps at 500, and the view model checks `> 500`).
18. **A mixed selection with "Remove from this category"** silently moves single-category items to Deleted; the dialog doesn't say so.
19. **The thumbnail loader caches transient exceptions** as permanent misses.
20. **The thumbnail disk cache is never pruned.**
21. **Main-window paste re-creates a deleted built-in category** (Notes/Links/Images) before the picker opens, even if you cancel.
22. **A case-only category rename** goes through a temp name that isn't in the undo journal. Rare.

## Reviewer's overall view
- Code health is good:
  - Every write is one transaction with a file-operation undo journal.
  - `File.Move` never overwrites.
  - Temp-file cleanup only touches Cabinette's own `.cabinette-*.tmp` files.
  - Recycle Bin semantics are enforced.
  - COM, HBITMAP and PIDL lifetimes are balanced, and lists virtualize.
- No permanent data-loss path was found outside the user's explicit "permanent" setting.
- Not covered:
  - Driving the WPF UI.
  - The opt-in Windows tests.
  - Most XAML templates.
  - Long paths (over 260 characters).
  - Junctions and symlinks in the vault.
