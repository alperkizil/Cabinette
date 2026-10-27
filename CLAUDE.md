# Cabinette — notes for agents

Cabinette is a personal Windows file organizer laid out like a chat app. **SPEC.md is the source of truth** for behavior. Read the sections relevant to your task before writing code.

## Layout
| Project | Purpose |
|---|---|
| `src/Cabinette.Core` (net10.0) | Models, `ILibraryService` + implementation, SQLite (`Data/Schema.sql`), categorization (`Categorization/ExtensionMap.cs`), hashing, vault file operations. No UI, no Win32. |
| `src/Cabinette.Windows` (net10.0-windows, WPF) | Win32/Shell interop behind the interfaces in `Contracts.cs`: thumbnails, classic shell context menu, Recycle Bin, clipboard reading, media info, category icon generation. |
| `src/Cabinette.App` (WPF exe, assembly `Cabinette.exe`) | UI. `Services/AppContracts.cs` holds the contracts shared by the shell and the thread view. |
| `src/Cabinette.Ipc` (net10.0-windows) | The local channel shared by Cabinette processes: pipe names (`InstanceNames`), the line format (`InstanceProtocol`, `LineReader`) and the `ai` command's records (`AiProtocol.cs`). References nothing else of Cabinette. |
| `src/Cabinette.Mcp` (exe `cabinette-mcp.exe`) | Stage 4 MCP server for Claude Code and Codex (stdio). Passes each tool call to the running app over the pipe; the app's `Services/Ai/*` executes it. |
| `tests/Cabinette.Core.Tests` | xUnit tests for Core. |
| `tests/Cabinette.App.Tests` | xUnit tests for App logic without UI (instance channel, Run-key command, notification texts, Explorer entries). |
| `tools/Cabinette.IconGen` | Renders `assets/icons/*` (app/tray .ico, previews, `CabinetteLogo.xaml`) from `assets/logo/*.svg`. Run it after changing a logo SVG and commit the output. |
| `installer/` | Inno Setup script (`Cabinette.iss`), its build (`build.ps1`) and a read-only state check (`check-install.ps1`); see `installer/README.md`. `InstallerScriptTests` keep its verb names and class ids in line with `ExplorerIntegration`. |
| `tools/explorer-dev/` | Scripts that add and remove the dev Explorer entries (see below). |

## Contracts
These files are shared and must not change without coordination:
- `Core/Models/*`, `Core/Services/ILibraryService.cs`, `Core/Services/LibraryException.cs`, `Core/Abstractions/*`
- `Windows/Contracts.cs`
- `App/Services/AppContracts.cs`, `App/Localization/L.cs`, `App/Themes/*.xaml`
- `App/ViewModels/Thread/ThreadViewModel.cs` (public surface), `App/ViewModels/Thread/ThreadFeature.cs` (method signature)
- `Ipc/AiProtocol.cs`, `Ipc/InstanceProtocol.cs` (the wire format between `cabinette-mcp.exe` and the app), `App/Services/Ai/AiContracts.cs`

If a contract truly needs to change, make the smallest additive change and list it in your final report.

## Conventions
- C# latest, file-scoped namespaces, nullable enabled, `Async` suffix on async methods. Core library code uses `ConfigureAwait(false)`; UI code does not.
- **UI text:** every user-visible string goes through `L.Get("Key")` / `{loc:Loc Key}`. Shell strings go in `Localization/Strings.resx`, thread strings in `Localization/ThreadStrings.resx`.
- **Colors:** only the `Cab.*` keys from `Themes/Light.xaml` / `Dark.xaml`, always via `{DynamicResource}` so the theme can be switched live. Never hardcode a color in a view.
- **Icons:** WPF-UI `ui:SymbolIcon` (Fluent System Icons). XAML namespace `xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`.
- **MVVM:** CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`). DI uses Microsoft.Extensions.DependencyInjection.
- **Performance:** lists must virtualize (`VirtualizingPanel.IsVirtualizing`, `VirtualizationMode="Recycling"`). Never do file I/O or hashing on the UI thread.
- **Dependencies:** no new NuGet packages without a good reason. Everything must be GPL-3.0 compatible (MIT, Apache-2.0, BSD). List any new package in your report.

## Build and test
```
dotnet build Cabinette.slnx
dotnet test tests/Cabinette.Core.Tests
dotnet test tests/Cabinette.Windows.Tests
dotnet test tests/Cabinette.App.Tests
dotnet test tests/Cabinette.Mcp.Tests
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1   # publish + Inno Setup -> artifacts\installer\Cabinette-Setup.exe
```
Building the installer is safe; **running** `Cabinette-Setup.exe` or its uninstaller installs system-wide behind UAC, so never do it without asking.
Dev runs (`--data-dir`, `--sample-data`, `--screenshot`) never write the "start with Windows" Run key; `--data-dir` runs get their own single-instance names, so they never talk to the user's real running copy.
Some Windows tests touch the user's real desktop, so they are opt-in through environment variables:
- `CABINETTE_RECYCLE_TESTS=1` runs tests that leave files in the Recycle Bin.
- `CABINETTE_CLIPBOARD_TESTS=1` runs tests that overwrite the clipboard.
- `CABINETTE_INTERACTIVE_TESTS=1` runs tests that briefly show a context menu.
- `CABINETTE_COM_TESTS=1` runs tests that register COM class objects (CoRegisterClassObject, visible to the whole Windows session while they run).

Never enable these while the user is working without asking first.

`--downloads-dir <folder>` (only with `--data-dir`) points Clean My Downloads mode at a test folder; the real Downloads folder is refused, but files from the test folder go to the **real** Recycle Bin, so use throwaway files.
Tests that let real WPF animation clocks run go in the `MotionTiming` xUnit collection (no parallelism), or they flake under load.

Dev runs (`--data-dir`) serve the dev Explorer entries ("Copy to Cabinette (dev)", "Add to Cabinette (Pointer, dev)"); `--sample-data` and `--screenshot` runs serve none.
`tools/explorer-dev/Register-DevExplorerEntries.ps1` and `Unregister-DevExplorerEntries.ps1` add and remove those entries in HKCU. They change the user's real Explorer menu, so ask before running them.

## Git
Commit messages end with:
```
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
```
