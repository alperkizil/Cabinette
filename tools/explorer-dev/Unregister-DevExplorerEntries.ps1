<#
.SYNOPSIS
    Removes the dev Explorer entries that Register-DevExplorerEntries.ps1 added for the current user.

.DESCRIPTION
    Deletes exactly these keys under HKCU\Software\Classes, when present, and nothing else:

      AllFilesystemObjects\shell\Cabinette.Dev.Copy
      AllFilesystemObjects\shell\Cabinette.Dev.Pointer
      CLSID\{C2FACF9C-2622-4B9D-8EF9-D096067DA6F4}
      CLSID\{BD7553E4-FBCA-4E73-9B4C-81D9C5477592}

    The installed Cabinette's entries are never touched. A dev Cabinette that is still running keeps serving the classes
    until it quits, but Explorer no longer shows the entries. Use -WhatIf to see what would be deleted.

.EXAMPLE
    pwsh -File tools\explorer-dev\Unregister-DevExplorerEntries.ps1
#>
#Requires -Version 7
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Keep in sync with Register-DevExplorerEntries.ps1 (a test checks both).
$subKeys = @(
    'Software\Classes\AllFilesystemObjects\shell\Cabinette.Dev.Copy',
    'Software\Classes\AllFilesystemObjects\shell\Cabinette.Dev.Pointer',
    'Software\Classes\CLSID\{C2FACF9C-2622-4B9D-8EF9-D096067DA6F4}',
    'Software\Classes\CLSID\{BD7553E4-FBCA-4E73-9B4C-81D9C5477592}'
)

foreach ($subKey in $subKeys) {
    $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subKey)
    if ($null -eq $existing) {
        Write-Host "Not present: HKCU\$subKey"
        continue
    }

    $existing.Dispose()
    if ($PSCmdlet.ShouldProcess("HKCU\$subKey", 'Delete the key and its subkeys')) {
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($subKey, $false)
        Write-Host "Removed: HKCU\$subKey"
    }
}
