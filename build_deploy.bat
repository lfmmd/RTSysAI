@echo off
REM ============================================================================
REM build_deploy.bat - build and deploy the RTSys plugin
REM
REM Usage:
REM   build_deploy.bat              Build only
REM   build_deploy.bat deploy       Build and deploy
REM ============================================================================
setlocal

set "PROJECT_ROOT=%~dp0"
set "ADAPTER_DIR=%PROJECT_ROOT%PlugInAdapter"
set "SHIM_DIR=%PROJECT_ROOT%PlugInNativeShim"
set "WORKER_DIR=%PROJECT_ROOT%PlugInWorker"

REM RTSys install directory; override by setting RTSYS_DIR before running.
if not defined RTSYS_DIR set "RTSYS_DIR=%USERPROFILE%\Desktop\RTSys"
set "DEPLOY_MODULE=AIPlugin"
set "DEPLOY_PATH=%RTSYS_DIR%\Plug_in\%DEPLOY_MODULE%"

echo ============================================================
echo  RTSys plugin build
echo ============================================================
echo.

REM ---------------------------------------------------------------------------
REM 1) Locate Visual Studio developer tools.
REM ---------------------------------------------------------------------------
echo [1/5] Locating Visual Studio...

set "VSWHERE="
set "VSWHERE_EXE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE_EXE%" set "VSWHERE_EXE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE_EXE%" (
    for /f "tokens=*" %%I in ('"%VSWHERE_EXE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath') do set "VSWHERE=%%I"
)

if not defined VSWHERE (
    echo [ERROR] Visual Studio with the C++ x86/x64 tools was not found.
    exit /b 1
)
echo       VS: %VSWHERE%

call "%VSWHERE%\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x86 >nul
if errorlevel 1 (
    echo [ERROR] VsDevCmd initialization failed.
    exit /b 1
)

REM ---------------------------------------------------------------------------
REM 2) Build the managed adapter.
REM ---------------------------------------------------------------------------
echo [2/5] Building PlugInAdapter...

pushd "%ADAPTER_DIR%" || exit /b 1
msbuild "PlugInAdapter.csproj" /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
set "CSBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%CSBUILD_RESULT%"=="0" (
    echo [ERROR] PlugInAdapter build failed.
    exit /b %CSBUILD_RESULT%
)
echo       OK: PlugInAdapter\bin\Release\RTSysPlugInAdapter.dll

REM ---------------------------------------------------------------------------
REM 3) Build the native shim with cl.exe.
REM ---------------------------------------------------------------------------
echo [3/5] Building native shim...

pushd "%SHIM_DIR%" || exit /b 1
if not exist "Release" mkdir "Release"

REM Build PlugInShimNative.cpp and its module definition with cl.exe.
cl.exe /nologo /utf-8 /W3 /EHsc /LD PlugInShimNative.cpp ^
  /link /DLL /OUT:"Release\RTSysPlugInNative.dll" ^
  /DEF:PlugInShimNative.def mscoree.lib ole32.lib

set "SHIMBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%SHIMBUILD_RESULT%"=="0" (
    echo [ERROR] Native shim build failed.
    exit /b %SHIMBUILD_RESULT%
)
echo       OK: PlugInNativeShim\Release\RTSysPlugInNative.dll

REM ---------------------------------------------------------------------------
REM 4) Build the worker process.
REM ---------------------------------------------------------------------------
echo [4/5] Building PlugInWorker...

pushd "%WORKER_DIR%" || exit /b 1
msbuild "PlugInWorker.csproj" /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
set "WORKERBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%WORKERBUILD_RESULT%"=="0" (
    echo [ERROR] PlugInWorker build failed.
    exit /b %WORKERBUILD_RESULT%
)
echo       OK: PlugInWorker\bin\Release\RTSysPlugInWorker.exe

REM ---------------------------------------------------------------------------
REM 5) Optional deployment
REM ---------------------------------------------------------------------------
if /i "%~1"=="deploy" (
    echo [5/5] Deploying to %DEPLOY_PATH% ...
    if not exist "%RTSYS_DIR%\RTSys.exe" goto :rtsys_missing

    REM Force-close RTSys before copying plugin files; unsaved projects may be lost.
    tasklist /FI "IMAGENAME eq RTSys.exe" 2>nul | find /I "RTSys.exe" >nul
    if not errorlevel 1 (
        echo       RTSys is running; force-closing it before deployment...
        taskkill /F /IM RTSys.exe >nul 2>nul
        if errorlevel 1 goto :rtsys_close_error
        timeout /T 2 /NOBREAK >nul
        tasklist /FI "IMAGENAME eq RTSys.exe" 2>nul | find /I "RTSys.exe" >nul
        if not errorlevel 1 goto :rtsys_close_error
    )

    if not exist "%DEPLOY_PATH%" (
        mkdir "%DEPLOY_PATH%" || goto :deploy_dir_error
    )

    echo       Copying files...
    copy /Y "%SHIM_DIR%\Release\RTSysPlugInNative.dll" "%DEPLOY_PATH%\" >nul
    if errorlevel 1 goto :deploy_error
    copy /Y "%ADAPTER_DIR%\bin\Release\RTSysPlugInAdapter.dll" "%DEPLOY_PATH%\" >nul
    if errorlevel 1 goto :deploy_error
    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.exe" "%DEPLOY_PATH%\" >nul
    if errorlevel 1 goto :deploy_error
    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.exe.config" "%DEPLOY_PATH%\" >nul
    if errorlevel 1 goto :deploy_error
    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.pdb" "%DEPLOY_PATH%\" >nul 2>nul

    REM Copy skill data.
    if exist "%WORKER_DIR%\skills" (
        if not exist "%DEPLOY_PATH%\skills" mkdir "%DEPLOY_PATH%\skills"
        xcopy /E /I /Y "%WORKER_DIR%\skills\*" "%DEPLOY_PATH%\skills\" >nul
        if errorlevel 1 goto :deploy_error
    )

    REM Copy external dependencies from the repository's parent directory.
    for %%F in (
        "TrioSharedLibrary.dll"
        "TrioCommunicationsLibrary.dll"
        "TrioBaseLib.dll"
        "Newtonsoft.Json.dll"
        "TrioCustomControlsLib.dll"
    ) do (
        if exist "%PROJECT_ROOT%..\%%~F" (
            copy /Y "%PROJECT_ROOT%..\%%~F" "%DEPLOY_PATH%\" >nul
            if errorlevel 1 goto :deploy_error
        )
    )

    echo       Deployed to: %DEPLOY_PATH%
    echo.
    echo       Restart RTSys to load the updated plugin.
) else (
    echo [5/5] Build only. Run build_deploy.bat deploy to deploy.
)

echo.
echo ============================================================
echo  Build complete
echo ============================================================
exit /b 0

:deploy_error
echo [ERROR] Deployment copy failed. Check destination permissions and file locks.
exit /b 1

:deploy_dir_error
echo [ERROR] Could not create deployment directory: %DEPLOY_PATH%
exit /b 1

:rtsys_missing
echo [ERROR] RTSys.exe was not found under %RTSYS_DIR%. Set RTSYS_DIR to the install directory.
exit /b 1

:rtsys_running
echo [ERROR] RTSys.exe is running. Save your project, close RTSys, then retry deployment.
exit /b 1

:rtsys_close_error
echo [ERROR] Could not force-close RTSys.exe. Deployment was stopped.
exit /b 1
