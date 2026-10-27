<#
.SYNOPSIS
    Builds the Cabinette installer: artifacts\installer\Cabinette-Setup.exe.

.DESCRIPTION
    1. Reads the version (Directory.Build.props) from MSBuild.
    2. Publishes src\Cabinette.App (Release, framework-dependent, win-x64) into artifacts\publish\Cabinette.
    3. Publishes src\Cabinette.Mcp the same way but as a single file, into its own temporary folder, and copies
       cabinette-mcp.exe and its .pdb next to Cabinette.exe. As one file, its own dependencies (the MCP SDK and its
       Microsoft.Extensions.* versions) stay inside cabinette-mcp.exe and can never overwrite or clash with the app's
       DLLs in the same folder. A compiler or publish warning fails the build. The test helper project
       (tests\Cabinette.Mcp.TestHelper) is never published.
    4. Restores installer\InnoSetup.csproj, which pins the Tools.InnoSetup NuGet package, and asks MSBuild where its
       ISCC.exe is. Nothing gets installed on the PC; the compiler stays in the NuGet cache.
    5. Compiles installer\Cabinette.iss with ISCC. Any ISCC warning fails the build.

    Works in Windows PowerShell 5.1 and PowerShell 7. Run from anywhere:
        powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repo 'src\Cabinette.App\Cabinette.App.csproj'
$mcpProject = Join-Path $repo 'src\Cabinette.Mcp\Cabinette.Mcp.csproj'
$toolProject = Join-Path $PSScriptRoot 'InnoSetup.csproj'
$script = Join-Path $PSScriptRoot 'Cabinette.iss'
$publishDir = Join-Path $repo 'artifacts\publish\Cabinette'
$mcpPublishDir = Join-Path $repo 'artifacts\publish\cabinette-mcp.tmp'
$outputDir = Join-Path $repo 'artifacts\installer'
# What the MCP server's publish ships next to Cabinette.exe (SPEC section 10: one file, plus its debug symbols).
$mcpExe = 'cabinette-mcp.exe'
$mcpFiles = @($mcpExe, 'cabinette-mcp.pdb')

# Runs a native command; stops on a non-zero exit code. Returns its output lines (stdout and stderr).
function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell turns stderr lines into errors otherwise
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) {
        $output | ForEach-Object { Write-Host "$_" }
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $exitCode."
    }
    return $output
}

# MSBuild and NuGet warnings in the canonical format ("<origin>: warning <code>: <text>"), each once.
function Select-BuildWarnings([object[]]$Lines) {
    return @($Lines | ForEach-Object { "$_".Trim() } | Where-Object { $_ -match ':\s*warning\s*[A-Za-z]*\d*\s*:' } |
        Select-Object -Unique)
}

Write-Host '==> Version'
$version = (Invoke-Native 'dotnet' @('msbuild', $appProject, '-nologo', '-getProperty:Version') | Select-Object -Last 1).ToString().Trim()
$numericVersion = ($version -split '[-+]')[0]
if ($numericVersion -notmatch '^\d+(\.\d+){1,3}$') {
    throw "Unexpected version '$version' from Directory.Build.props."
}
Write-Host "    $version"

Write-Host "==> Publishing Cabinette to $publishDir"
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
Invoke-Native 'dotnet' @('publish', $appProject, '-nologo', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-o', $publishDir) | ForEach-Object { Write-Host "    $_" }
if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'Cabinette.exe'))) {
    throw "Publishing did not produce Cabinette.exe in $publishDir."
}

Write-Host "==> Publishing $mcpExe (single file) next to Cabinette.exe"
if (Test-Path -LiteralPath $mcpPublishDir) {
    Remove-Item -LiteralPath $mcpPublishDir -Recurse -Force
}
# A full rebuild first, so every compiler warning shows up even when an earlier build left the project up to date;
# then the publish bundles that build. Only this project and Cabinette.Ipc are built: never the test helper.
$mcpArguments = @('-nologo', '-tl:off', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true')
$mcpOutput = @(Invoke-Native 'dotnet' (@('build', $mcpProject, '--no-incremental') + $mcpArguments))
$mcpOutput += @(Invoke-Native 'dotnet' (@('publish', $mcpProject, '--no-build') + $mcpArguments + @('-o', $mcpPublishDir)))
$mcpOutput | ForEach-Object { Write-Host "    $_" }
$mcpWarnings = @(Select-BuildWarnings $mcpOutput)
if ($mcpWarnings.Count -gt 0) {
    throw "Publishing $mcpExe reported $($mcpWarnings.Count) warning(s):`n$($mcpWarnings -join "`n")"
}
# Single file: only the program and debug symbols. Anything else (a DLL left out of the bundle) would be missing
# at run time, since only the files below are copied.
$extra = @(Get-ChildItem -LiteralPath $mcpPublishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne $mcpExe })
if ($extra.Count -gt 0) {
    throw "$mcpExe was not published as a single file; the publish also produced: $(($extra | ForEach-Object { $_.Name }) -join ', ')."
}
foreach ($name in $mcpFiles) {
    $source = Join-Path $mcpPublishDir $name
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Publishing did not produce $name in $mcpPublishDir."
    }
    if (Test-Path -LiteralPath (Join-Path $publishDir $name)) {
        throw "The app's publish already contains $name; it must come only from src\Cabinette.Mcp."
    }
    Copy-Item -LiteralPath $source -Destination $publishDir
}
Remove-Item -LiteralPath $mcpPublishDir -Recurse -Force

# Both programs carry the version of Directory.Build.props; Setup ships them as a pair.
$appVersion = (Get-Item -LiteralPath (Join-Path $publishDir 'Cabinette.exe')).VersionInfo.ProductVersion
$mcpVersion = (Get-Item -LiteralPath (Join-Path $publishDir $mcpExe)).VersionInfo.ProductVersion
if ($mcpVersion -ne $appVersion) {
    throw "$mcpExe has version '$mcpVersion', Cabinette.exe '$appVersion'."
}
Write-Host "    $mcpExe $mcpVersion"
$helper = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter '*TestHelper*')
if ($helper.Count -gt 0) {
    throw "The test helper must never ship, but the publish folder has: $(($helper | ForEach-Object { $_.Name }) -join ', ')."
}

Write-Host '==> Inno Setup compiler (Tools.InnoSetup from NuGet)'
Invoke-Native 'dotnet' @('restore', $toolProject, '-nologo') | Out-Null
$iscc = (Invoke-Native 'dotnet' @('msbuild', $toolProject, '-nologo', '-getProperty:InnoSetupCompiler') | Select-Object -Last 1).ToString().Trim()
if (-not $iscc -or -not (Test-Path -LiteralPath $iscc)) {
    throw "ISCC.exe not found (InnoSetupCompiler='$iscc')."
}
$iscc = [System.IO.Path]::GetFullPath($iscc)
Write-Host "    $iscc"

Write-Host '==> Compiling installer\Cabinette.iss'
# --quiet hides warnings unless --messages-jsonl is on; then errors and warnings come as one JSON object per line.
$output = Invoke-Native $iscc @('--quiet', '--messages-jsonl', "/DAppVersion=$version", "/DAppVersionNumeric=$numericVersion",
    "/DPublishDir=$publishDir", "/DOutputDir=$outputDir", $script)
$warnings = 0
foreach ($line in @($output | ForEach-Object { "$_" } | Where-Object { $_.Trim() -ne '' })) {
    try {
        $message = $line | ConvertFrom-Json
        Write-Host "    $($message.message)"
        if ($message.severity -eq 'warning') {
            $warnings++
        }
    }
    catch {
        Write-Host "    $line"
    }
}
$setup = Join-Path $outputDir 'Cabinette-Setup.exe'
if ($warnings -gt 0) {
    Remove-Item -LiteralPath $setup -ErrorAction SilentlyContinue
    throw "ISCC reported $warnings warning(s) (see above)."
}

$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$size = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)
Write-Host "==> Done: $setup ($size MB)"
Write-Host "    SHA-256 $hash"
