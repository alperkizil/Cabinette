# Bug hunt, the app's side of the "ai" command (2026-10-24)

**Status: all 9 findings fixed on 2026-10-24** by a fix agent (branch `fix/ai-commands`, `d604a5b`, merged into main as `24b8779`).
The integrator read the one Core change (F4) and ran the full suites.

- **Reviewed:** branch `worktree-agent-ad9c248136bb1b0ca` (`9f1a158`):
  - `Services/Ai/AiCommand*.cs`: every MCP tool through `ILibraryService`, logged and reported
  - `AiCommandGate` (not_ready until the library is open with a vault)
  - the pipe server's per-line timeouts (`InstanceChannel.cs`)
  - AI-run Move vault, Migrate and Backup in `VaultOperations.cs`, with a status registry
- **Reviewer:** Claude Fable 5.1 (review branch `review/ai-commands-bughunt`, `896822d`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.App.Tests/AiCommandReviewTests.cs`, plus Core tests for F4. The fix agent reverted each fix in turn and saw its tests fail.
- **Verdict on the piece:** the guardrails that matter hold:
  - nothing an AI can call deletes permanently (every path checked, including both Recycle Bin refusals)
  - an unknown id changes nothing
  - busy and not_ready are gated correctly
  - the pipe server's new timeouts are sound

  The defects were about matching the UI's rules and reporting what really happened.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 7 | 8 (4×2) | Replies awaited the log write. If the log write had waited for the library's write lock, every reply would stall during Move vault and Backup. | Already not the case with the merged Core log, which uses its own short transaction. The test now uses the real library with a paused Move vault and a paused Backup: reads, busy refusals and status are answered at once and logged. |
| 1 | 6 (3×2) | `rename_category` renamed the Deleted system category and its folder; the UI hides Rename for it. | Refused. |
| 3a | 6 (3×2) | `move_files` from a category the files aren't in silently added them, while the reply, toast and log said "moved". | `invalid_argument`, naming the ids, unless every file is in the source category or one of its subcategories. |
| 2 | 4 (4×1) | Device-path spellings (`\\?\`, `\\.\`) got past the "not inside the vault" checks: an AI backup was written inside the vault. | Device paths are refused for move_vault and create_backup. The checks also compare the final paths. 8.3 names and `..` were already expanded by `GetFullPath`; tests now pin that. |
| 3b | 4 (2×2) | Tag, untag, add to category, remove from category and others reported every id as changed, with a toast, when nothing changed. | The real number changed is reported; 0 means no toast. |
| 8 | 4 (2×2) | `remove_from_category` on a file's only category sent it to Deleted without a word. | Refused (using the UI's own rule, `ThreadViewModel.IsOnlyInThread`); the message points to delete_files or move_files. |
| 4 | 2 (2×1) | Any delete, an AI's included, forgot the library's remembered late Recycle Bin refusal. The user's open "Delete permanently?" could then silently do nothing. | Core keeps refusals per delete (up to 8). Only the same delete takes its refusal: its confirmed retry uses it, and the same delete without "permanent" drops it. Two Core tests that encoded "any delete ends it" were turned around. Permanent deletes still need the user's confirmed retry of the same delete. |
| 5 | 2 (2×1) | AI tags dropped format characters (ZWJ emoji), unlike the tag editor, so they didn't match the user's tags. | Normalized with `TagEditorViewModel.Normalize`. Tags over 200 characters are refused. |
| 6 | 2 (2×1) | Queries, paths and errors went into the year-long log in full (200 KB verified). | Each text field the log stores is capped at 2,000 characters plus "…", and lists at 50. Replies to the AI are unchanged. |

## Leads the reviewer dropped (kept for the record)
- **Permanent deletes:** every path was checked; `RecycleOnly` never reaches a permanent delete.
- **The busy check racing a vault operation starting:** the library's write lock serializes them, so the change applies after the move.
- **The UI's Move vault doesn't re-check busy after its dialogs** while an AI operation starts. Pre-existing; the library serializes them.
- **not_ready:** the gate ordering in startup, first run, cleanup and exit is correct. A library disposed mid-call (Restore) answers not_ready.
- **Deadlocks:** none (no synchronous UI-thread wait on a pipe handler).
- **Pipe server:** long handlers, mid-call disconnects, pipelined lines and the 32 M-character limit all behave. There's no cap on concurrent connections (same user only).
- **`add_files`** accepts the vault and drive roots, like a drop.

## Left as they are
- The final-path comparison doesn't see through loopback admin shares (`\\localhost\C$\…`). An AI naming the vault that way could start a move into the vault itself, which stops after its catch-up rounds ("kept changing") without losing anything. A backup could be written inside the vault.
