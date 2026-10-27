# Bug hunt, sort by type and Move to category (2026-10-22)

**Status: all 5 findings fixed on 2026-10-22**, after the merge of the branch.

- **Reviewed:** branch `worktree-agent-a90e17c5bcf445f5e` (`6abd784`), two features:
  - "type categories sort by type" (`AddRequest.SortIntoTypeCategories`, `TypeSorting`, `FileIntake`, `TypeSortOutcome`)
  - "Move to category…" (`ILibraryService.MoveToCategoryAsync`, the thread menu and selection bar)
- **Reviewer:** Claude Fable 5.1, with repro tests against a real library and the real vault monitor.
- **Verification:** the integrator (Claude Opus 5.5) re-checked each finding against the code before fixing it.
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.Core.Tests/BugHuntTypeSortMoveTests.cs`, and
  `tests/Cabinette.App.Tests/BugHuntTypeSortMoveTests.cs` (which also covers the thread's move wiring, untested before).
- **Verdict on the core:** solid. `MoveToCategoryAsync` could not be made to lose, duplicate or orphan a file. The reviewer tried:
  - a failure on the 3rd of 5 files (the first two go back)
  - case-only and long-name clashes, a target folder that is gone
  - nested parents and ancestors, Deleted, search
  - the vault monitor hashing or waiting on a locked file in the same folder
  
  The monitor stays quiet because the reconcile plan is rebuilt under the write lock.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 3 (3×1) | A bubble dragged out of a thread onto a type category's row in the chat list was re-added as files, so it was sorted by type: a photo dropped on the Spreadsheets row went to Images. | The thread's drag now carries the item ids (`ThreadView.InternalDragFormat`). A row adds those items to its category, like "Add to category…" (`MainViewModel.AddItemsToCategoryAsync`). The list background refuses bubbles, like the window and the thread. |
| 2 | 2 (2×1) | Move refused a file that was merely being read (the monitor hashing it, a viewer), while Rename of it worked: the pre-check opened it with no sharing. | The lock pre-check is gone. A program holding the file without delete sharing still fails the move with "in use" (mapped from the sharing violation), and the write is undone. |
| 3 | 2 (1×2) | A drop on another category's row said "N stayed here" about files that stayed in that category, not in the thread on screen. | It says "N stayed in Spreadsheets" unless the target is the open thread. |
| 4 | 2 (1×2) | A file already in its type category, dropped on another type category, was announced as "sorted by type into Images", and Images opened, although nothing changed. | A duplicate counts as sorted only when it joined the category. |
| 5 | 2 (2×1) | Out of Deleted, with the file gone from the Deleted folder: Move refused, but "Add to category…" took the item out anyway, pointing at nothing. | Both refuse ("could not be found"). |

Also on 2026-10-22, decided by the user: **Notes keeps text files** (`.md`, `.txt` and the other Text types) dropped on it;
other types dropped on Notes still sort.

## Leads the reviewer dropped (kept for the record)
- A 258-character name after a clash fails with Win32 206, mapped to `Unknown` ("The filename or extension is too long"): harmless.
- `Rehome` updating the row when the file vanishes between the check and the move: microseconds, same write.
- `is_missing` not cleared by a move of a file that is there: the next monitor batch clears it.
- The notice count includes stale ids; "Show" after a multi-file move scrolls to the end (as Add to category does): cosmetic.

## Not covered
Real on-screen interaction: the classic menu entry, a real drag onto a row. Also the AI add path (stage 4), and a vault
on an unplugged drive during a move.
