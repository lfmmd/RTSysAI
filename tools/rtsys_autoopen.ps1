<#
RTSys AI plugin auto-open helper (automation only).

Starts RTSys (or attaches to a running instance), waits for the main frame,
then posts WM_COMMAND for the configured plugin entry so the AI panel opens
without manual clicking. Does NOT register any startup item and does NOT keep
a daemon running.

Usage:
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\rtsys_autoopen.ps1
  powershell ... -Attach                      # attach to running RTSys only
  powershell ... -Project "D:\demo\TestPro.zpj"
  powershell ... -Plugin AIPlugin -Verify
  powershell ... -Plugin 0 -NoVerify

Command id rule (confirmed from RTSys.exe sub_50AA70):
  WM_COMMAND id = 37000 + plugin index, where index is Plugin%d in
  HKCU\Software\RTSys Application 1.0\RTSys\Plugin.
#>
param(
    [string]$Exe = (Join-Path $env:USERPROFILE 'Desktop\RTSys\RTSys.exe'),
    [string]$Project = '',
    [string]$Plugin = 'AIPlugin',
    [switch]$Attach,
    [int]$WaitSeconds = 90,
    [int]$SettleMs = 2500,
    [switch]$NoVerify,
    [int]$VerifyTimeoutSec = 20
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class RtsysWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
"@

$script:ProcName = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
$script:PluginKey = 'HKCU:\Software\RTSys Application 1.0\RTSys\Plugin'

function Get-PluginMeta([string]$plugin) {
    $meta = [pscustomobject]@{ Index = $null; CommandId = $null; Title = '' }
    if ($plugin -match '^\d+$') {
        $meta.Index = [int]$plugin
        $meta.CommandId = 37000 + $meta.Index
    }
    if (-not (Test-Path $script:PluginKey)) {
        if ($meta.CommandId -eq $null) { $meta.CommandId = 37000 }
        return $meta
    }
    $props = Get-ItemProperty $script:PluginKey
    $count = 0
    if ($props.PSObject.Properties.Name -contains 'Count') { $count = [int]$props.Count }
    for ($i = 0; $i -lt $count; $i++) {
        $dll = $props.("Plugin$i")
        $dir = $props.("Dir$i")
        $match = $false
        if ($plugin -match '^\d+$') { $match = ([int]$plugin -eq $i) }
        else {
            if ($dll -and $dll -like '*RTSysPlugInNative*') { $match = $true }
            if ($dir -and $dir -ieq $plugin) { $match = $true }
        }
        if ($match) {
            $meta.Index = $i
            $meta.CommandId = 37000 + $i
            $meta.Title = [string]$props.("Menu name$i")
            break
        }
    }
    if ($meta.CommandId -eq $null) { $meta.CommandId = 37000 }
    return $meta
}

function Find-RtsysProcess {
    $p = Get-Process -Name $script:ProcName -ErrorAction SilentlyContinue |
         Where-Object { $_.MainWindowHandle -ne 0 } |
         Select-Object -First 1
    return $p
}

function Wait-RtsysProcess([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $p = Find-RtsysProcess
        if ($p) { return $p }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

function Find-ProcessWindowByTitle([int]$procId, [string]$title) {
    $script:found = $false
    if ([string]::IsNullOrEmpty($title)) { return $false }
    $cb = [RtsysWin+EnumProc]{
        param($h, $l)
        $wpid = 0
        [void][RtsysWin]::GetWindowThreadProcessId($h, [ref]$wpid)
        if ($wpid -eq $procId) {
            $len = [RtsysWin]::GetWindowTextLength($h)
            if ($len -gt 0) {
                $sb = New-Object System.Text.StringBuilder ($len + 1)
                [void][RtsysWin]::GetWindowText($h, $sb, $sb.Capacity)
                if ($sb.ToString() -eq $title) { $script:found = $true }
            }
            [void][RtsysWin]::EnumChildWindows($h, [RtsysWin+EnumProc]{
                param($ch, $cl)
                $len2 = [RtsysWin]::GetWindowTextLength($ch)
                if ($len2 -gt 0) {
                    $sb2 = New-Object System.Text.StringBuilder ($len2 + 1)
                    [void][RtsysWin]::GetWindowText($ch, $sb2, $sb2.Capacity)
                    if ($sb2.ToString() -eq $title) { $script:found = $true }
                }
                return $true
            }, [IntPtr]::Zero)
        }
        return $true
    }
    [void][RtsysWin]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:found
}

# 1) Locate or start RTSys.
$proc = Find-RtsysProcess
if (-not $proc) {
    if ($Attach) {
        Write-Error "No running $($script:ProcName) main window found (-Attach)."
        exit 1
    }
    if (-not (Test-Path $Exe)) {
        Write-Error "RTSys executable not found: $Exe"
        exit 1
    }
    if ($Project) {
        Write-Host "[open] starting $Exe with project $Project"
        Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Project) | Out-Null
    } else {
        Write-Host "[open] starting $Exe"
        Start-Process -FilePath $Exe | Out-Null
    }
    $proc = Wait-RtsysProcess $WaitSeconds
}
if (-not $proc) {
    Write-Error "RTSys main window did not appear within $WaitSeconds s."
    exit 1
}

$hwnd = $proc.MainWindowHandle
Write-Host "[open] pid=$($proc.Id) hwnd=0x$($hwnd.ToString('X')) title=$($proc.MainWindowTitle)"

# 2) Let the frame finish initialization (ribbon/message map).
if ($SettleMs -gt 0) { Start-Sleep -Milliseconds $SettleMs }

$meta = Get-PluginMeta $Plugin
Write-Host "[open] plugin='$Plugin' index=$($meta.Index) commandId=$($meta.CommandId)"

# 3) Post WM_COMMAND(37000 + index).
$ok = [RtsysWin]::PostMessage($hwnd, 0x111, [IntPtr]$meta.CommandId, [IntPtr]::Zero)
if (-not $ok) {
    Write-Error "PostMessage(WM_COMMAND,$($meta.CommandId)) failed."
    exit 2
}
Write-Host "[open] WM_COMMAND $($meta.CommandId) posted"

# 4) Optional verification: look for the plugin caption window (docked or floating).
if (-not $NoVerify) {
    $deadline = (Get-Date).AddSeconds($VerifyTimeoutSec)
    $seen = $false
    while ((Get-Date) -lt $deadline) {
        if (Find-ProcessWindowByTitle $proc.Id $meta.Title) { $seen = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if ($seen) {
        Write-Host "[open] verified: window '$($meta.Title)' present"
    } else {
        Write-Warning "WM_COMMAND posted, but window '$($meta.Title)' was not found within $VerifyTimeoutSec s."
        exit 2
    }
}

exit 0
