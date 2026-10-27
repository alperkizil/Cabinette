<#
.SYNOPSIS
    Checks, with Inno Setup 7.1 itself, how Setup can be cancelled from a BeforeInstall procedure (finding F1 of the
    stage 4 installer bug hunt). Installs nothing system-wide and shows nothing.

.DESCRIPTION
    installer\Cabinette.iss ends the running cabinette-mcp.exe from a BeforeInstall procedure on its first [Files] entry,
    and cancels Setup from there with the <cancel-install> block (CancelInstallation) when the user chooses Cancel. This
    script builds three tiny throwaway installers around that exact block and runs them:
      cancel-silent      CancelInstallation, /SILENT       expected: exit code 5, rolled back, the old file kept
      cancel-verysilent  CancelInstallation, /VERYSILENT   expected: logged as not cancellable, installs (exit code 0)
      abort-silent       Abort instead                     expected: Setup ignores it and installs (exit code 0)
    The last case is why Cabinette.iss doesn't use Abort there.

    The installers are per-user (PrivilegesRequired=lowest) with CreateUninstallRegKey=no and CloseApplications=no: no
    administrator rights, no registry, no Restart Manager. They install into a temp folder, which is removed afterwards.
    They run on a separate desktop that is never shown, inside a job object, so every process they start ends with the
    job. Works in Windows PowerShell 5.1 and PowerShell 7; needs the Tools.InnoSetup package restored (build.ps1 does it).
        powershell -NoProfile -ExecutionPolicy Bypass -File docs\review\bughunt-2026-10-24-installer\Test-CancelFromBeforeInstall.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$toolProject = Join-Path $repo 'installer\InnoSetup.csproj'
$ErrorActionPreference = 'Continue'   # Windows PowerShell turns a native program's stderr lines into errors otherwise
& dotnet restore $toolProject -nologo | Out-Null
$iscc = "$(& dotnet msbuild $toolProject -nologo '-getProperty:InnoSetupCompiler' | Select-Object -Last 1)".Trim()
$ErrorActionPreference = 'Stop'
if (-not $iscc -or -not (Test-Path -LiteralPath $iscc)) {
    throw "ISCC.exe not found (InnoSetupCompiler='$iscc'). Run installer\build.ps1 once, or dotnet restore installer\InnoSetup.csproj."
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class CabinetteHiddenDesktopRunner
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic; public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObjectW(IntPtr sa, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    /// <summary>Runs a program on a new desktop that is never shown and returns its exit code, or -1 after the timeout.
    /// The program and everything it starts run in a job that is terminated at the end.</summary>
    public static int Run(string desktopName, string exe, string arguments, int timeoutMs)
    {
        const uint GENERIC_ALL = 0x10000000, CREATE_SUSPENDED = 0x4, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        IntPtr desktop = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception();
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) { CloseDesktop(desktop); throw new Win32Exception(); }
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.Basic.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                throw new Win32Exception();
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = desktopName;
            var cmd = new StringBuilder("\"" + exe + "\" " + arguments);
            PROCESS_INFORMATION pi;
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED, IntPtr.Zero, null, ref si, out pi))
                throw new Win32Exception();
            try
            {
                if (!AssignProcessToJobObject(job, pi.hProcess)) throw new Win32Exception();
                ResumeThread(pi.hThread);
                if (WaitForSingleObject(pi.hProcess, (uint)timeoutMs) != 0)
                {
                    return -1;
                }
                uint code;
                GetExitCodeProcess(pi.hProcess, out code);
                return (int)code;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        finally
        {
            TerminateJobObject(job, 1);
            CloseHandle(job);
            CloseDesktop(desktop);
        }
    }
}
'@

$iss = Get-Content -Raw -LiteralPath (Join-Path $repo 'installer\Cabinette.iss')
$block = [regex]::Match($iss, '(?s)// <cancel-install>.*?// </cancel-install>')
if (-not $block.Success) {
    throw 'installer\Cabinette.iss has no <cancel-install> block.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('cabinette-cancel-probe-' + [guid]::NewGuid().ToString('N'))
$src = Join-Path $work 'src'
New-Item -ItemType Directory -Force $src | Out-Null
# More than one 64 KB block, like cabinette-mcp.exe: Setup checks for a cancel after each block it unpacks.
$bytes = New-Object byte[] (1MB)
(New-Object Random 7).NextBytes($bytes)
[IO.File]::WriteAllBytes((Join-Path $src 'first.bin'), $bytes)
Set-Content -LiteralPath (Join-Path $src 'second.txt') -Value 'new second'

$cases = @(
    @{ Name = 'cancel-silent'; Body = 'CancelInstallation;'; Mode = '/SILENT'; Exit = 5; Replaced = $false
       Log = @('Cancelling the installation.', 'User canceled the installation process.', 'Rolling back changes.') },
    @{ Name = 'cancel-verysilent'; Body = 'CancelInstallation;'; Mode = '/VERYSILENT'; Exit = 0; Replaced = $true
       Log = @('Setup can''t be cancelled here', 'Installation process succeeded.') },
    @{ Name = 'abort-silent'; Body = 'Abort;'; Mode = '/SILENT'; Exit = 0; Replaced = $true
       Log = @('Expression error', 'Installation process succeeded.') }
)
$failed = 0
try {
    foreach ($case in $cases) {
        $dir = Join-Path $work $case.Name
        $app = Join-Path $dir 'app'
        New-Item -ItemType Directory -Force $app | Out-Null
        Set-Content -LiteralPath (Join-Path $app 'first.bin') -Value 'old first'

        $script = @"
[Setup]
AppId=CabinetteCancelProbe-$($case.Name)
AppName=Cabinette cancel probe
AppVersion=1.0
DefaultDirName=$app
PrivilegesRequired=lowest
CreateUninstallRegKey=no
UsePreviousAppDir=no
DirExistsWarning=no
CloseApplications=no
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
OutputDir=$dir
OutputBaseFilename=probe-setup

[Files]
Source: "$src\first.bin"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: ProbeBeforeFiles
Source: "$src\second.txt"; DestDir: "{app}"; Flags: ignoreversion

[Code]
$($block.Value)

procedure ProbeBeforeFiles;
begin
  $($case.Body)
end;
"@
        Set-Content -LiteralPath (Join-Path $dir 'probe.iss') -Value $script
        $ErrorActionPreference = 'Continue'
        $compile = & $iscc --quiet (Join-Path $dir 'probe.iss') 2>&1
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) {
            $compile | ForEach-Object { Write-Host "$_" }
            throw "ISCC failed for $($case.Name)."
        }

        $log = Join-Path $dir 'setup.log'
        $exit = [CabinetteHiddenDesktopRunner]::Run('CabinetteProbe' + [guid]::NewGuid().ToString('N'),
            (Join-Path $dir 'probe-setup.exe'), "$($case.Mode) /SUPPRESSMSGBOXES /NORESTART /LOG=`"$log`"", 60000)
        $text = if (Test-Path -LiteralPath $log) { Get-Content -Raw -LiteralPath $log } else { '' }
        $replaced = (Get-Item -LiteralPath (Join-Path $app 'first.bin')).Length -gt 100
        $problems = @()
        if ($exit -ne $case.Exit) { $problems += "exit code $exit, expected $($case.Exit)" }
        if ($replaced -ne $case.Replaced) { $problems += "first.bin replaced: $replaced, expected $($case.Replaced)" }
        foreach ($line in $case.Log) {
            if (-not $text.Contains($line)) { $problems += "log lacks '$line'" }
        }
        if (-not $case.Replaced -and (Test-Path -LiteralPath (Join-Path $app 'second.txt'))) { $problems += 'second.txt was installed' }
        if ($problems.Count -eq 0) {
            Write-Host "PASS $($case.Name): exit code $exit, first.bin replaced: $replaced"
        }
        else {
            $failed++
            Write-Host "FAIL $($case.Name): $($problems -join '; ')"
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed -gt 0) {
    exit 1
}
