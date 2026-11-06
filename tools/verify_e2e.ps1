#requires -Version 5.1
<#
.SYNOPSIS
    Start RTSys, wait for the user to activate the AI plugin, then run api_probe.py.

.DESCRIPTION
    The plugin is lazy-loaded: the user must click the "AI 插件" menu in RTSys.
    This script NEVER clicks the menu; it only waits and polls.

    Steps:
      1. Stop any running RTSys.exe
      2. Start RTSys.exe and wait for its main window
      3. Prompt the user to activate the plugin
      4. Poll up to ActivationTimeoutSeconds for plugin.log "Create() OK hwnd="
         or a listening API port (9090-9093)
      5. Run tools\api_probe.py and tee output to tools\verify_e2e.log
#>
[CmdletBinding()]
param(
    [string]$RTSysPath = '%USERPROFILE%\Desktop\RTSys\RTSys.exe',
    [string]$PluginLog = (Join-Path $env:APPDATA 'RTSysAI\plugin.log'),
    [string]$ShimLog = '%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\shim_diag.log',
    [int]$ActivationTimeoutSeconds = 120,
    [string]$HostName = 'localhost'
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogPath = Join-Path $ScriptDir 'verify_e2e.log'
$ProbePath = Join-Path $ScriptDir 'api_probe.py'
$Ports = @(9090, 9091, 9092, 9093)

function Write-Log {
    param([string]$Message)
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Test-ApiPort {
    param([string]$Name, [int]$Port)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($Name, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne(300)) { return $false }
        $client.EndConnect($iar)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Find-ApiPort {
    foreach ($port in $Ports) {
        if (Test-ApiPort -Name $HostName -Port $port) { return $port }
    }
    return $null
}

function Get-NewLogText {
    param([string]$Path, [long]$Offset)
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $stream = $null
    $reader = $null
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
        if ($Offset -gt $stream.Length) { $Offset = 0 }  # log was rotated/truncated
        if ($Offset -ge $stream.Length) { return '' }
        $stream.Seek($Offset, 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    }
    catch { return '' }
    finally {
        if ($reader) { $reader.Close() }
        if ($stream) { $stream.Close() }
    }
}

function Write-LogTail {
    param([string]$Path, [int]$Count = 20)
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Log "  (log not found: $Path)"
        return
    }
    Write-Log "--- last $Count lines of $Path ---"
    Get-Content -LiteralPath $Path -Tail $Count -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Log "  $_" }
}

Write-Log '=== verify_e2e start ==='
Write-Log "RTSys: $RTSysPath"
Write-Log "Plugin log: $PluginLog"
Write-Log "Shim log: $ShimLog"

if (-not (Test-Path -LiteralPath $RTSysPath)) {
    Write-Log "ERROR: RTSys.exe not found: $RTSysPath"
    exit 3
}

$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) {
    Write-Log 'ERROR: python not found in PATH'
    exit 3
}
if (-not (Test-Path -LiteralPath $ProbePath)) {
    Write-Log "ERROR: probe script not found: $ProbePath"
    exit 3
}

# 1. Stop existing RTSys
$existing = Get-Process -Name 'RTSys' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Log "Stopping $($existing.Count) running RTSys process(es)..."
    $existing | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-Process -Name 'RTSys' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Name 'RTSys' -ErrorAction SilentlyContinue) {
        Write-Log 'WARNING: RTSys still running after 15s'
    } else {
        Write-Log 'RTSys stopped.'
    }
} else {
    Write-Log 'No running RTSys process.'
}

# Capture plugin.log length before launch so old "Create() OK" lines are ignored.
$logOffset = 0L
if (Test-Path -LiteralPath $PluginLog) {
    $logOffset = (Get-Item -LiteralPath $PluginLog).Length
}

# 2. Start RTSys
Write-Log 'Starting RTSys...'
$rtsysDir = Split-Path -Parent $RTSysPath
$proc = Start-Process -FilePath $RTSysPath -WorkingDirectory $rtsysDir -PassThru
Write-Log "RTSys started, PID=$($proc.Id)"

$windowDeadline = (Get-Date).AddSeconds(60)
$windowReady = $false
while ((Get-Date) -lt $windowDeadline) {
    $proc.Refresh()
    if ($proc.HasExited) {
        Write-Log 'ERROR: RTSys exited before the main window appeared.'
        exit 3
    }
    if ($proc.MainWindowHandle -ne 0) { $windowReady = $true; break }
    Start-Sleep -Milliseconds 500
}
if ($windowReady) {
    Write-Log "RTSys main window ready (hwnd=$($proc.MainWindowHandle))."
} else {
    Write-Log 'WARNING: main window not detected within 60s; continuing anyway.'
}

# 3. Ask the user to activate the plugin (never auto-click)
Write-Host ''
Write-Host '============================================================' -ForegroundColor Yellow
Write-Host ' 请在 RTSys 中点击「AI 插件」菜单激活插件' -ForegroundColor Yellow
Write-Host ' (脚本不会自动点击菜单；激活后会继续执行 API 探测)' -ForegroundColor Yellow
Write-Host '============================================================' -ForegroundColor Yellow
Write-Host ''
Write-Log 'Waiting for user to click the AI plugin menu...'

# 4. Poll for activation
$deadline = (Get-Date).AddSeconds($ActivationTimeoutSeconds)
$detectedPort = $null
$createSeen = $false
$announcedPort = $false
$announcedCreate = $false

while ((Get-Date) -lt $deadline) {
    $newText = Get-NewLogText -Path $PluginLog -Offset $logOffset
    if ($newText -match 'Create\(\) OK hwnd=') {
        $createSeen = $true
        if (-not $announcedCreate) {
            Write-Log 'Detected plugin.log: Create() OK hwnd=... (plugin loaded).'
            $announcedCreate = $true
        }
    }
    $detectedPort = Find-ApiPort
    if ($detectedPort) {
        if (-not $announcedPort) {
            Write-Log "API port is listening: $detectedPort"
            $announcedPort = $true
        }
        break
    }
    Start-Sleep -Milliseconds 1000
}

if (-not $detectedPort) {
    Write-Log "ERROR: no API port listening after $ActivationTimeoutSeconds seconds."
    if ($createSeen) {
        Write-Log 'Plugin Create() was seen, but ApiServer did not open a port.'
    } else {
        Write-Log 'Plugin Create() was NOT seen - the menu was likely not clicked.'
    }
    Write-LogTail -Path $PluginLog -Count 20
    Write-LogTail -Path $ShimLog -Count 20
    Write-Log '=== verify_e2e end (FAIL: plugin not activated) ==='
    exit 1
}

# 5. Run the probe
Write-Log "Running API probe: python `"$ProbePath`" --host $HostName"
& $python.Source $ProbePath --host $HostName 2>&1 | ForEach-Object {
    Write-Host $_
    Add-Content -LiteralPath $LogPath -Value $_ -Encoding UTF8
}
$probeExit = $LASTEXITCODE
Write-Log "api_probe.py exited with code $probeExit (0=clean, 1=ERROR findings, 2=unreachable)."
Write-Log '=== verify_e2e end ==='
exit $probeExit
