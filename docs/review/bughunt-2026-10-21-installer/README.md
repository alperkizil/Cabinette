# Bug hunt, stage 3 installer (2026-10-21)

**Status: all 3 findings fixed on 2026-10-21**, after the merge of the installer branch (`7b4be0d`).

- **Reviewed:** branch `worktree-agent-a24f99d4de677814a` (`f555817`, `14e2e55`): `installer/Cabinette.iss`, `build.ps1`,
  `check-install.ps1` and the drift test `InstallerScriptTests`.
- **Reviewer:** Claude Fable 5.1, static review plus tests. It didn't run Setup, the uninstaller or Cabinette, wrote no
  registry, and downloaded no runtime.
- **Verification:** the integrator (Claude Opus 5.5) re-checked each finding against the code and the Inno Setup
  documentation before fixing it.
- **Scoring:** as in [the stage 1+2 bug hunt](../bughunt-2026-10-15/README.md): Impact (1–5) × Likelihood (1–3), for one
  normal user installing on their own PC.
- **Regression tests:** `tests/Cabinette.App.Tests/BugHuntInstallerTests.cs`. The signature-check harness
  `Test-SignatureScript.ps1` (in this folder) runs Setup's exact PowerShell check against signed, unsigned, tampered
  and non-Microsoft files; every case passed.

## Findings, by score

| # | Score | Finding | Fix |
|---|---|---|---|
| 1 | 4 (4×1) | Setup and the uninstaller close Cabinette through Restart Manager, which sends the sign-out message. Cabinette quit even in the middle of a vault move, backup or restore. A restore cut off after renaming the database leaves a half-restored library (recoverable by hand). | `AppLifetime.TryEndSession` refuses while an operation runs, like Quit in the tray; `App.xaml.cs` cancels SessionEnding. Setup then shows its Retry page. This also protects a Windows sign-out. |
| 2 | 2 (2×1) | The COM classes were registered only in the 64-bit registry view. WOW64 redirects `HKLM\Software\Classes\CLSID` but shares the verb keys, so a 32-bit program's file dialog showed the entries, and clicking them did nothing. | Both classes are also written with `Root: HKLM32`; the drift test and `check-install.ps1` check both views. |
| 3 | 2 (2×1) | "Launch Cabinette" ran it elevated when Setup itself was started with "Run as administrator" (Inno: `runasoriginaluser` has no effect then). Explorer drag and drop onto an elevated window is blocked. | The finish-page entry starts Cabinette through `explorer.exe`, which runs it at the desktop's level. |

Also corrected: a code comment claimed `{tmp}` is writable only by administrators. It is in the user's own temp
folder; that adds no risk, since the same user could replace `Cabinette-Setup.exe` itself.

## Leads the reviewer dropped (kept for the record)
- `DOTNET_ROOT` is honored by the app but ignored by Setup's runtime check (developer machines only).
- The uninstaller's Restart Manager call (`RmRegisterResources` with an array of strings) couldn't be verified
  without running it. A failure is logged, and files in use then fail to delete. Check the Uninstall Log in the
  manual test.
- With the prototype running, Setup can't close it (it's outside `{app}`), and "Launch Cabinette" activates the
  prototype. Quit the prototype before installing.
- Uninstalling from another account leaves that account's own Run value (as SPEC says).
- `Tools.InnoSetup` is unofficial and restored without a lock file. ISCC.exe is Authenticode-signed. Hardening only.
