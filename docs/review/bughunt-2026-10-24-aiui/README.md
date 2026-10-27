# Bug hunt, the AI UI: grouped toasts and Settings › AI (2026-10-24)

**Status: all 5 findings fixed on 2026-10-24** by a fix agent (branch `fix/ai-ui`, `368561a`, merged into main as `777fd60`),
together with two integrator notes. The integrator looked at the screenshots and ran the full suites.

- **Reviewed:** branch `worktree-agent-add1d28fb67aed923` (`1ce11c1`):
  - `AiChangeNotifier` / `AiChangeText`: the toasts, merged per client like the vault monitor's
  - Settings › AI: the connect commands with Copy, and the activity log viewer (`AiActivityViewModel`, `AiActivityText`, `AiActivityInlines`)
  - screenshot states and sample activity
  - `ScreenshotRunner.PrepareOffscreen`'s `WS_EX_NOACTIVATE`: off-screen screenshot runs used to take the foreground, a pre-existing issue the feature agent found and fixed
- **Reviewer:** Claude Fable 5.1 (review branch `review/ai-ui-bughunt`, `78e4948`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.App.Tests/AiUiReviewTests.cs`. The fix agent reverted each fix in turn and saw its tests fail.
- **Verdict on the piece:** solid. The toast merging, the wording (every kind and plural), the threading, and the viewer's paging and live updates are careful and well tested.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 2 | 6 (3×2) | Keyboard users reached only the first link of the activity log: Tab went to it, then out of the list. | `TabNavigation="Local"` on the list. A focused link also creates the rows on either side and scrolls into view, since Tab outran the virtualized list under load. Links carry their item's name. |
| 1 | 3 (3×1) | One entry whose details had a `null` file or category blanked the whole activity log for as long as it lived (a year). | Null references are dropped. A row that still can't be described shows the tool name, client and time. |
| 3 | 3 (3×1) | PowerShell expands `$name` and treats the backtick as an escape inside the double-quoted paths, so such a path silently registered a wrong one. | `$`, `` ` `` and typographic quotes are escaped for PowerShell, and the hint says "in PowerShell". One text can't serve cmd too (cmd keeps backticks and expands `%NAME%`); paths without those characters work in both. Tested by letting powershell.exe and cmd.exe parse the exact text, with a stand-in echo program instead of claude/codex. |
| 4 | 2 (2×1) | Any error opening a link said the file "is no longer in Cabinette". | Only a missing item means gone; errors say "couldn't be opened" and why. |
| 5 | 2 (1×2) | Dev runs always said `cabinette-mcp.exe` was missing and to reinstall. | A dev run finds the repository's own build (the newest of Debug and Release). The hint says to build `src/Cabinette.Mcp`. |

**Integrator's notes, also done:**
- The commands broke in the middle of a path (`"C:` at the end of a line). They now break only between arguments, and a too-long path after a backslash. Copying removes the invisible break characters. The Copy button moved beside the title.
- The dev commands match the README's: `cabinette-dev`, Claude Code's local scope, and full `--data-dir` and `--app` paths, which the MCP server requires.

## Leads the reviewer dropped (kept for the record)
- **`--`** passes through PowerShell 5.1 and 7 to native commands.
- **Toast bodies over 255 characters** are cut by Windows without an error.
- **No toast can come from a read:** there is no read kind.
- **The notifier:** setting off, failures always shown, quitting, dispose with a flush pending, two clients, bursts: all as specified.
- **`WS_EX_NOACTIVATE`** applies only in screenshot runs.
- **Relative `--data-dir`** is made absolute at startup.
- **Virtualization:** the list has its own fixed height, so it virtualizes.
- **Theming:** only `Cab.*` resources.

## Left as they are (minor)
- "2 categories renamed" counts renames, not categories (one category renamed twice in 5 s).
- Day headers go stale if Settings stays open past midnight.
- The loaded entries grow while Settings stays open (small, virtualized rows).
- The Copy button is disabled during its 2 s "Copied" feedback.
- The clipboard writer can block for up to ~5 s on a held clipboard.
- The date presets are copied from the main filter (a test keeps them equal).
