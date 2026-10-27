# Bug hunt, stage 3 Explorer entries (2026-10-21)

**Status: all 3 findings fixed on 2026-10-21**, after the merge of the Explorer branch.

- **Reviewed:** branch `worktree-agent-a96858682f68c55f5` at `887972a`. Covers:
  - the COM local-server drop target (`ExplorerDropServer`, `DataObjectPaths`, `HDrop`)
  - the queue that holds selections until the library is open
  - the forwarder that passes a selection on over the local channel's `add` command
  - the single-instance change (an inaccessible mutex now means "already running")
  - the dev registration scripts
- **Reviewer:** Claude Fable 5.1, with repro tests. It never registered COM classes, ran the dev scripts or ran the app.
- **Verification:** the integrator (Claude Opus 5.5) re-checked each finding against the code. Both repro tests failed on
  the merged code before the fixes and pass after them.
- **Scoring:** as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md): Impact (1–5) × Likelihood (1–3).
- **Regression tests:** `tests/Cabinette.App.Tests/BugHuntExplorerTests.cs`.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 6 (2×3) | With Cabinette's window open but behind Explorer, an Explorer entry's result went to the in-app snackbar, which dismisses itself after 4–7 s unseen. The vault monitor toasts in the same situation. | Both notifiers share one rule, `NotificationService.IsSeen` (window shown and active). Anything else gets a toast. |
| 2 | 3 (3×1) | "Restore from a backup" on the first-run screen restarts Cabinette, and the restart dropped Explorer selections that were waiting for a vault. SPEC 9 says they're added once a vault is chosen. | On a restart the cleanup keeps waiting selections in `explorer-pending.json` in the data folder. The next start queues them again, checked like a local-channel `add`, then deletes the file. A file older than 10 minutes is ignored. |
| 3 | 2 (2×1) | Over the forwarder path (Cabinette running as administrator), a selection of about 45,000+ files was one JSON line over the 4 M character limit; the running copy dropped it without a reply. | `InstanceProtocol.MaxLineLength` is now 32 M characters, a few hundred thousand paths. |

Also renamed a test whose name claimed more than it checks: it covers the thread-pool fallback of an unregistered server,
not the dispatcher path the app uses (that one is in the opt-in `ExplorerComRegistrationTests`).

## Checked and found sound (by the reviewer)
- **COM:** reference counting in `CreateInstance`, interface vtable orders, `POINTL` by value, HRESULTs, releasing the
  medium and RCWs in `Drop`, the IShellItemArray fallback, and handlers posted after `Drop` returns. `CoRevokeClassObject`
  runs on every exit path; registration failure falls back to the forwarder.
- **Races:** drops before or while the library opens and during first run; two drops at once; the forwarder and a
  starting primary in either order; adds during Move vault.
- **Mutex change:** a normal second launch still activates the running copy; `--tray` still exits quietly; `--data-dir`
  runs keep their own names.
- **Data safety:** no double adds; the mode comes from the class; dev runs only serve the dev classes and the real app
  only the production ones. The dev scripts write and remove exactly their four HKCU keys.

## Leads the reviewer dropped (kept for the record)
- **Closed queue:** `Drop` returns success even if the queue is closed. The window is microseconds, because cleanup
  revokes the classes first.
- **Startup error dialog:** after a startup failure, selections are still accepted while the error dialog shows, then
  discarded.
- **Two toasts in a row:** clicking the first opens the second's category. Cosmetic, pre-existing.
- **Explorer add during Restore from Settings:** shows "Something went wrong", the same as a drop onto the window.
- **Register script:** `GetFullPath` on a relative `-DataDir` resolves against the process directory. Nit; it never
  touches the real data folder.
- **Forwarder:** "ok" means "taken", so if first run is then closed, nothing is added silently. Same family as #2,
  rarer.

## Not covered
Real Explorer and COM activation, and a real elevated Cabinette. These are covered by the manual checks (dev entries on
the desktop, then the installed app).
