# Cabinette installer

`Cabinette.iss` is an [Inno Setup](https://jrsoftware.org/isinfo.php) 7 script. It builds `Cabinette-Setup.exe`, a
64-bit setup that installs Cabinette for all users. Behavior is specified in SPEC.md section 9.

## Building

```
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1
```

(`pwsh -File installer\build.ps1` works too.) You need the .NET 10 SDK and, the first time, access to nuget.org.
Nothing gets installed on the PC. The script:

1. reads `Version` from `Directory.Build.props`,
2. publishes `src\Cabinette.App` (Release, framework-dependent, win-x64) into `artifacts\publish\Cabinette`,
3. publishes `src\Cabinette.Mcp` the same way but as a single file (`PublishSingleFile`), into its own temporary
   folder `artifacts\publish\cabinette-mcp.tmp`, and copies `cabinette-mcp.exe` and `cabinette-mcp.pdb` next to
   `Cabinette.exe`. As one file, its dependencies (the MCP SDK and its `Microsoft.Extensions.*` versions) stay inside
   the exe and can never overwrite or clash with the app's DLLs in the same folder. The build stops if the project
   compiles or publishes with a warning (it is rebuilt from scratch first, so no warning hides behind an up-to-date
   build), if the publish produced anything but the exe and `.pdb` files, or if the two programs' versions differ.
   The test helper (`tests\Cabinette.Mcp.TestHelper`) is never built or shipped,
4. restores `installer\InnoSetup.csproj`, which pins the `Tools.InnoSetup` NuGet package (Inno Setup 7.1.0), and
   takes `ISCC.exe` from the NuGet cache,
5. compiles `Cabinette.iss` into **`artifacts\installer\Cabinette-Setup.exe`** and prints its SHA-256.
   Any ISCC warning fails the build.

To update Inno Setup, change the version in `InnoSetup.csproj`. `Tools.InnoSetup` is an unofficial package by
Vaclav Slavik (package files in the public domain). Inno Setup itself is under the Inno Setup License (permissive,
see `license.txt` in the package). It's a build tool only; the setup program it produces carries that license too.

The setup isn't code-signed, so SmartScreen shows "unknown publisher" the first time it runs.

## What Setup does

- Requires administrator rights, Windows 10 1809 (build 17763) or later, x64 (Arm64 Windows 11 runs it under emulation).
- Installs to `C:\Program Files\Cabinette` (64-bit), with a Start Menu shortcut, an uninstaller and `LICENSE.txt`.
  No desktop shortcut, no license page. The `licenses` folder holds the licenses of the fonts built into Cabinette
  (SIL OFL 1.1, which asks for them to travel with the fonts); it comes from the publish folder like the program files.
- **`cabinette-mcp.exe`**, the MCP server for Claude Code and Codex (SPEC.md section 10), goes next to `Cabinette.exe`
  as one file. Setup registers nothing for it and never reads or writes Claude Code's or Codex's settings; you
  register it yourself (root `README.md`, or Settings › AI in Cabinette).
- **.NET 10 Desktop Runtime (x64).** Setup looks where `Cabinette.exe` will look: the folder registered under
  `HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\InstallLocation` (32-bit registry view), else
  `C:\Program Files\dotnet`. It needs a release `10.x` folder with its `.deps.json` under both
  `shared\Microsoft.NETCore.App` and `shared\Microsoft.WindowsDesktop.App`. If one is missing, clicking Install:
  1. downloads `https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` (Microsoft's link to the latest 10.x
     patch, served from `builds.dotnet.microsoft.com`),
  2. checks its Authenticode signature with Windows PowerShell: it must be valid and trusted, signed by an
     organization `O=Microsoft Corporation`, with a chain that ends in a Microsoft root. Otherwise Setup doesn't run
     it, stays on the Ready page and offers https://dotnet.microsoft.com/download/dotnet/10.0,
  3. runs it with `/install /quiet /norestart`. Exit code 0 continues; 3010 continues and asks for a restart at the
     end; 1641 (restart started), 1618 (another install running) and anything else stop with a message.
- **Closes a running Cabinette** before replacing files (Restart Manager, `CloseApplications=yes`): the Preparing to
  Install page lists it, and after Install Setup shows "Closing applications...". Cabinette quits when asked
  (`App.xaml.cs`, SessionEnding), except while a vault move, migrate, backup or restore runs: then it refuses, and
  Setup shows "Setup was unable to automatically close all applications" with Try again, Ignore and Cancel
  installation (the uninstaller a Retry/Cancel box). It isn't restarted automatically; the last page offers "Launch
  Cabinette", which starts it through Explorer, so it runs at the desktop's level (not elevated) even when Setup was
  started with "Run as administrator".
- **Ends a running `cabinette-mcp.exe`** after Cabinette has closed, before it replaces any file (see below).
- **Never writes "start with Windows".** Cabinette does that itself on its first normal start.
- **Upgrades** in place: a newer setup with the same AppId installs over the old version.
- Always writes a log to `%TEMP%\Setup Log <date> #<n>.txt` (the uninstaller: `Uninstall Log ...`), including the
  runtime check, the signature check output, the Restart Manager and Run-key decisions and which `cabinette-mcp.exe`
  processes it ended.

### A running cabinette-mcp.exe (upgrade and uninstall)

Claude Code and Codex start `cabinette-mcp.exe` as their child, one per session, so it can be running while you
upgrade or uninstall. Windows doesn't let a running program's file be replaced or deleted, and an old copy left
running would only keep the file locked. So Setup (after closing Cabinette, before it replaces any file) and the
uninstaller (after closing Cabinette, before it removes files) end it:

- **Only the copies started from this installation's file** (`{app}\cabinette-mcp.exe`) are ended. A process counts
  when its program file is that very file (the same volume serial number and file index), however its path was
  spelled: also when it was started through an 8.3 short name (`C:\PROGRA~1\CABINE~1\CABINE~1.EXE`) or a junction or
  symbolic link. Only when a file can't be opened are the full paths compared instead. A `cabinette-mcp.exe` anywhere
  else, such as a developer build, is another file and is left alone.
- **They are ended outright** (`Stop-Process`, which calls `TerminateProcess`), by a short Windows PowerShell script
  in `[Code]` (`EndMcpScript`). That loses nothing: `cabinette-mcp.exe` keeps no state, Cabinette does the work and
  logs every call in its activity log.
- **Not through Restart Manager** (`CloseApplicationsFilterExcludes`). Restart Manager closes a console program by
  sending Ctrl+C to its console, and Windows delivers that to every process attached to the console. Tested on this
  PC: with a stand-in client that shared its console with `cabinette-mcp.exe`, `RmShutdown` stopped the client too.
  Whether Claude Code or Codex share their console with the server isn't under Cabinette's control, and a Ctrl+C
  could interrupt or end your session (and the shell it runs in).
- **After Cabinette, before any file.** Setup does it from a `BeforeInstall` procedure on its first `[Files]` entry,
  `cabinette-mcp.exe` itself. That runs inside the installation proper: after "Closing applications..." (Restart
  Manager closing Cabinette) and before the first file is replaced (Inno Setup's installation order; there is no
  `[InstallDelete]`). So an AI client keeps its server when Setup stops before that point: when Cabinette refuses to
  close (a vault operation runs) and you choose Cancel installation, or when you cancel during "Closing
  applications...". `CurStepChanged(ssInstall)`, where it used to be, runs before Cabinette is asked to close.
- If one can't be ended (it runs in another Windows account that Setup may not end), Setup asks you to close Claude
  Code and Codex and offers Retry or Cancel, like the uninstaller does for Cabinette. Cancel cancels the installation
  the way Setup's own Cancel button does: nothing has been replaced yet, Setup rolls back what it did ("Rolling back
  changes...") and exits with code 5. (An exception or `Abort` can't do that from `BeforeInstall`: Inno Setup catches
  it there and goes on installing.) Only in a very silent install, which can't be cancelled at that point, does Setup
  go on; replacing the file that is still in use then stops it with its usual error box, whose Cancel installation
  (the answer with `/SUPPRESSMSGBOXES`) rolls back too. `docs\review\bughunt-2026-10-24-installer\Test-CancelFromBeforeInstall.ps1`
  checks all three with Inno Setup itself, using throwaway per-user installers on a hidden desktop.

Afterwards the AI client shows the `cabinette` server as disconnected or failed. To reconnect:

- **Claude Code:** `/mcp reconnect cabinette` (or open `/mcp`, choose `cabinette`, Reconnect), or restart Claude Code.
- **Codex:** start a new session; Codex starts its MCP servers with each session.

If an AI client starts `cabinette-mcp.exe` again in the moment between that step and the copy of that very file,
Setup shows its usual "An error occurred while trying to replace the existing file" box with Try again: close the
client and try again.

### Registry (all under `HKLM\Software\Classes`, removed on uninstall)

| Key | Values |
|---|---|
| `AllFilesystemObjects\shell\Cabinette.Copy` | `MUIVerb` = `Copy to Cabinette`, `Icon` = `"<app>\Cabinette.exe",0`, `MultiSelectModel` = `Player` |
| `AllFilesystemObjects\shell\Cabinette.Copy\DropTarget` | `Clsid` = `{E92F98C8-F1F4-4B7F-BBBE-38D6678CE55E}` |
| `AllFilesystemObjects\shell\Cabinette.Pointer` | `MUIVerb` = `Add to Cabinette (Pointer)`, `Icon`, `MultiSelectModel` as above |
| `AllFilesystemObjects\shell\Cabinette.Pointer\DropTarget` | `Clsid` = `{ED9B5A7A-4497-43EF-9727-8C6AFCD8CAE2}` |
| `CLSID\{E92F98C8-...}` and `CLSID\{ED9B5A7A-...}` | a description; `LocalServer32` = `"<app>\Cabinette.exe"`. Written in both registry views (also `WOW6432Node\CLSID`), so 32-bit programs' file dialogs can use the entries too |

The verb names and class ids come from `ExplorerIntegration` in `src/Cabinette.Windows/Contracts.cs`;
`InstallerScriptTests` (tests/Cabinette.App.Tests) fails if the script drifts from them. Inno Setup also writes its
usual uninstall entry (`HKLM\...\Uninstall\{17D5A5C2-4B3D-4D0C-9945-12604F711F45}_is1`).

To see what is registered on this PC (read-only, no admin rights needed):

```
powershell -NoProfile -ExecutionPolicy Bypass -File installer\check-install.ps1
```

## What uninstall does

- Closes a running Cabinette (Restart Manager, from `[Code]`; the uninstaller has no built-in option). If that fails,
  it asks to quit Cabinette from the tray and retry, or cancel.
- Ends the `cabinette-mcp.exe` processes started from the installation (see above), with Retry or Cancel if one
  can't be ended.
- Removes the files (`cabinette-mcp.exe` too), the Start Menu shortcut and the registry keys above.
- Removes the uninstalling account's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Cabinette`, but only
  when it starts this installation's `Cabinette.exe`.
- Leaves `%LocalAppData%\Cabinette` (library, settings, thumbnails, logs) and the vault alone.
- Leaves Claude Code's and Codex's registrations alone: remove them yourself with
  `claude mcp remove --scope user cabinette` and `codex mcp remove cabinette`.

## Install test (once, at the end of stage 4)

Running `Cabinette-Setup.exe` installs system-wide behind UAC (and SmartScreen asks first), so this is done by hand,
once. Quit any other Cabinette first (the prototype in `artifacts\Cabinette-prototype` is outside `{app}`, so Setup
can't close it). Use throwaway files only.

1. **Before:** `powershell -NoProfile -ExecutionPolicy Bypass -File installer\check-install.ps1`. Everything is
   missing, and no `cabinette-mcp` process runs.
2. **Install** `artifacts\installer\Cabinette-Setup.exe`, keep "Launch Cabinette" ticked and finish first run with a
   throwaway vault. Run `check-install.ps1` again: the entries and classes point at `C:\Program Files\Cabinette`,
   "start with Windows" is set, `cabinette-mcp.exe` shows the same version as `Cabinette.exe` ("matches") and its
   `--help` exits with code 0.
3. **Explorer entries:** Shift+right-click a few throwaway files, choose "Copy to Cabinette" and "Add to Cabinette
   (Pointer)", and a folder; also from a 32-bit program's file dialog. Check the notification and the categories.
4. **Register with Claude Code** using the installed path, then check it from a terminal:
   ```
   claude mcp add --scope user cabinette -- "C:\Program Files\Cabinette\cabinette-mcp.exe"
   claude mcp get cabinette
   ```
   (With Codex: `codex mcp add cabinette -- "C:\Program Files\Cabinette\cabinette-mcp.exe"`, then `codex mcp list`.)
5. **A few real calls** from a new Claude Code session in a scratch folder, with Cabinette's window hidden in the tray:
   1. "List my Cabinette categories" (`list_categories`).
   2. "Search Cabinette for <a name from step 3>" (`search`).
   3. Create `%TEMP%\cabinette-mcp-test.txt` with a line of text and ask "Add it to Cabinette as a copy"
      (`add_files`). A toast "Claude Code added 1 file to …" appears.
   4. "Read it back from Cabinette" (`read_file`): the same line.
   5. "Delete it from Cabinette" (`delete_files`): it is in the Recycle Bin, even if your delete setting says
      permanent. Delete it there yourself.
6. **Settings › AI** in Cabinette: the two commands show `C:\Program Files\Cabinette\cabinette-mcp.exe`, and the
   activity log lists the calls of step 5 (reads too) with "Claude Code" as the client.
7. **Upgrade with the AI connected:** set `Version` to 0.1.1 in `Directory.Build.props` (don't commit it), build the
   installer and run it while the Claude Code session of step 5 is still open.
   - The Setup log (`%TEMP%\Setup Log …`) has, in this order: "Shutting down applications using our files." (Restart
     Manager closing Cabinette), then "Ending cabinette-mcp.exe (pid …), started by claude (pid …)" and "Ending
     C:\Program Files\Cabinette\cabinette-mcp.exe: exit code 0", then "-- File entry --" with "Dest filename:
     C:\Program Files\Cabinette\cabinette-mcp.exe", the first program file. (Only the uninstaller's own entry comes
     before it; on an upgrade it says "Leaving temporary file in place for now.", so nothing is replaced there.)
   - The Claude Code session itself keeps running (it isn't interrupted). `/mcp` shows `cabinette` as failed or
     disconnected; `/mcp reconnect cabinette` brings it back, and `list_categories` works again.
   - `check-install.ps1` shows 0.1.1 for both programs ("matches").
   - **Cabinette busy** (if you can keep a vault operation running long enough, for example a backup of a vault with a
     few GB of throwaway files): start it, then run the same setup again. Setup shows "Setup was unable to
     automatically close all applications"; choose Cancel installation. The log ends with "User canceled the
     installation process." and has no "Ending cabinette-mcp.exe", and `/mcp` in Claude Code still shows `cabinette`
     connected.
8. **Uninstall** from Settings → Apps while Claude Code is connected again. The Uninstall log shows Restart Manager
   closing Cabinette ("… process(es) use C:\Program Files\Cabinette\Cabinette.exe; asking them to close." and
   "RmShutdown: 0") before "Ending cabinette-mcp.exe (pid …)". `check-install.ps1`: everything missing, including
   `cabinette-mcp.exe`, `C:\Program Files\Cabinette` is gone, the library database and the vault are still there.
9. **Remove the registrations:** `claude mcp remove --scope user cabinette` (and `codex mcp remove cabinette`).
   `claude mcp get cabinette` then says it isn't found.
