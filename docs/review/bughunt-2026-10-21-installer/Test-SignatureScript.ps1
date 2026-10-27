<#
.SYNOPSIS
    Bug-hunt check: runs the installer's Authenticode check (the PowerShell script inside installer\Cabinette.iss,
    function SignatureScript) through Windows PowerShell exactly as Setup does, against known files.

.DESCRIPTION
    Extracts the script's string literal from Cabinette.iss and starts
        powershell.exe -NoLogo -NoProfile -NonInteractive -Command "<script>"
    with the file in CABINETTE_VERIFY_FILE, like HasMicrosoftSignature in the .iss. Read-only apart from two
    temporary copies of dotnet.exe in $env:TEMP (one with a flipped byte), which are deleted again. Nothing is
    installed, downloaded or run from the installer.

    Expected exit codes: 0 Microsoft-signed, 2 not signed / hash mismatch, 3 signed by someone else, 9 check failed.
#>
param([string] $Iss = (Join-Path $PSScriptRoot '..\..\..\installer\Cabinette.iss'))

$text = Get-Content -LiteralPath $Iss -Raw
$m = [regex]::Match($text, '(?s)function SignatureScript: String;\s*begin\s*Result :=(.*?);\s*end;')
if (-not $m.Success) { throw 'SignatureScript not found in the .iss' }
$script = ''
foreach ($line in $m.Groups[1].Value -split "`r?`n") {
    $t = $line.Trim()
    if ($t.StartsWith('//')) { continue }
    foreach ($lit in [regex]::Matches($t, "'((?:[^']|'')*)'")) {
        $script += $lit.Groups[1].Value.Replace("''", "'")
    }
}
if ($script.Contains('"')) { throw 'The script contains a double quote; Setup passes it inside -Command "..."' }

function Invoke-Check([string] $File) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
    $psi.Arguments = '-NoLogo -NoProfile -NonInteractive -Command "' + $script + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.Environment['CABINETTE_VERIFY_FILE'] = $File
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    [pscustomobject]@{ Exit = $p.ExitCode; Out = ($out.Trim() -replace "`r?`n", ' | ') }
}

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$work = Join-Path $env:TEMP ('cabinette-sigcheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $odd = Join-Path $work "odd 'name' `$x & (y).exe"
    Copy-Item -LiteralPath $dotnet -Destination $odd
    $tampered = Join-Path $work 'tampered.bin'
    Copy-Item -LiteralPath $dotnet -Destination $tampered
    $bytes = [IO.File]::ReadAllBytes($tampered)
    $i = [int]($bytes.Length / 2)
    $bytes[$i] = $bytes[$i] -bxor 0xFF
    [IO.File]::WriteAllBytes($tampered, $bytes)

    $cases = @(
        @{ File = $dotnet; Expect = 0; Why = 'Microsoft-signed (root: Microsoft Root Certificate Authority 2011)' },
        @{ File = "$env:WINDIR\System32\notepad.exe"; Expect = 0; Why = 'Microsoft-signed (root 2010)' },
        @{ File = $odd; Expect = 0; Why = 'path with quote, dollar, ampersand, parentheses' },
        @{ File = $tampered; Expect = 2; Why = 'Microsoft-signed file with one byte flipped (HashMismatch)' },
        @{ File = (Join-Path $work 'missing.exe'); Expect = 9; Why = 'missing file' }
    )
    $iscc = Get-ChildItem "$env:USERPROFILE\.nuget\packages\tools.innosetup\*\tools\ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($iscc) { $cases += @{ File = $iscc.FullName; Expect = 3; Why = 'valid signature by a non-Microsoft publisher' } }

    $failed = 0
    foreach ($c in $cases) {
        $r = Invoke-Check $c.File
        $ok = $r.Exit -eq $c.Expect
        if (-not $ok) { $failed++ }
        "{0} exit={1} expected={2}: {3}`n    {4}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' }), $r.Exit, $c.Expect, $c.Why, $r.Out)
    }
    if ($failed) { throw "$failed check(s) failed." }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
