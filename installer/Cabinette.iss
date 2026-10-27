; Cabinette installer (Inno Setup 7). Build it with installer\build.ps1, which publishes the app and passes the defines
; below; see installer\README.md. Behavior: SPEC.md section 9.

#ifndef AppVersion
  #error AppVersion is not defined. Build with installer\build.ps1 (it reads Version from Directory.Build.props).
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric AppVersion
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\Cabinette"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; Explorer entries and their COM drop targets. These must match Cabinette.Windows.ExplorerIntegration; the test
; InstallerScriptTests (tests/Cabinette.App.Tests) fails if they drift apart.
#define CopyVerb "Cabinette.Copy"
#define CopyVerbText "Copy to Cabinette"
#define CopyClsid "E92F98C8-F1F4-4B7F-BBBE-38D6678CE55E"
#define PointerVerb "Cabinette.Pointer"
#define PointerVerbText "Add to Cabinette (Pointer)"
#define PointerClsid "ED9B5A7A-4497-43EF-9727-8C6AFCD8CAE2"

; The app's "start with Windows" entry (Cabinette.App.Services.StartupCommand), removed again on uninstall.
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "Cabinette"

; The MCP server for Claude Code and Codex (src/Cabinette.Mcp, SPEC section 10), one file next to Cabinette.exe.
; Setup registers nothing for it and never touches Claude Code's or Codex's settings: you register it yourself
; (README.md, or Settings > AI in Cabinette).
#define McpExe "cabinette-mcp.exe"

[Setup]
; Never change AppId: upgrades find the existing installation through it.
AppId={{17D5A5C2-4B3D-4D0C-9945-12604F711F45}
AppName=Cabinette
AppVersion={#AppVersion}
AppPublisher=alperkizil
; The link Settings › Apps shows for the publisher. Switch it to the repository once it is on GitHub.
AppPublisherURL=https://github.com/alperkizil
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductName=Cabinette
VersionInfoDescription=Cabinette Setup
DefaultDirName={autopf}\Cabinette
DisableProgramGroupPage=yes
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Cabinette-Setup
SetupIconFile=..\assets\icons\Cabinette.ico
UninstallDisplayName=Cabinette
UninstallDisplayIcon={app}\Cabinette.exe
WizardStyle=modern dynamic
Compression=lzma2/max
SolidCompression=yes
; Restart Manager closes a running Cabinette before files are replaced. Cabinette doesn't register for restarts, and a
; restart from here would run elevated, so the finished page offers "Launch Cabinette" instead.
CloseApplications=yes
RestartApplications=no
; Except cabinette-mcp.exe: Restart Manager closes a console program with Ctrl+C, which reaches every process on its
; console, so it could also stop the AI client that started it. [Code] ends it instead (EndMcpServers).
CloseApplicationsFilterExcludes={#McpExe}
; Tells Explorer to refresh after the Explorer entries were added or removed.
ChangesAssociations=yes
; Always write "Setup Log <date> #<n>.txt" / "Uninstall Log ..." to %TEMP% (runtime download, signature check,
; Restart Manager and Run-key decisions are logged there).
SetupLogging=yes
UninstallLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.ReadyRuntimeHeading=Also downloads and installs from Microsoft:
english.ReadyRuntimeItem=.NET 10 Desktop Runtime (x64), about 60 MB
english.RuntimeDownloadCaption=Downloading the .NET Desktop Runtime
english.RuntimeDownloadDescription=Cabinette needs the .NET 10 Desktop Runtime from Microsoft.
english.RuntimeInstallCaption=Installing the .NET Desktop Runtime
english.RuntimeInstallDescription=This can take a few minutes.
english.RuntimeVerifying=Checking Microsoft's signature on the download...
english.RuntimeInstalling=Installing the .NET 10 Desktop Runtime (x64)...
english.RuntimeDownloadFailed=The .NET 10 Desktop Runtime could not be downloaded: %1
english.RuntimeSignatureFailed=The downloaded .NET Desktop Runtime installer does not carry a valid Microsoft signature, so Setup did not run it.
english.RuntimeStartFailed=The .NET Desktop Runtime installer could not be started: %1
english.RuntimeInstallFailed=The .NET Desktop Runtime installer failed (exit code %1).
english.RuntimeOtherInstall=Another installation is in progress. Wait for it to finish, then click Install again.
english.RuntimeRebootInitiated=The .NET Desktop Runtime installer is restarting Windows. Run Cabinette Setup again afterwards.
english.RuntimeStillMissing=The .NET Desktop Runtime installer finished, but the runtime still isn't there.
english.RuntimeManual=You can install the .NET 10 Desktop Runtime (x64) yourself from Microsoft:%n%1%n%nOpen this page now?
english.CloseFailed=Cabinette is still running and could not be closed.%n%nQuit Cabinette (right-click its tray icon and choose Quit), then click Retry.
english.McpEndFailed=Cabinette's connection to Claude Code or Codex (cabinette-mcp.exe) is still running and could not be ended.%n%nClose Claude Code and Codex, also in other signed-in Windows accounts, then click Retry.

[Files]
; cabinette-mcp.exe comes first: its BeforeInstall ends the running copies (EndMcpServersBeforeFiles), and it must run
; before any file is replaced. That is inside the installation proper, after Restart Manager has closed Cabinette, so
; an AI client keeps its server when Setup stops before (Cabinette refuses to close, the user cancels), and a cancel
; there rolls back. Keep this entry first. Named on its own, so ISCC fails when build.ps1 didn't put it into the
; publish folder. Removed on uninstall like every installed file.
Source: "{#PublishDir}\{#McpExe}"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: EndMcpServersBeforeFiles
Source: "{#PublishDir}\*"; Excludes: "\{#McpExe}"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Cabinette"; Filename: "{app}\Cabinette.exe"; WorkingDir: "{app}"

[Registry]
; Explorer right-click entries (classic menu; on Windows 11 under "Show more options") on files, folders and mixed
; selections. MultiSelectModel=Player plus a DropTarget hands the whole selection to Cabinette's COM class in one call,
; with no item limit. COM starts "Cabinette.exe -Embedding" when Cabinette isn't running.
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#CopyVerb}"; Flags: deletekey uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#CopyVerb}"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{#CopyVerbText}"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#CopyVerb}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Cabinette.exe"",0"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#CopyVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#CopyVerb}\DropTarget"; ValueType: string; ValueName: "Clsid"; ValueData: "{{{#CopyClsid}}"

Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#PointerVerb}"; Flags: deletekey uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#PointerVerb}"; ValueType: string; ValueName: "MUIVerb"; ValueData: "{#PointerVerbText}"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#PointerVerb}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Cabinette.exe"",0"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#PointerVerb}"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"
Root: HKLM; Subkey: "Software\Classes\AllFilesystemObjects\shell\{#PointerVerb}\DropTarget"; ValueType: string; ValueName: "Clsid"; ValueData: "{{{#PointerClsid}}"

; The COM classes behind the entries, served by Cabinette.exe.
Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}"; Flags: deletekey uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}"; ValueType: string; ValueName: ""; ValueData: "Cabinette drop target: {#CopyVerbText}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}\LocalServer32"; ValueType: string; ValueName: ""; ValueData: """{app}\Cabinette.exe"""

Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}"; Flags: deletekey uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}"; ValueType: string; ValueName: ""; ValueData: "Cabinette drop target: {#PointerVerbText}"
Root: HKLM; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}\LocalServer32"; ValueType: string; ValueName: ""; ValueData: """{app}\Cabinette.exe"""

; The same classes in the 32-bit view. WOW64 redirects HKLM\Software\Classes\CLSID (the verb keys above are shared),
; so without these a 32-bit program's file dialog would show the entries but couldn't start Cabinette.
Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}"; Flags: deletekey uninsdeletekey
Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}"; ValueType: string; ValueName: ""; ValueData: "Cabinette drop target: {#CopyVerbText}"
Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#CopyClsid}}\LocalServer32"; ValueType: string; ValueName: ""; ValueData: """{app}\Cabinette.exe"""

Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}"; Flags: deletekey uninsdeletekey
Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}"; ValueType: string; ValueName: ""; ValueData: "Cabinette drop target: {#PointerVerbText}"
Root: HKLM32; Subkey: "Software\Classes\CLSID\{{{#PointerClsid}}\LocalServer32"; ValueType: string; ValueName: ""; ValueData: """{app}\Cabinette.exe"""

[Run]
; Started through Explorer, so Cabinette runs at the desktop's level (not elevated) even when Setup itself was started
; with "Run as administrator", where runasoriginaluser has no effect. "Start with Windows" is written by Cabinette
; itself on this first normal start, never by Setup (which runs as administrator and could pick the wrong account).
Filename: "{win}\explorer.exe"; Parameters: """{app}\Cabinette.exe"""; Description: "{cm:LaunchProgram,Cabinette}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  // Microsoft's evergreen link to the latest .NET 10 Windows Desktop Runtime installer for x64.
  // It redirects to https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/<version>/windowsdesktop-runtime-<version>-win-x64.exe
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeFileName = 'windowsdesktop-runtime-win-x64.exe';
  RuntimeDownloadPageUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  RuntimeMajor = '10';

  ERROR_INSTALL_ALREADY_RUNNING = 1618;
  ERROR_SUCCESS_REBOOT_INITIATED = 1641;
  ERROR_SUCCESS_REBOOT_REQUIRED = 3010;

var
  DownloadPage: TDownloadWizardPage;
  RuntimePage: TOutputMarqueeProgressWizardPage;
  RuntimeMissing: Boolean;
  RestartNeeded: Boolean;

function SetEnvironmentVariable(lpName, lpValue: String): BOOL;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

// <dotnet-detection>
// The .NET root that Cabinette.exe (a framework-dependent apphost) uses: the registered x64 install location (kept
// in the 32-bit registry view), else the default folder.
function DotNetRoot: String;
begin
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Result)
    or (Trim(Result) = '') then
  begin
    if IsArm64 then
      Result := ExpandConstant('{commonpf64}\dotnet\x64')
    else
      Result := ExpandConstant('{commonpf64}\dotnet');
  end;
  Result := RemoveBackslashUnlessRoot(Trim(Result));
end;

// True when <Root>\shared\<Framework> has a release (not preview) 10.x version folder with its .deps.json. Cabinette
// targets 10.0.0, and .NET rolls forward to any newer 10.x.
function HasFramework(const Root, Framework: String): Boolean;
var
  FindRec: TFindRec;
  Dir: String;
begin
  Result := False;
  Dir := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Dir + RuntimeMajor + '.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0)
          and FileExists(Dir + FindRec.Name + '\' + Framework + '.deps.json') then
        begin
          Log(Format('Found %s %s in %s', [Framework, FindRec.Name, Root]));
          Result := True;
        end;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsDesktopRuntimeInstalledIn(const Root: String): Boolean;
begin
  Result := HasFramework(Root, 'Microsoft.NETCore.App') and HasFramework(Root, 'Microsoft.WindowsDesktop.App');
end;

function IsDesktopRuntimeInstalled: Boolean;
var
  Root: String;
begin
  Root := DotNetRoot;
  Result := IsDesktopRuntimeInstalledIn(Root);
  if not Result then
    Log(Format('.NET %s Desktop Runtime (x64) not found in %s', [RuntimeMajor, Root]));
end;
// </dotnet-detection>

// <signature-check>
// Windows PowerShell (part of Windows) checks the Authenticode signature. Accepted only when the signature is valid
// and trusted, the signer's organization is Microsoft Corporation, and the chain ends in a Microsoft root. The file
// path comes in through an environment variable, so it needs no quoting. The script uses no double quotes. The
// module is loaded from Windows PowerShell's own folder: a PSModulePath inherited from PowerShell 7 would otherwise
// make it load PowerShell 7's copy, which fails.
function SignatureScript: String;
begin
  Result :=
    '$ErrorActionPreference = ''Stop''; ' +
    'try { ' +
      'Import-Module (Join-Path $PSHOME ''Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1''); ' +
      '$sig = Get-AuthenticodeSignature -LiteralPath $env:CABINETTE_VERIFY_FILE; ' +
      'Write-Output (''Signature: '' + $sig.Status); ' +
      'if ($sig.Status -ne ''Valid'') { exit 2 }; ' +
      '$flags = [Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines; ' +
      '$isMicrosoft = { param($c) @($c.SubjectName.Decode($flags) -split ''\r?\n'') -contains ''O=Microsoft Corporation'' }; ' +
      '$cert = $sig.SignerCertificate; ' +
      'Write-Output (''Signer: '' + $cert.Subject); ' +
      'if (-not (& $isMicrosoft $cert)) { exit 3 }; ' +
      '$chain = New-Object Security.Cryptography.X509Certificates.X509Chain; ' +
      '$chain.ChainPolicy.RevocationMode = ''NoCheck''; ' +
      // The intermediates embedded in the signature, so the chain also builds offline.
      'try { $embedded = New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection; ' +
        '$embedded.Import($env:CABINETTE_VERIFY_FILE); $chain.ChainPolicy.ExtraStore.AddRange($embedded) } catch { }; ' +
      '[void]$chain.Build($cert); ' +
      '$root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate; ' +
      'Write-Output (''Root: '' + $root.Subject + '' '' + $root.Thumbprint); ' +
      'if (($root.Subject -ne $root.Issuer) -or -not (& $isMicrosoft $root)) { exit 4 }; ' +
      'exit 0 ' +
    '} catch { Write-Output (''Check failed: '' + $_.Exception.Message); exit 9 }';
end;

function HasMicrosoftSignature(const FileName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not SetEnvironmentVariable('CABINETTE_VERIFY_FILE', FileName) then
  begin
    Log('Could not pass the file name to the signature check.');
    Exit;
  end;
  try
    if ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -Command "' + SignatureScript + '"',
      '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode, nil) then
    begin
      Log(Format('Signature check of %s: exit code %d', [FileName, ResultCode]));
      Result := ResultCode = 0;
    end
    else
      Log('The signature check could not run: ' + SysErrorMessage(ResultCode));
  except
    Log('The signature check failed: ' + GetExceptionMessage);
  end;
  SetEnvironmentVariable('CABINETTE_VERIFY_FILE', '');
end;
// </signature-check>

// <restart-manager>
// Restart Manager (rstrtmgr.dll), used by the uninstaller to close a running Cabinette the same way Setup does with
// CloseApplications=yes: Cabinette gets WM_QUERYENDSESSION/WM_ENDSESSION and quits (App.xaml.cs SessionEnding).
const
  RM_ERROR_MORE_DATA = 234;

function RmStartSession(var SessionHandle: DWORD; SessionFlags: DWORD; SessionKey: String): DWORD;
  external 'RmStartSession@rstrtmgr.dll stdcall delayload';
function RmEndSession(SessionHandle: DWORD): DWORD;
  external 'RmEndSession@rstrtmgr.dll stdcall delayload';
function RmRegisterResources(SessionHandle: DWORD; FileCount: UINT; FileNames: TArrayOfString;
  ApplicationCount: UINT; Applications: NativeUInt; ServiceCount: UINT; ServiceNames: NativeUInt): DWORD;
  external 'RmRegisterResources@rstrtmgr.dll stdcall delayload';
function RmGetList(SessionHandle: DWORD; var ProcInfoNeeded: UINT; var ProcInfo: UINT; AffectedApps: NativeUInt;
  var RebootReasons: DWORD): DWORD;
  external 'RmGetList@rstrtmgr.dll stdcall delayload';
function RmShutdown(SessionHandle: DWORD; ActionFlags: ULONG; StatusCallback: NativeUInt): DWORD;
  external 'RmShutdown@rstrtmgr.dll stdcall delayload';

// Asks every process that uses FileName to close and waits for it. Returns False only when a process is still
// running afterwards; a Restart Manager problem is logged and treated as nothing to close.
function CloseProcessesUsing(const FileName: String): Boolean;
var
  Session: DWORD;
  SessionKey: String;
  Files: TArrayOfString;
  Needed, Count: UINT;
  Reasons, Error: DWORD;
begin
  Result := True;
  try
    // Receives the session key (32 characters plus the terminating null).
    SessionKey := StringOfChar(' ', 64);
    Error := RmStartSession(Session, 0, SessionKey);
    if Error <> 0 then
    begin
      Log(Format('RmStartSession failed: %d', [Error]));
      Exit;
    end;
    try
      SetArrayLength(Files, 1);
      Files[0] := FileName;
      Error := RmRegisterResources(Session, 1, Files, 0, 0, 0, 0);
      if Error <> 0 then
      begin
        Log(Format('RmRegisterResources failed: %d', [Error]));
        Exit;
      end;
      Needed := 0;
      Count := 0;
      Reasons := 0;
      Error := RmGetList(Session, Needed, Count, 0, Reasons);
      if (Error <> 0) and (Error <> RM_ERROR_MORE_DATA) then
      begin
        Log(Format('RmGetList failed: %d', [Error]));
        Exit;
      end;
      if Needed = 0 then
      begin
        Log('No running process uses ' + FileName);
        Exit;
      end;
      Log(Format('%d process(es) use %s; asking them to close.', [Needed, FileName]));
      Error := RmShutdown(Session, 0, 0);
      Log(Format('RmShutdown: %d', [Error]));
      Result := Error = 0;
    finally
      RmEndSession(Session);
    end;
  except
    Log('Restart Manager failed: ' + GetExceptionMessage);
  end;
end;
// </restart-manager>

// <end-mcp>
// cabinette-mcp.exe runs as a child of an AI client (Claude Code or Codex), one per client session, and keeps its file
// in use. Restart Manager would close it with Ctrl+C, which Windows delivers to every process on the same console, so
// a client sharing its console with it (and the shell the client runs in) would get it too. Instead, Setup and the
// uninstaller end exactly the processes started from this installation's file, with Stop-Process (TerminateProcess),
// right before files are replaced or removed. A cabinette-mcp.exe elsewhere (a dev build) is left alone. It keeps no
// state, since Cabinette does the work, so nothing is lost: the AI client shows the server as disconnected until it
// reconnects (installer\README.md). The path comes in through an environment variable, and the script uses no double
// quotes. It runs in Windows PowerShell 5.1 (and 7) and compiles nothing, so it takes well under a second. Exit codes:
// 0 none running (any more), 1 one is still running, 9 the check failed.
function EndMcpScript: String;
begin
  Result :=
    '$ErrorActionPreference = ''Stop''; ' +
    'try { ' +
      '$target = [IO.Path]::GetFullPath($env:CABINETTE_MCP_EXE); ' +
      '$name = [IO.Path]::GetFileName($target); ' +
      // Which processes run the installed file. WMI's ExecutablePath is the path a process was started with, and an
      // 8.3 short name (C:\PROGRA~1\CABINE~1\CABINE~1.EXE, process name CABINE~1.EXE) or a path through a junction or
      // symbolic link reaches {app}\cabinette-mcp.exe too. So a process counts when its file is the very same file:
      // the same volume serial number and file index (GetFileInformationByHandle, declared through Reflection.Emit,
      // so nothing is compiled) or, when a file can't be opened, the same full path. A copy elsewhere is another file.
      '$type = [Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly((New-Object Reflection.AssemblyName(''CabinetteEndMcp'')), ''Run'').DefineDynamicModule(''CabinetteEndMcp'').DefineType(''Kernel32'', ''Public, Class''); ' +
      '$type.DefinePInvokeMethod(''GetFileInformationByHandle'', ''kernel32.dll'', ''Public, Static, PinvokeImpl'', ''Standard'', [bool], [Type[]]@([Microsoft.Win32.SafeHandles.SafeFileHandle], [byte[]]), ''Winapi'', ''Unicode'').SetImplementationFlags(''PreserveSig''); ' +
      '$kernel32 = $type.CreateType(); ' +
      // BY_HANDLE_FILE_INFORMATION (52 bytes): the volume serial number at offset 28, the file index at 44 (8 bytes).
      '$fileId = { param($path) try { $file = [IO.File]::Open($path, ''Open'', ''Read'', ''ReadWrite, Delete''); ' +
        'try { $info = New-Object byte[] 52; if ($kernel32::GetFileInformationByHandle($file.SafeFileHandle, $info)) { ' +
        '[BitConverter]::ToString($info, 28, 4) + ''/'' + [BitConverter]::ToString($info, 44, 8) } } finally { $file.Dispose() } } catch { } }; ' +
      '$targetId = & $fileId $target; ' +
      'Write-Output (''Looking for '' + $target + $(if ($targetId) { '' (file id '' + $targetId + '')'' } else { '' (no file id, compared by path only)'' })); ' +
      '$isTarget = { param($path) try { ([IO.Path]::GetFullPath($path) -eq $target) -or ($targetId -and ((& $fileId $path) -eq $targetId)) } catch { $false } }; ' +
      // Candidates by process name: the file's long name, or any 8.3 name of an .exe.
      '$filter = ''Name = '' + [char]39 + $name + [char]39 + '' OR Name LIKE '' + [char]39 + ''%~%.EXE'' + [char]39; ' +
      // A WMI service that hangs would hang Setup, which waits for this script: the query gives up after 30 s, and
      // the error ends the script with exit code 9 (logged, Setup goes on).
      '$find = { @(Get-CimInstance -ClassName Win32_Process -Filter $filter -OperationTimeoutSec 30 | ' +
        'Where-Object { $_.ExecutablePath -and (& $isTarget $_.ExecutablePath) }) }; ' +
      '$running = @(& $find); ' +
      'if ($running.Count -eq 0) { Write-Output (''Not running: '' + $target); exit 0 }; ' +
      'foreach ($p in $running) { ' +
        '$parent = Get-Process -Id $p.ParentProcessId -ErrorAction SilentlyContinue; ' +
        '$by = if ($parent) { $parent.ProcessName } else { ''a process that has ended'' }; ' +
        'Write-Output (''Ending '' + $p.Name + '' (pid '' + $p.ProcessId + ''), started by '' + $by + '' (pid '' + $p.ParentProcessId + '')'' + ' +
          '$(if ($p.ExecutablePath -ne $target) { '' as '' + $p.ExecutablePath })); ' +
        'try { Stop-Process -Id $p.ProcessId -Force } catch { Write-Output (''Could not end pid '' + $p.ProcessId + '': '' + $_.Exception.Message) } ' +
      '}; ' +
      'Wait-Process -Id @($running | ForEach-Object { $_.ProcessId }) -Timeout 5 -ErrorAction SilentlyContinue; ' +
      '$left = @(& $find); ' +
      'foreach ($p in $left) { Write-Output (''Still running: pid '' + $p.ProcessId) }; ' +
      'if ($left.Count -gt 0) { exit 1 }; ' +
      'exit 0 ' +
    '} catch { Write-Output (''Check failed: '' + $_.Exception.Message); exit 9 }';
end;

// Ends the cabinette-mcp.exe processes started from {app}. Returns False only when one is still running afterwards;
// a check that can't run is logged and treated as nothing to end (a file still in use then fails visibly).
function EndMcpServers: Boolean;
var
  McpExe: String;
  ResultCode: Integer;
begin
  Result := True;
  McpExe := ExpandConstant('{app}\{#McpExe}');
  if not FileExists(McpExe) then
  begin
    Log('Not installed (nothing to end): ' + McpExe);
    Exit;
  end;
  if not SetEnvironmentVariable('CABINETTE_MCP_EXE', McpExe) then
  begin
    Log('Could not pass the file name to the cabinette-mcp.exe check.');
    Exit;
  end;
  try
    if ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoLogo -NoProfile -NonInteractive -Command "' + EndMcpScript + '"',
      '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode, nil) then
    begin
      Log(Format('Ending %s: exit code %d', [McpExe, ResultCode]));
      Result := ResultCode <> 1;
    end
    else
      Log('The cabinette-mcp.exe check could not run: ' + SysErrorMessage(ResultCode));
  except
    Log('The cabinette-mcp.exe check failed: ' + GetExceptionMessage);
  end;
  SetEnvironmentVariable('CABINETTE_MCP_EXE', '');
end;

// Ends them, or lets the user close the AI clients and retry. Returns False when the user chose Cancel instead (a
// suppressed box answers Cancel); the caller then stops Setup or the uninstaller.
function EndMcpServersOrAsk: Boolean;
begin
  Result := True;
  while not EndMcpServers do
  begin
    if SuppressibleMsgBox(CustomMessage('McpEndFailed'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;
// </end-mcp>

// <cancel-install>
// Cancelling Setup from a BeforeInstall procedure. Inno Setup 7.1 catches every exception raised there, Abort
// included (Setup.MainFunc.pas, NotifyInstallEntry: shown as an error, then the installation goes on), so neither Abort
// nor RaiseException can stop it. What Setup's own Cancel button does can: WizardForm.Close hands it to
// TMainForm.Close, which (on the installing page, after CancelButtonClick) sets NeedToAbortInstall. Setup then stops
// at its next progress update, which comes before the next file replaces an existing one, logs "User canceled the
// installation process.", rolls back and exits with code 5 (Setup.Install.pas, PerformInstall).
var
  CancelRequested: Boolean;
  CancelAccepted: Boolean;

procedure CancelInstallation;
begin
  CancelAccepted := False;
  CancelRequested := True;
  try
    WizardForm.Close;
  finally
    CancelRequested := False;
  end;
  if CancelAccepted then
    Log('Cancelling the installation.')
  else
    // TMainForm.Close does nothing while the wizard is hidden (/VERYSILENT) or with /NOCANCEL. The next file then
    // stops Setup itself: replacing a file that is in use fails with Setup's Retry/Ignore/Cancel box, and Cancel (the
    // answer with /SUPPRESSMSGBOXES) is a cancel with rollback too.
    Log('Setup can''t be cancelled here (very silent or /NOCANCEL); it goes on.');
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if CancelRequested then
  begin
    // The user chose Cancel in the box that led here: no second "Exit Setup?" question.
    Confirm := False;
    CancelAccepted := Cancel;
  end;
end;
// </cancel-install>

// ---------------------------------------------------------------- Setup

function InitializeSetup: Boolean;
begin
  RuntimeMissing := not IsDesktopRuntimeInstalled;
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(CustomMessage('RuntimeDownloadCaption'),
    CustomMessage('RuntimeDownloadDescription'), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  RuntimePage := CreateOutputMarqueeProgressPage(CustomMessage('RuntimeInstallCaption'),
    CustomMessage('RuntimeInstallDescription'));
end;

procedure AddMemoPart(var Memo: String; const Part, NewLine: String);
begin
  if Part <> '' then
  begin
    if Memo <> '' then
      Memo := Memo + NewLine + NewLine;
    Memo := Memo + Part;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  AddMemoPart(Result, MemoDirInfo, NewLine);
  AddMemoPart(Result, MemoGroupInfo, NewLine);
  AddMemoPart(Result, MemoTasksInfo, NewLine);
  if RuntimeMissing then
    AddMemoPart(Result, CustomMessage('ReadyRuntimeHeading') + NewLine + Space + CustomMessage('ReadyRuntimeItem'),
      NewLine);
end;

// Runs the verified runtime installer quietly. On failure, Message says why.
function RunRuntimeInstaller(const FileName: String; var Message: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  RuntimePage.SetText(CustomMessage('RuntimeInstalling'), '');
  RuntimePage.Animate;
  if not Exec(FileName, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Message := FmtMessage(CustomMessage('RuntimeStartFailed'), [SysErrorMessage(ResultCode)]);
    Exit;
  end;
  Log(Format('.NET Desktop Runtime installer exit code: %d', [ResultCode]));
  case ResultCode of
    0: ;
    ERROR_SUCCESS_REBOOT_REQUIRED:
      begin
        // Installed; Windows asks for a restart at the end of Setup.
        RestartNeeded := True;
        Result := True;
        Exit;
      end;
    ERROR_SUCCESS_REBOOT_INITIATED:
      begin
        Message := CustomMessage('RuntimeRebootInitiated');
        Exit;
      end;
    ERROR_INSTALL_ALREADY_RUNNING:
      begin
        Message := CustomMessage('RuntimeOtherInstall');
        Exit;
      end;
  else
    begin
      Message := FmtMessage(CustomMessage('RuntimeInstallFailed'), [IntToStr(ResultCode)]);
      Exit;
    end;
  end;
  if not IsDesktopRuntimeInstalled then
  begin
    Message := CustomMessage('RuntimeStillMissing');
    Exit;
  end;
  Result := True;
end;

// Downloads the runtime installer, checks its signature and runs it. Returns False (with Message, or an empty
// Message when the user aborted the download) when Cabinette can't be installed yet.
function InstallRuntime(var Message: String): Boolean;
var
  FileName: String;
begin
  Result := False;
  Message := '';
  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, RuntimeFileName, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Runtime download aborted by the user.')
      else
        Message := FmtMessage(CustomMessage('RuntimeDownloadFailed'), [AddPeriod(GetExceptionMessage)]);
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  // {tmp} is a folder in the user's own temp directory, so programs running as this user could still replace the file
  // between the check and the run. That's no new risk: they could just as well replace Cabinette-Setup.exe itself.
  FileName := ExpandConstant('{tmp}\' + RuntimeFileName);
  RuntimePage.SetText(CustomMessage('RuntimeVerifying'), '');
  RuntimePage.Show;
  try
    RuntimePage.Animate;
    if not HasMicrosoftSignature(FileName) then
    begin
      Message := CustomMessage('RuntimeSignatureFailed');
      DeleteFile(FileName);
      Exit;
    end;
    Result := RunRuntimeInstaller(FileName, Message);
  finally
    RuntimePage.Hide;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Message: String;
  ErrorCode: Integer;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;
  RuntimeMissing := not IsDesktopRuntimeInstalled;
  if not RuntimeMissing then
    Exit;

  Result := InstallRuntime(Message);
  if Result then
    RuntimeMissing := False
  else if Message <> '' then
  begin
    // Stay on the Ready page (Install retries); a silent Setup exits here.
    if SuppressibleMsgBox(Message + #13#10#13#10 + FmtMessage(CustomMessage('RuntimeManual'), [RuntimeDownloadPageUrl]),
      mbCriticalError, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', RuntimeDownloadPageUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

// BeforeInstall of the first [Files] entry, so inside the installation proper: Restart Manager has closed Cabinette
// ("Closing applications...", Setup.Install.pas: PerformInstall calls ShutdownApplications before it processes
// [Files]), and no file has been replaced yet. When Cabinette refused to close, Setup's "unable to close" box came
// first, and Cancel there leaves the AI clients connected. Not CurStepChanged(ssInstall): that runs before
// PerformInstall, so before Cabinette is asked to close, and Abort there is a fatal error (exit code 3, no rollback).
procedure EndMcpServersBeforeFiles;
begin
  if not EndMcpServersOrAsk then
    CancelInstallation;
end;

// ---------------------------------------------------------------- Uninstall

// The program path of a Run-key command line, like StartupCommand.Split in the app.
function CommandProgram(const CommandLine: String): String;
var
  Text: String;
  Index: Integer;
begin
  Text := Trim(CommandLine);
  if Copy(Text, 1, 1) = '"' then
  begin
    Delete(Text, 1, 1);
    Index := Pos('"', Text);
  end
  else
    Index := Pos(' ', Text);
  if Index > 0 then
    Text := Copy(Text, 1, Index - 1);
  Result := Text;
end;

// Removes "start with Windows" of the account that uninstalls, but only when it starts this installation's
// Cabinette.exe (another copy, like a dev build, keeps its entry).
procedure RemoveStartupEntry;
var
  CommandLine, ProgramPath, AppExe: String;
begin
  if not RegQueryStringValue(HKCU, '{#RunKey}', '{#RunValue}', CommandLine) then
    Exit;
  ProgramPath := CommandProgram(CommandLine);
  AppExe := ExpandConstant('{app}\Cabinette.exe');
  if (ProgramPath <> '') and (CompareText(ExpandFileName(ProgramPath), ExpandFileName(AppExe)) = 0) then
  begin
    if RegDeleteValue(HKCU, '{#RunKey}', '{#RunValue}') then
      Log('Removed the "start with Windows" entry: ' + CommandLine)
    else
      Log('Could not remove the "start with Windows" entry: ' + CommandLine);
  end
  else
    Log('Kept the "start with Windows" entry; it starts another program: ' + CommandLine);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  // A running Cabinette would keep its files locked: close it first, or let the user quit it and retry.
  while not CloseProcessesUsing(ExpandConstant('{app}\Cabinette.exe')) do
  begin
    if SuppressibleMsgBox(CustomMessage('CloseFailed'), mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
      Abort;
  end;
  // Then the MCP servers that AI clients started from here, which keep cabinette-mcp.exe in use. Abort stops the
  // uninstaller here (CurUninstallStepChanged(usUninstall)) before it removes anything.
  if not EndMcpServersOrAsk then
    Abort;
  RemoveStartupEntry;
end;
