# Bug hunt, Starred files (2026-10-22)

**Status: all 3 findings fixed on 2026-10-22**, after the merge of the branch.

- **Reviewed:** branch `worktree-agent-a8c3088e26809a38d`, commits `974f86b` (core) and `8cca83e` (UI):
  - the `item_stars` table (no schema version bump, so older builds still open the library)
  - the star API
  - the "Starred" chat-list row and the read-only Starred view
  - Star/Unstar actions and the star markers
- **Reviewer:** Claude Fable 5.1, with 14 repro tests on the real library and the thread view model, and off-screen renders.
- **Verification:** the integrator (Claude Opus 5.5) re-checked the findings against the code.
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.Core.Tests/StarredReviewTests.cs`, `tests/Cabinette.App.Tests/StarredReviewTests.cs`.
- **Verdict:** no defect above score 2.
  - **Data:** every `DELETE FROM items` notes stars. Renames, moves, relinks, migrate, Move vault and the hash merge keep the id or hand the star on. Foreign keys are on for every connection, so the cascade and the prune on open cover id reuse.
  - **UI:** live updates reach the view; the forbidden actions are unreachable by menu, bar, keyboard, drop and paste.
  - **Performance:** 50,000 stars load in about 250 ms.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 2 (1×2) | In the Starred view, the details layout sorted and dated rows by "Date added", while the chat and the grid use the star time. | The date column reads "Starred" there, and sorts and shows the star time. |
| 2 | 1 (1×1) | The sample library (dev builds) reported a category rename for that category only, so a Starred view showing a file from a subcategory kept a stale path. | It reports the whole subtree, like the real library. |
| 3 | 1 (1×1) | The thread harness's fake raised one star event per id, also when nothing changed. | One event with exactly the changed ids, like the real library. |

## Leads the reviewer dropped (kept for the record)
- An older build opening or restoring a starred library: safe by construction. The cascade lives in the table's own DDL, the older build also turns foreign keys on, and restore copies the database file as is.
- A second click on Star before the reload: a no-op, starred once.
- "Select all → Unstar" leaves select mode with 0 selected: the same pattern as delete-all, pre-existing.
- Chat-list motion when the Starred row appears: the planner is LIS-based, so no jumpers; reasoned, not rendered live.

## Not covered
`MainViewModel.OpenStarredAsync` end to end (it needs a full main view model), the Windows classic menu's Cabinette items,
light-theme renders of the new glyphs, and the live chat-list animation.
