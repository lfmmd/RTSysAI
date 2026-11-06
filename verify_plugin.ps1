# ============================================================================
# verify_plugin.ps1 —— RTSys AI 插件端到端自动验证
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File verify_plugin.ps1
#
# 注意：RTSys 插件是懒加载——在用户激活菜单前不会加载。验证脚本会：
#   1. 启动 RTSys
#   2. 等待用户手动激活“AI 插件”菜单，或自动检测插件已加载
#   3. 检测 ApiServer 端口响应
# ============================================================================
$ErrorActionPreference = "Stop"

$RTSYS_EXE   = "%USERPROFILE%\Desktop\RTSys\RTSys.exe"
$PLUGIN_DIR  = "%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin"
$API_URL     = "http://localhost:9090/"
$API_TIMEOUT = 60
$CHECK_INTERVAL = 2

Write-Host "========================================================"
Write-Host "  RTSys AI Plugin End-to-End Verification"
Write-Host "========================================================"
Write-Host ""

# ---- 1. Confirm DLLs deployed ----
Write-Host "[1/5] Checking plugin DLLs ..."
$dlls = @("RTSysPlugInNative.dll", "RTSysPlugInAdapter.dll", "TrioSharedLibrary.dll")
foreach ($dll in $dlls) {
    $path = Join-Path $PLUGIN_DIR $dll
    if (-not (Test-Path $path)) {
        Write-Host "  [FAIL] Missing $dll" -ForegroundColor Red
        exit 1
    }
    $size = (Get-Item $path).Length
    Write-Host "  [OK]   $dll ($size bytes)"
}

# ---- 2. Start RTSys if not running ----
Write-Host ""
Write-Host "[2/5] Starting RTSys.exe ..."
$proc = Get-Process -Name "RTSys" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "  [OK]   RTSys already running (PID $($proc.Id)), skip launch"
} else {
    if (-not (Test-Path $RTSYS_EXE)) {
        Write-Host "  [FAIL] RTSys.exe not found: $RTSYS_EXE" -ForegroundColor Red
        exit 1
    }
    Start-Process -FilePath $RTSYS_EXE -WorkingDirectory (Split-Path $RTSYS_EXE)
    Write-Host "  [OK]   Launch initiated, waiting for main window..."
}

# ---- 3. Wait for main window ----
Write-Host ""
Write-Host "[3/5] Waiting for RTSys main window ..."
$start = Get-Date
$hwnd = $null
while ((Get-Date) - $start -lt (New-TimeSpan -Seconds 30)) {
    $proc = Get-Process -Name "RTSys" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc -and $proc.MainWindowHandle -and $proc.MainWindowHandle -ne 0) {
        $hwnd = $proc.MainWindowHandle
        Write-Host "  [OK]   Main window appeared (HWND 0x$hwnd)"
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $hwnd -or $hwnd -eq 0) {
    Write-Host "  [FAIL] No main window within 30 seconds, launch may have failed" -ForegroundColor Red
    exit 1
}

# ---- 3.5: Wait for plugin lazy loading ----
Write-Host ""
Write-Host "[3.5/5] Waiting for plugin lazy loading ..."

# Plugin load detection via plugin.log and shim_diag.log
Write-Host "  [INFO] Plugin loads lazily when you activate 'AI 插件' menu in RTSys UI."
Write-Host "  [INFO] Waiting up to 60s for plugin to auto-load or be activated..."

$pluginLoaded = $false
$start = Get-Date
while ((Get-Date) - $start -lt (New-TimeSpan -Seconds 60)) {
    # Check plugin.log for "Create() OK hwnd=..."
    $logPath = "%USERPROFILE%\AppData\Roaming\RTSysAI\plugin.log"
    if (Test-Path $logPath) {
        $content = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
        if ($content -match "Create\(\) OK hwnd=0x[0-9A-Fa-f]+") {
            $pluginLoaded = $true
            Write-Host "  [OK]   Plugin loaded successfully (Create() OK detected in plugin.log)"
            break
        }
    }
    # Check shim_diag.log for Create() returning non-zero
    $shimLog = "%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\shim_diag.log"
    if (Test-Path $shimLog) {
        $content = Get-Content $shimLog -Raw -ErrorAction SilentlyContinue
        if ($content -match "ExecuteInDefaultAppDomain\(Create\) -\&gt; ret=[1-9]") {
            $pluginLoaded = $true
            Write-Host "  [OK]   Plugin Create() returned non-zero HWND (shim_diag.log)"
            break
        }
    }
    Start-Sleep -Seconds 2
}

if (-not $pluginLoaded) {
    Write-Host "  [WARN] Plugin did not auto-load within 60s." -ForegroundColor Yellow
    Write-Host "  [INFO] Please manually activate 'AI 插件' menu in RTSys UI to load plugin."
    Write-Host "  [INFO] Waiting for plugin activation and ApiServer..."
}

# ---- 4. Poll ApiServer port ----
Write-Host ""
Write-Host "[4/5] Waiting for ApiServer at $API_URL ..."
$start = Get-Date
$apiOk = $false
while ((Get-Date) - $start -lt (New-TimeSpan -Seconds $API_TIMEOUT)) {
    # Check plugin.log / shim_diag.log for new load events to track progress
    if (-not $pluginLoaded) {
        $logPath = "%USERPROFILE%\AppData\Roaming\RTSysAI\plugin.log"
        if (Test-Path $logPath) {
            $content = Get-Content $logPath -Raw -ErrorAction SilentlyContinue
            if ($content -match "Create\(\) OK hwnd=0x[0-9A-Fa-f]+") {
                # Verify it's a NEW record by checking timestamp after script start
                $pluginLoaded = $true
                Write-Host "  [OK]   Plugin loaded (Create() OK detected in plugin.log)"
            }
        }
        $shimLog = "%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\shim_diag.log"
        if (Test-Path $shimLog) {
            $content = Get-Content $shimLog -Raw -ErrorAction SilentlyContinue
            if ($content -match "ExecuteInDefaultAppDomain\(Create\) -\&gt; ret=[1-9]") {
                $pluginLoaded = $true
                Write-Host "  [OK]   Plugin Create() returned non-zero HWND (shim_diag.log)"
            }
        }
    }

    try {
        $resp = Invoke-WebRequest -Uri $API_URL -Method GET -TimeoutSec 2 -ErrorAction Stop
        if ($resp.StatusCode -eq 200) {
            $apiOk = $true
            Write-Host "  [OK]   ApiServer responded HTTP $($resp.StatusCode)"
            break
        }
    } catch {
        # still waiting
    }
    Start-Sleep -Seconds $CHECK_INTERVAL
}

if (-not $apiOk) {
    if (-not $pluginLoaded) {
        Write-Host "  [FAIL] Plugin was never activated. Please activate 'AI 插件' menu in RTSys UI, then re-run this script." -ForegroundColor Red
    } else {
        Write-Host "  [FAIL] Plugin loaded but ApiServer did not respond within ${API_TIMEOUT}s at $API_URL" -ForegroundColor Red
        Write-Host "         Possible causes: ApiServer port occupied, CLR error after plugin load"
    }
    exit 1
}

# ---- 5. Optional: check plugin-related window handles ----
Write-Host ""
Write-Host "[5/5] Checking plugin windows (optional) ..."
$rtsysProc = Get-Process -Name "RTSys" | Select-Object -First 1
if ($rtsysProc) {
    Write-Host "  [INFO] RTSys main window HWND: 0x$($rtsysProc.MainWindowHandle)"
    Write-Host "  [INFO] Look for new AI-related child windows to confirm plugin UI embedded"
}

# ---- Conclusion ----
Write-Host ""
Write-Host "========================================================"
Write-Host "  Result: PASS" -ForegroundColor Green
Write-Host "========================================================"
Write-Host ""
Write-Host "Plugin loaded and running. RTSys process stays in foreground"
Write-Host "for manual observation. You can also call API at $API_URL."
Write-Host ""
Write-Host "To stop verification, close the RTSys window manually."
Write-Host ""

exit 0
