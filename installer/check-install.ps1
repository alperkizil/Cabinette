<#
.SYNOPSIS
    Shows what the Cabinette installer registered. Read-only: it changes nothing and needs no admin rights.

.DESCRIPTION
    Prints the Explorer entries and COM classes (HKLM\Software\Classes), the uninstall entry, the "start with Windows"
    value of the current account, whether the program files and the library database exist, the installed
    cabinette-mcp.exe (its version, whether it matches Cabinette.exe, and its --help, which only prints usage and never
    starts Cabinette), and running Cabinette and cabinette-mcp processes. Run it before installing, after installing,
    after an upgrade and after uninstalling:
        powershell -NoProfile -ExecutionPolicy Bypass -File installer\check-install.ps1

    The program files are looked for where Setup put them: the uninstall entry's InstallLocation, else
    C:\Program Files\Cabinette (the 64-bit Program Files folder, also from a 32-bit PowerShell, where
    $env:ProgramFiles is Program Files (x86)). -InstallDir checks another folder instead, for example the publish
    folder before building the installer (artifacts\publish\Cabinette). The registry checks stay the same.
#>
param([string]$InstallDir)

# A 32-bit PowerShell sees the 32-bit registry view under HKLM\Software\Classes\CLSID, so both CLSID lines below would
# show the 32-bit registration.
if ([Environment]::Is64BitOperatingSystem -and -not [Environment]::Is64BitProcess) {
    'Note: this is a 32-bit PowerShell; the CLSID lines show the 32-bit registry view. Run the 64-bit powershell.exe for the full check.'
}

$classes = 'Registry::HKEY_LOCAL_MACHINE\Software\Classes'
foreach ($verb in 'Cabinette.Copy', 'Cabinette.Pointer') {
    $key = Get-Item "$classes\AllFilesystemObjects\shell\$verb" -ErrorAction SilentlyContinue
    if ($key) {
        $dropTarget = Get-Item "$classes\AllFilesystemObjects\shell\$verb\DropTarget" -ErrorAction SilentlyContinue
        $clsid = if ($dropTarget) { $dropTarget.GetValue('Clsid') } else { '<no DropTarget>' }
        "{0}: MUIVerb='{1}' Icon='{2}' MultiSelectModel='{3}' DropTarget='{4}'" -f $verb, $key.GetValue('MUIVerb'),
            $key.GetValue('Icon'), $key.GetValue('MultiSelectModel'), $clsid
    }
    else {
        "${verb}: missing"
    }
}
# The classes are registered in both registry views (the 32-bit one is WOW6432Node).
foreach ($view in @{ Name = '64-bit'; Path = "$classes\CLSID" }, @{ Name = '32-bit'; Path = "$classes\WOW6432Node\CLSID" }) {
    foreach ($id in '{E92F98C8-F1F4-4B7F-BBBE-38D6678CE55E}', '{ED9B5A7A-4497-43EF-9727-8C6AFCD8CAE2}') {
        $key = Get-Item "$($view.Path)\$id" -ErrorAction SilentlyContinue
        if ($key) {
            $server = Get-Item "$($view.Path)\$id\LocalServer32" -ErrorAction SilentlyContinue
            "{0} ({1}): '{2}' LocalServer32='{3}'" -f $id, $view.Name, $key.GetValue(''), $(if ($server) { $server.GetValue('') })
        }
        else {
            "${id} ($($view.Name)): missing"
        }
    }
}
# Setup is 64-bit and writes its uninstall entry to the 64-bit registry view; read that view from any PowerShell.
$uninstall = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64').OpenSubKey(
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{17D5A5C2-4B3D-4D0C-9945-12604F711F45}_is1')
$installLocation = $null
if ($uninstall) {
    $installLocation = "$($uninstall.GetValue('InstallLocation'))".TrimEnd('\')
    "Uninstall entry: {0} {1} in {2}" -f $uninstall.GetValue('DisplayName'), $uninstall.GetValue('DisplayVersion'), $installLocation
    $uninstall.Close()
}
else {
    'Uninstall entry: missing'
}
if (-not $InstallDir) {
    if ($installLocation) { $InstallDir = $installLocation }
    elseif ($env:ProgramW6432) { $InstallDir = Join-Path $env:ProgramW6432 'Cabinette' }
    else { $InstallDir = Join-Path $env:ProgramFiles 'Cabinette' }
}
$run = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
"Start with Windows (this account): " + $(if ($run -and $run.PSObject.Properties['Cabinette']) { $run.Cabinette } else { 'missing' })

$appExe = Join-Path $InstallDir 'Cabinette.exe'
$mcpExe = Join-Path $InstallDir 'cabinette-mcp.exe'
"Program files present: " + (Test-Path -LiteralPath $appExe) + " ($appExe)"
"Library database present: " + (Test-Path (Join-Path $env:LOCALAPPDATA 'Cabinette\cabinette.db'))

# The MCP server for Claude Code and Codex. Setup registers nothing for it; the AI clients' own settings are not read.
if (Test-Path -LiteralPath $mcpExe) {
    # ProductVersion is "<version>+<commit>"; both programs come from one build, so they must be equal.
    $mcpVersion = (Get-Item -LiteralPath $mcpExe).VersionInfo.ProductVersion
    $appVersion = if (Test-Path -LiteralPath $appExe) { (Get-Item -LiteralPath $appExe).VersionInfo.ProductVersion } else { $null }
    "cabinette-mcp.exe: version $mcpVersion; " + $(if (-not $appVersion) { 'Cabinette.exe missing' }
        elseif ($mcpVersion -eq $appVersion) { 'matches Cabinette.exe' } else { "DIFFERS from Cabinette.exe ($appVersion)" })

    # --help prints the usage to stderr and exits 0; it never starts Cabinette. Run with a timeout, no window.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $mcpExe
    $psi.Arguments = '--help'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    try {
        $process = [System.Diagnostics.Process]::Start($psi)
        $process.StandardInput.Close()
        $stderr = $process.StandardError.ReadToEndAsync()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        if ($process.WaitForExit(15000)) {
            $usage = @($stderr.Result -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
            "cabinette-mcp.exe --help: exit code $($process.ExitCode); " +
                $(if ($usage.Count -gt 0) { "first line: $($usage[0].Trim())" } else { 'no usage printed' }) +
                $(if ($stdout.Result.Trim() -ne '') { '; it also wrote to stdout (unexpected)' })
        }
        else {
            $process.Kill()
            'cabinette-mcp.exe --help: no answer within 15 s (ended)'
        }
    }
    catch {
        "cabinette-mcp.exe --help: could not run ($($_.Exception.Message))"
    }
}
else {
    "cabinette-mcp.exe: missing ($mcpExe)"
}

Get-Process Cabinette -ErrorAction SilentlyContinue | ForEach-Object { "Running: pid $($_.Id) $($_.Path)" }
Get-Process cabinette-mcp -ErrorAction SilentlyContinue | ForEach-Object { "Running: pid $($_.Id) $($_.Path)" }
