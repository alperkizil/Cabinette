# Bug hunt, Clean My Downloads mode (2026-10-22)

**Status: all 10 findings fixed on 2026-10-22** by a fix agent (branch `worktree-agent-a76eb61fc0c35bcc8`, merged into main).
The integrator then changed one fix (see #8).

- **Reviewed:** branch `worktree-agent-a037662ac81f0cc57` (`e4a8f6b`, `9eb1061`, `625acf9`):
  - the Downloads cleaner (`Core/Downloads/*`)
  - the Downloads known-folder lookup
  - the app host, confirm, notifier and the `--downloads-dir` dev flag
- **Reviewer:** Claude Fable 5.1 (review branch `bughunt-downloads-fable`, `25b823e`), with temp folders and a fake Recycle Bin only.
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md). A file recycled without a
  vault copy counts as 4, since the Recycle Bin can restore it.
- **Regression tests:** `tests/Cabinette.Core.Tests/BugHuntDownloadsTests.cs`, `MarkOfTheWebTests.cs`,
  `tests/Cabinette.App.Tests/BugHuntDownloadsChoiceTests.cs`, and `tests/Cabinette.Windows.Tests/FinalPathsTests.cs`. The fix agent
  reverted each fix in turn to prove its tests fail without it.
- **Verdict on data safety:** sound. No way was found to recycle a download without a committed vault copy, through races,
  duplicates, found-again re-linking, Move vault, pause, shutdown or the dev flags.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 12 (4×3) | The Mark of the Web (`Zone.Identifier`) was stripped from every copy into the vault, so SmartScreen and Office's Protected View no longer applied once the marked original was recycled. This also applied to every manual copy add. | `FileOps.CopyAndHashAsync` copies the stream: every copy add, found-again re-link, Migrate and Move vault keep it. A vault on FAT has no streams: the copy goes on without it, logged. A backup `.zip` can't hold it (known gap). |
| 2 | 4 (4×1) | A duplicate was recycled against the item's stored hash, never against the vault file's actual content. | The vault copy is read back whenever this pass didn't just write it; a mismatch keeps the original. |
| 3 | 4 (4×1) | The vault copy wasn't flushed to disk before the original was recycled (power cut right after). | `Flush(flushToDisk: true)` on the copy and its mark, for every copy add. |
| 4 | 4 (2×2) | A one-off Recycle Bin hiccup kept the file in Downloads for good. | Only a real refusal keeps a file; other failures are retried with a growing delay, holding the watermark back. |
| 5 | 3 (3×1) | In-place downloads with an unknown partial suffix moved half-done after a stalled minute. | Added `.!qB .!ut .!bt .bc! .az! .fdmdownload .jc! .td`, plus an aria2 sibling rule (`name` waits while `name.aria2` exists). |
| 6 | 3 (3×1) | `--downloads-dir` through a junction reached the real Downloads folder that the guard refuses by name. | New `Cabinette.Windows.FinalPaths` (`GetFinalPathNameByHandle`): the guard and the vault-inside check compare final paths. |
| 7 | 2 (2×1) | A lock appearing during the add cost a second full copy and a misleading "already in Cabinette". | The stored item is remembered; the next pass finishes from it. |
| 8 | 2 (2×1) | A file changed during its add left a stale extra item in the vault. | `LibraryService.DiscardStaleAddAsync` removes that item only while untouched (same content, no tags, star or other use). **Changed by the integrator:** its vault copy goes to the **Recycle Bin**, not a permanent delete. It is usually a half copy, but if something saved a different file over the original, it is the only one left. Where the Recycle Bin can't take it, the item stays. |
| 9 | 2 (2×1) | A vault copy failing its check was retried forever with a full copy each time. | Growing delay (5, 10, 20 min, capped at 1 h). After 3 attempts the file stays, with a notice (`VaultCopyUnchecked`). |
| 10 | 2 (2×1) | An abandoned partial download pinned the watermark, so kept files were re-announced at every start. | A partial untouched for a day stops holding the watermark. |

**Decided by the user (2026-10-22):** files copied into Downloads from another drive, or extracted there, are treated as
downloads too. The setting's hint now says that many viewers don't keep a file open, so a file you're only viewing can still move.

## Leads the reviewer dropped (kept for the record)
- **Downloads redirected to OneDrive:** cloud placeholders are skipped. If hydrated files also carry the attribute, the mode would do nothing there. Not testable here.
- **FAT32/exFAT Downloads across a DST change:** a file from the hour before switch-on can read as newer.
- **Restore on another PC with the setting on:** the mode runs without its confirm, since `state.json` isn't in the backup.
- **Watcher after an error** (the folder deleted and recreated): it isn't recreated; the 5-minute scan still runs.
- **XXH128 isn't cryptographic:** a crafted collision could recycle a download against a different file (dedupe design).
- **Torrent clients that write without a suffix** (qBittorrent's default, Transmission, Deluge): protected only by the open-file check and the quiet minute.
