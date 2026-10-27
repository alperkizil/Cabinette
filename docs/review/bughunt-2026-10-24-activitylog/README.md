# Bug hunt, AI activity log in Core (2026-10-24)

**Status: both findings fixed on 2026-10-24** by a fix agent (branch `fix/activity-log`, `525ca83`, merged into main as `882dd62`).
The integrator read the schema change and ran the full suites.

- **Reviewed:** branch `worktree-agent-a26b40c8be3f9cc35` (`08500e2`):
  - `ILibraryService.AddActivityAsync` / `GetActivityAsync` (`LibraryService.Activity.cs`)
  - `ActivityRules.cs`, the rules both stores share
  - `InMemoryActivityLog` (the sample library and test fakes)
  - three indexes on `activity_log`; the prune at open and once a day
- **Reviewer:** Claude Fable 5.1 (review branch `review/activity-log-bughunt`, `40d1602`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:**
  - `tests/Cabinette.Core.Tests/ActivityLogReviewTests.cs`
  - new cases in `ActivityLogTests.cs`

  The fix agent reverted each fix in turn to prove its tests fail without it:
  - F1: 5 tests fail; with only the rebuild reverted, 3.
  - F2: 3 tests fail with only the read-triggered prune reverted; 6 with only the cutoff filter reverted.
- **Verdict on the piece:** solid. The query filters, UTC and DST handling, paging, the limit clamp and the non-ASCII client match are correct and identical in both stores. The lock design holds against Move vault, Migrate, Backup, Restore, dispose and bursts. Appends use their own short transaction, not the library's write lock, which Move vault and Backup hold for minutes.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 2 (1×2) | Ids were reused once the prune emptied the table: `activity_log.id` had no `AUTOINCREMENT`, so after a year without AI calls the next entry got id 1 again. That contradicts "ids grow with time" and the in-memory log. | New libraries get `AUTOINCREMENT`. `Database.RebuildActivityLogWithAutoincrement` gives an older table the same definition when the library opens, inside the initialize transaction, keeping every row and id; the schema script recreates the indexes. No schema version bump, so earlier builds still open the library and its backups. |
| 2 | 2 (1×2) | The daily prune ran only from an append, so a Cabinette in the tray with no AI calls kept showing entries older than a year. | A read runs the same cheap check, with the prune in the background. Neither store ever returns an entry older than the retention cutoff, pruned or not. |

## Probes that held (kept as tests)
- **P1:** disposing during a prune of 5001 old rows (6 batches) leaves nothing holding the database. A restore can rename it, and the next open finishes the prune.
- **P2:** a clock that steps back runs exactly one extra prune, then waits a day again.

## Checked by the reviewer's own experiments (not committed)
- **The two stores agree:** the SQL store and `InMemoryActivityLog` were compared over 300 entries and 3000 random queries: offsets from −12 to +14, DST, and client names such as İ/ı, ß/SS, ǅ, σ/ς/Σ and Ç/ç. There were 0 differences.
- **Speed:** at 400,000 rows the viewer's first page took 11 ms cold. The worst filter combination (an old month plus six change tools) took 123 ms.
- **Appends during a backup:** while a 217 MB database was copied with `VACUUM INTO` (770 ms), 66 appends committed, none waited more than 3 ms, and the snapshot held exactly the rows committed before it.

## Leads the reviewer dropped (kept for the record)
- **A busy timeout reaching callers:** that would need another process holding the write lock for over 40 s. The app's `AiActivityLog` catches and logs append failures.
- **Dispose while a read opens a pooled connection:** this is pre-existing for every read, not part of this piece.
- **A clock set far ahead prunes entries early:** inherent to time-based retention.
- Also checked and fine: leap years; the text collation of `at`; escaping names inside `json_each`.

## Left as they are
- A table the prune had already emptied before the rebuild lost its largest id, so its ids can restart at 1 once. Every shipped build's table is empty, so this has no effect.
- The F2 fix commit's message still carries a "WIP" note from the usage-cap stop; the work it lists was finished in the same branch.
