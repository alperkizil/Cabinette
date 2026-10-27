<#
.SYNOPSIS
    Adds the dev Explorer entries "Copy to Cabinette (dev)" and "Add to Cabinette (Pointer, dev)" for the current user.

.DESCRIPTION
    Writes only under HKCU\Software\Classes (no admin rights, nothing machine-wide, nothing for other accounts):

      AllFilesystemObjects\shell\Cabinette.Dev.Copy      the verb: text, MultiSelectModel=Player, icon, DropTarget\Clsid
      AllFilesystemObjects\shell\Cabinette.Dev.Pointer   the same for pointer mode
      CLSID\{C2FACF9C-2622-4B9D-8EF9-D096067DA6F4}       LocalServer32 = "<exe>" --data-dir "<dir>"
      CLSID\{BD7553E4-FBCA-4E73-9B4C-81D9C5477592}       the same

    Explorer then hands every selection to the given build as one COM drop, and COM starts it with -Embedding when it
    isn't running. The ids are the dev ones in src/Cabinette.Windows/Contracts.cs (ExplorerIntegration.Dev*), which only
    --data-dir runs serve. The installed Cabinette's entries are never touched.

    Undo: Unregister-DevExplorerEntries.ps1. Use -WhatIf to see every write without making it.

.PARAMETER CabinetteExe
    The built Cabinette.exe that serves the entries, for example
    src\Cabinette.App\bin\Debug\net10.0-windows10.0.19041.0\Cabinette.exe

.PARAMETER DataDir
    The throwaway data folder of the dev run (its --data-dir). Never the real %LocalAppData%\Cabinette.

.EXAMPLE
    pwsh -File tools\explorer-dev\Register-DevExplorerEntries.ps1 -CabinetteExe src\Cabinette.App\bin\Debug\net10.0-windows10.0.19041.0\Cabinette.exe -DataDir $env:TEMP\cabinette-explorer-dev -WhatIf
#>
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $CabinetteExe,

    [Parameter(Mandatory)]
    [string] $DataDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- checks ----------------------------------------------------------------------------------------------------------

$exe = [IO.Path]::GetFullPath($CabinetteExe)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetFileName($exe) -ne 'Cabinette.exe') {
    throw "Not a built Cabinette.exe: $exe"
}

$data = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($DataDir))
if ($data.EndsWith('\')) {
    throw "Use a folder for -DataDir, not a drive root: $data"
}

$realDataDir = [IO.Path]::TrimEndingDirectorySeparator((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Cabinette'))
if ($data -ieq $realDataDir) {
    throw "-DataDir is Cabinette's real data folder ($realDataDir). Use a throwaway folder."
}

# ---- what gets written -----------------------------------------------------------------------------------------------

# Keep in sync with ExplorerIntegration.DevCopyVerb/DevPointerVerb and the Dev*DropTargetClsid values (a test checks it).
$entries = @(
    @{ Verb = 'Cabinette.Dev.Copy'; Text = 'Copy to Cabinette (dev)'; Clsid = '{C2FACF9C-2622-4B9D-8EF9-D096067DA6F4}'; ClassName = 'Cabinette dev drop target (copy)' },
    @{ Verb = 'Cabinette.Dev.Pointer'; Text = 'Add to Cabinette (Pointer, dev)'; Clsid = '{BD7553E4-FBCA-4E73-9B4C-81D9C5477592}'; ClassName = 'Cabinette dev drop target (pointer)' }
)

# COM appends " -Embedding" to this command when it starts the server.
$serverCommand = "`"$exe`" --data-dir `"$data`""

$cmdlet = $PSCmdlet

function Set-UserClassValue([string] $SubKey, [string] $Name, [string] $Value) {
    $label = if ($Name) { $Name } else { '(Default)' }
    if ($cmdlet.ShouldProcess("HKCU\$SubKey [$label]", "Set to '$Value'")) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($SubKey)
        try {
            $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
        }
        finally {
            $key.Dispose()
        }
    }
}

foreach ($entry in $entries) {
    $verbKey = "Software\Classes\AllFilesystemObjects\shell\$($entry.Verb)"
    $classKey = "Software\Classes\CLSID\$($entry.Clsid)"

    Set-UserClassValue $verbKey '' $entry.Text
    Set-UserClassValue $verbKey 'MUIVerb' $entry.Text
    Set-UserClassValue $verbKey 'MultiSelectModel' 'Player'
    Set-UserClassValue $verbKey 'Icon' "$exe,0"
    Set-UserClassValue "$verbKey\DropTarget" 'Clsid' $entry.Clsid

    Set-UserClassValue $classKey '' $entry.ClassName
    Set-UserClassValue "$classKey\LocalServer32" '' $serverCommand
}

if ($WhatIfPreference) {
    return
}

# ---- read back -------------------------------------------------------------------------------------------------------

Write-Host 'Written (read back from the registry):'
foreach ($entry in $entries) {
    foreach ($subKey in @(
            "Software\Classes\AllFilesystemObjects\shell\$($entry.Verb)",
            "Software\Classes\AllFilesystemObjects\shell\$($entry.Verb)\DropTarget",
            "Software\Classes\CLSID\$($entry.Clsid)",
            "Software\Classes\CLSID\$($entry.Clsid)\LocalServer32")) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
        try {
            foreach ($name in $key.GetValueNames()) {
                $label = if ($name) { $name } else { '(Default)' }
                Write-Host ("  HKCU\{0} [{1}] = {2}" -f $subKey, $label, $key.GetValue($name))
            }
        }
        finally {
            $key.Dispose()
        }
    }
}

Write-Host ''
Write-Host 'Shift+right-click a file in Explorer (or "Show more options") to see the dev entries.'
Write-Host 'Undo: pwsh -File tools\explorer-dev\Unregister-DevExplorerEntries.ps1'
