# Install test, end of stage 4 (2026-10-24)

The plan in [installer/README.md](../../../installer/README.md), run with the user on their PC. The user clicked through Setup, UAC, first run and the uninstaller; the integrator did the rest.

| Step | Result |
|---|---|
| 1. Before | `check-install.ps1`: everything missing. |
| 2. Install 0.1.0 (main `6af9e7e`) | Explorer verbs and COM classes (64- and 32-bit) point at `C:\Program Files\Cabinette`. The uninstall entry says 0.1.0. `cabinette-mcp.exe` "matches" and `--help` exits with 0. "Start with Windows" was set (`"C:\Program Files\Cabinette\Cabinette.exe" --tray`, checked in the user's own terminal). First run used a fresh vault, `Documents\Cabinette Vault`. |
| 3. Explorer entries | Invoked like Explorer does (`InvokeVerbEx`): "Copy to Cabinette" on a folder (it became the category "Install test folder"), then on a two-file selection (one batch: "2 item(s) to add"); "Add to Cabinette (Pointer)" on a `.md`. The 32-bit file-dialog variant wasn't run. |
| 4. Register | `claude mcp add --scope user cabinette -- "C:\Program Files\Cabinette\cabinette-mcp.exe"`; `claude mcp get`: Connected. |
| 5. Real calls | A fresh headless Claude Code session (Sonnet) in a scratch folder called list_categories, search, add_files (sorted into Text), read_file (the same line back) and delete_files: no errors. The copy went to the Recycle Bin. The user saw the toasts and taskbar notifications. |
| 6. Activity log | 5 entries, client "Claude Code", none failed. |
| 7. Upgrade to 0.1.1 (version bump not committed) | Run with a server connected (a stand-in `cabinette-mcp.exe` in a terminal). The Setup log, in order: "Shutting down applications using our files." (14:07:39.702), the uninstaller entry ("Leaving temporary file in place for now."), "Ending cabinette-mcp.exe (pid 20420), started by pwsh" with exit code 0, then the first program file, `cabinette-mcp.exe`. "Installation process succeeded"; both programs show 0.1.1; Cabinette relaunched. `claude mcp get cabinette` was Connected again. The "Cabinette busy → Cancel" case wasn't run here (the review harness covers it with throwaway installers). |
| 8. Uninstall (Settings › Apps) | Server connected again. The Uninstall log: "1 process(es) use …Cabinette.exe; asking them to close.", "RmShutdown: 0", then "Ending cabinette-mcp.exe (pid 30600)" with exit code 0, "Removed the 'start with Windows' entry", "Uninstallation process succeeded". `check-install.ps1`: everything missing, `C:\Program Files\Cabinette` gone. The library database and the vault were kept. The Run value was gone too (checked in the user's terminal). |
| 9. Unregister | `claude mcp remove --scope user cabinette`; `claude mcp get cabinette`: not found. |

## Found along the way
- **The agent tools' registry view is filtered:** in this environment, both the Bash and PowerShell tools see a private copy of HKCU. Their reads miss real values, and their writes and deletes don't reach the real registry. HKLM and the file system are real.
  - The install-state check run from those tools reported "start with Windows" as missing while it was set.
  - An earlier cleanup of a stray dev Run value (2026-10-24 00:56) never reached the real registry. The value was overwritten by the prototype's own registration at 01:01, and by the installed Cabinette at 14:02.
  - Check HKCU in the user's own terminal (the Terminal panel), not with the agent tools.
- **SmartScreen** didn't appear: the setup was built on this PC, so it has no Mark of the Web.
