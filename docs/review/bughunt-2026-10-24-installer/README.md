# Bug hunt, shipping cabinette-mcp.exe with the installer (2026-10-24)

**Status: all 5 findings fixed on 2026-10-24** by a fix agent (branch `fix/installer`, `0e8e5de`, merged into main as `c696113`).
The integrator added the README/Settings cross-check test and ran the full suites.

- **Reviewed:** branch `stage4-installer` (`0981ff2`):
  - `build.ps1`: a full rebuild, then a single-file publish of `cabinette-mcp.exe` next to `Cabinette.exe`, with strict checks
  - `Cabinette.iss`: running server copies are ended outside Restart Manager, because Restart Manager's Ctrl+C would reach the AI client sharing their console
  - `check-install.ps1`
  - the root README's "Use Cabinette from Claude Code or Codex" section and the install-test plan in `installer/README.md`
- **Reviewer:** Claude Fable 5.1 (review branch `review/installer-bughunt`, `e2f4647`).
- **Scoring:** Impact (1–5) × Likelihood (1–3), as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md).
- **Regression tests:** `tests/Cabinette.App.Tests/InstallerReviewTests.cs`, `tests/Cabinette.Mcp.Tests` (the end-script tests), and the harness `Test-CancelFromBeforeInstall.ps1` in this folder. The fix agent reverted each fix in turn and saw its tests fail.
- **Never run:** `Cabinette-Setup.exe` and its uninstaller. The harness builds throwaway per-user installers (no uninstall entry, installed into a temp folder that is deleted afterwards) on a hidden desktop.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 6 (3×2) | Setup ended the MCP servers **before** Restart Manager asked Cabinette to close, not after (`CurStepChanged(ssInstall)` runs before `PerformInstall`). If Cabinette refused (a vault operation running), the AI lost its server for nothing. A cancel there was a fatal error (exit code 3, no rollback). | The servers are ended from `BeforeInstall` on the first `[Files]` entry (cabinette-mcp.exe's own, moved first): after Restart Manager closed Cabinette, before any file is replaced. Inno 7.1 swallows exceptions there (checked in its source), so Cancel uses the wizard's own Cancel path: exit code 5 with rollback. Proven with the harness. |
| 3 | 4 (2×2) | The README's dev registration and Settings › AI's differed (scope and `--app`). | Settings follows the README (fixed in `fix/ai-ui`). The README gives the full command with quoted full paths, and an integration test checks it equals what Settings builds. |
| 4 | 3 (3×1) | The WMI query had no timeout, so a hung WMI hung Setup silently. | `-OperationTimeoutSec 30`; a timeout is logged and Setup continues. |
| 2 | 2 (2×1) | A server started through an 8.3 short path or a junction wasn't recognized as the installed copy, so Setup would fail with "file in use". | Matched by file identity (volume serial + file index), falling back to full paths. WMI is also asked for 8.3 process names. |
| 5 | 1 (1×1) | `check-install.ps1` looked in `Program Files (x86)` from a 32-bit host. | Uses the uninstall entry's InstallLocation (read from the 64-bit registry view), then `ProgramW6432`. |

**Also from the Codex test:** the README says Codex asks before tools marked destructive (rejected under `codex exec` with approval "never"; see `default_tools_approval_mode`). It also says Windows paths in a hand-edited `config.toml` need single quotes or doubled backslashes.

## Verified by the reviewer (no finding)
- `CloseApplicationsFilterExcludes` works in ISCC 7.1, and Restart Manager still closes Cabinette.exe.
- No console flash.
- The uninstaller's order: Cabinette first, then the servers.
- The publish is a single Release file at the app's version, with no test helper.
- Every command in the docs matches `claude` 2.1.306 and `codex` 0.159.3.

## Leads dropped
- A new NuGet audit advisory fails the build by design.
- The install-test plan expects the server's parent to be "claude"; on Windows it may be a shell wrapper.
- Ending another account's copies may fail without a debug privilege; that gives Retry/Cancel.

## What the install test should check
- **Setup log, in order:**
  1. "Shutting down applications using our files."
  2. "Ending cabinette-mcp.exe (pid …)…" and its exit code 0.
  3. Then the `cabinette-mcp.exe` file entry.
- **Cancel with Cabinette busy:** the log ends with "User canceled the installation process.", has no "Ending cabinette-mcp.exe", and Claude Code stays connected.
- **Uninstall log:** Restart Manager closes Cabinette ("RmShutdown: 0") before "Ending cabinette-mcp.exe".
