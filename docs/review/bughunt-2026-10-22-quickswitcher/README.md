# Bug hunt, Ctrl+K quick switcher (2026-10-22)

**Status: all 4 findings fixed on 2026-10-22** by a fix agent (branch `worktree-agent-a29edc41765a24549`, merged into main).
The integrator reviewed the result and ran the full suites.

- **Reviewed:** branch `worktree-agent-a278b9bc221243bd3` (`e7132df`):
  - the quick switcher (`QuickSwitcherViewModel`/`View`, `RecentCategories`, `ItemOpener`)
  - Ctrl+K in `MainWindow`
  - the top-aligned dialog style
  - the album-cell flash
- **Reviewer:** Claude Fable 5.1 (review branch `review/quick-switcher-bughunt`, `87d53cf`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:**
  - `tests/Cabinette.App.Tests/QuickSwitcherReviewTests.cs`
  - `tests/Cabinette.Core.Tests/NamePrefixSearchTests.cs`
  
  The fix agent reverted each fix in turn to prove its tests fail without it.
- **Verdict on the switcher itself:** sound. The key decision, debounce and cancellation, the recent list's persistence and `ItemOpener` all hold. The defects were in landing on a file afterwards.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 4 (2×2) | Enter on a file hidden by the thread's search or filter scrolled to it, then the debounced reload jumped to the bottom 250 ms later. With a slow load it never scrolled at all. | `FocusItem` stops the debounce timer and keeps the request until a reload that lists the file is applied; that reload scrolls to the file instead of the end. The newest request wins. |
| 2 | 4 (2×2) | A photo behind an album's "+N" tile had no cell, so it never flashed. | `AlbumRow.CellShowing` gives the "+N" tile, which flashes. Also fixed: after live updates the album rows on screen weren't the ones the thread tracked, so an album's selection check mark went stale (pre-existing). |
| 3 | 3 (3×1) | Ranking only saw the main search's newest 500 matches, so an older exact or starts-with match could be missing. | New additive `ILibraryService.SearchNamePrefixAsync`: exact and starts-with name matches, through the trigram index. The switcher adds them when the main search has more than 500. |
| 4 | 2 (2×1) | Ctrl+K left the thread's filter popup floating over the switcher. | Opening any dialog closes both filter popups (`MainViewModel.CloseFilterPopups`). |

Also fixed: a flaky test of the recent list, which polled `state.json`. `AppStateStore` now writes in order in the background, with an internal `WhenSavedAsync` for tests; before, a read during a write could make that write fail for good. And the timing-based motion tests now run without parallelism (`MotionTiming`).

## Leads the reviewer dropped (kept for the record)
- **Keyboard:** Ctrl+K steals nothing, key repeat is swallowed, and dialogs can't stack.
- **Async:** stale results can't overwrite newer ones.
- **Navigation:** a deleted home category can't occur; Deleted items open the Deleted thread.
- **`state.json`:** isn't written atomically, so a crash mid-write resets it (1×1). After restoring a backup from another library, the recent ids point at other categories (2×1).
- **Word-start matches** ("inv" finding "2026 invoice.pdf") beyond the first 500 are still not fetched; only exact and starts-with are.
