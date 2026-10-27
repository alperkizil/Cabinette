# Bug hunt, stage 1 + 2 (2026-10-15)

**Status: all 13 fixed on 2026-10-21**, as planned in [FIX-PLAN.md](FIX-PLAN.md) (the user confirmed every default).
- Merges into `main`: group A `4c8c387` (#1, #2, #3, #4, #11), group B `bf416f2` (#5, #6, #7, #9, #10, #12), group C `a1257cf` (#8, #13).
  Integration fixes after the merges: `d6a69e4`, `fb9002f`, `1fd7a64` and the commit that updated this file.
- `main` before the fixes is tagged `before-bughunt-fixes`.
- Each repro test below became a regression test that asserts the fixed behavior: `BugHuntMoveRestoreTests`, `BugHuntAddMonitorTests`
  and `BugHuntDeleteFirstRunTests` in the Core, Windows and App test projects. The `*.cs.txt` files here are kept as the record of the bugs.

## How it was done
- **Reviewers:** four independent reviewers covered the whole codebase on `main` at `abf0af5`, with the same brief:
  - Claude Opus 5.5 and Claude Fable 5.1, which could write repro tests
  - GPT Astra 6 (`xhigh`) and GPT 6.1 Sol (`high`), static only, through the Codex CLI in a read-only sandbox
- **Verification:** each finding was re-checked by a model other than the one that found it, which tried to disprove it:
  - the ChatGPT findings by Claude Opus 5.5
  - Opus's solo findings by Fable 5.1
  - Fable's solo findings by Opus 5.5
- **Repro tests:** every survivor has at least one, and every test passed on `main`. The integrator re-ran the ChatGPT verifier's tests and
  checked that the test hooks they use already exist in `src/`.
- **Scoring:** Impact (1–5) × Likelihood (1–3), judged for one normal user of this personal app.
  - Impact: 5 = files lost with no way back; 4 = crash, security hole or recoverable loss; 3 = a wrong result you may not notice;
    2 = visible glitch or slowness; 1 = cosmetic.
  - Likelihood: 3 = daily use; 2 = a specific but plausible situation; 1 = unusual timing or combinations.
  - A finding with several scenarios gets the highest scenario score.
- **Result:** no finding was dropped outright. Four claims were narrowed (see "Partly"). Most bugs need unusual timing, so scores are low.
  - None of them loses data in normal daily use.
  - The two Claude reviewers also dropped their own unprovable leads before reporting: pipe-name squatting, SQLite id reuse,
    and a vault inside the data folder.

## Findings, by score

| # | Score | I × L | Finding | Found by |
|---|---|---|---|---|
| 1 | **6** | 3 × 2 | After **Move vault**, the open thread keeps old file paths: Open, Show in folder and Copy act on the old copy, so edits go to the old vault | Sol, Opus |
| 2 | **5** | 5 × 1 | A **cancelled or failed Move vault / restore** empties the whole destination folder permanently, including files the user put there meanwhile (a name collision can both cause the failure and delete the user's file) | Sol, Astra |
| 3 | **4** | 4 × 1 | A **crafted backup** (someone else's .zip) can make restore create folders outside the chosen vault and the monitor move a bundled .exe there (e.g. the Startup folder). Zip entry names are checked; database folder names aren't | Sol, Astra |
| 4 | **4** | 4 × 1 | **Move vault** verifies the copy against itself: files edited or added in the old vault during the move are missing from the new one, and go to the Recycle Bin if the user then recycles the old vault | Sol, Astra |
| 5 | **4** | 2 × 2 | The **"missing" mark on vault files gets out of sync**. A file marked missing while the drive was unplugged stays "missing" after it returns. Rarer worse cases: re-adding it duplicates the file; a file deleted while monitoring is paused can't be re-added (dropped as a duplicate, then removed) | all four |
| 6 | **4** | 2 × 2 | **Renaming a category while a file is being copied into it** fails with a misleading "Access to "Trip" was denied". Rollback is clean; retrying later works | Fable |
| 7 | **3** | 3 × 1 | An **add still copying when Move vault starts** finishes into the old vault; its item points at the new vault and is removed by the next rescan (the source original survives) | all four |
| 8 | **3** | 3 × 1 | When the shell refuses a file **after** the commit (too big for the Recycle Bin), **"Delete permanently" does nothing**: the rows are already gone, so the retry finds nothing. A vault file comes back untagged; a pointer's real file stays | Sol, Astra, Opus |
| 9 | **3** | 3 × 1 | **Renaming a folder and a file inside it while Cabinette is closed** (or paused) makes every full rescan fail with a foreign-key error and roll back, silently, retried every 30 s | Astra |
| 10 | **3** | 3 × 1 | A **pointer whose real file was edited elsewhere** keeps its old hash, so adding a file with the old content is discarded as "Already in Cabinette" | Astra |
| 11 | **3** | 3 × 1 | **Restore** drops Hidden/System on files and folders, **Move vault** on folders, so the monitor then imports e.g. a `.git` folder as a category and hidden album art as items | Opus |
| 12 | **2** | 2 × 1 | **Quitting or crashing mid-copy** leaves a hidden `.cabinette-*.tmp` (possibly GBs) that nothing ever removes | Opus |
| 13 | **2** | 2 × 1 | **First run accepts `%LocalAppData%`** (after "Use this folder anyway"), so Cabinette's own thumbnails and logs become a category. The live database is never touched | Fable |

### Narrowed claims ("Partly")
- **#3:** crafted item file names are harmless; the first rescan drops them. Only the folder names matter.
- **#6:** the reviewer said the "folder renamed while copying" code can never run. It can, in a narrow window, and fails cleanly there.
- **#11:** Move vault keeps file attributes; only folders lose theirs.
- **#13:** the Move vault part is false, because it requires an empty folder. First run only gets there by browsing into the hidden
  AppData folder and overriding the "not empty" warning.

## Fix groups (shared root causes)
- **Move vault robustness:** #1, #2 (move part), #4, #7.
  - Possible fixes: block adds during a move, re-check the vault inside the add's write, reload the open thread, and verify against the
    source at the end.
- **Restore robustness:** #2 (restore part), #3, #11.
  - Possible fixes: clean up only what the operation created, validate folder and file names from the restored database, and keep attributes.
- **Copy staging:** #6, #7, #12.
  - Possible fix: copy into one staging folder (e.g. `vault\.cabinette-tmp`) outside the category folders, move the file in under the
    write lock, and sweep that folder at startup.
- **Missing and duplicate checks:** #5, #10.
  - Possible fix: check the disk for vault items too, clear the mark when the monitor sees the file, and re-hash a pointer whose size or
    time changed before calling it a duplicate.
- **Delete fallback:** #8. Possible fix: return the refused paths and delete exactly those, instead of replaying item ids.
- **Reconciler:** #9. Possible fix: one plan item per database id.
- **First-run guard:** #13. Possible fix: reuse restore's "can't contain the data folder" check.

## Files here
- `gpt-sol.md`, `gpt-astra.md`: the two ChatGPT reports as written.
- `*.cs.txt`: all repro tests, which assert the buggy behavior.
  - Reviewers' tests: `OpusReviewer.*`, `FableReviewer.*`.
  - Verifiers' tests: `VerifyGpt.*` (G1–G9), `VerifyOpusFindings.*` (temp files, attributes), `VerifyFableFindings.*` (rename during copy,
    data folder).
- When a bug gets fixed, turn its test into a regression test that asserts the fixed behavior.
