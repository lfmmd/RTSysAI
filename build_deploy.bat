@echo off
REM ============================================================================
REM build_deploy.bat —— 一键编译 + 部署 RTSys 插件
REM
REM 用法:
REM   build_deploy.bat              仅编译
REM   build_deploy.bat deploy       编译并部署到 RTSys 安装目录
REM ============================================================================
setlocal enabledelayedexpansion

set "PROJECT_ROOT=%~dp0"
set "ADAPTER_DIR=%PROJECT_ROOT%PlugInAdapter"
set "SHIM_DIR=%PROJECT_ROOT%PlugInNativeShim"
set "WORKER_DIR=%PROJECT_ROOT%PlugInWorker"

REM RTSys 安装目录（按需修改）
if not defined RTSYS_DIR set "RTSYS_DIR=%USERPROFILE%\Desktop\RTSys"
set "DEPLOY_MODULE=AIPlugin"
set "DEPLOY_PATH=%RTSYS_DIR%\Plug_in\%DEPLOY_MODULE%"

echo ============================================================
echo  RTSys 插件构建
echo ============================================================
echo.

REM ---------------------------------------------------------------------------
REM 1) 定位 VS 开发环境（提供 msbuild / cl / 引用程序集）
REM ---------------------------------------------------------------------------
echo [1/5] 定位 Visual Studio 开发环境...

set "VSWHERE="
for %%V in (
    "%ProgramFiles%\Microsoft Visual Studio\18\Community"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Community"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Professional"
    "%ProgramFiles%\Microsoft Visual Studio\2022\Enterprise"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Enterprise"
) do (
    if exist "%%V\Common7\Tools\VsDevCmd.bat" if not defined VSWHERE set "VSWHERE=%%V"
)

if not defined VSWHERE (
    echo [错误] 未找到 Visual Studio 安装，请检查
    exit /b 1
)
echo       VS: %VSWHERE%

call "%VSWHERE%\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x86 >nul
if errorlevel 1 (
    echo [错误] VsDevCmd 初始化失败
    exit /b 1
)

REM ---------------------------------------------------------------------------
REM 2) 编译 C# 托管库
REM ---------------------------------------------------------------------------
echo [2/5] 编译 C# 托管库 (PlugInAdapter)...

pushd "%ADAPTER_DIR%"
msbuild "PlugInAdapter.csproj" /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
set "CSBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%CSBUILD_RESULT%"=="0" (
    echo [错误] C# 编译失败
    exit /b %CSBUILD_RESULT%
)
echo       OK: PlugInAdapter\bin\Release\RTSysPlugInAdapter.dll

REM ---------------------------------------------------------------------------
REM 3) 编译纯原生 shim (cl.exe，不依赖 C++/CLI 工作负载)
REM ---------------------------------------------------------------------------
echo [3/5] 编译纯原生 shim (PlugInShimNative)...

pushd "%SHIM_DIR%"
if not exist "Release" mkdir "Release"

REM 源码: PlugInShimNative.cpp + PlugInShimNative.def（无 .vcxproj，直接用 cl.exe）
cl.exe /c /nologo /utf-8 /W3 /EHsc /LD PlugInShimNative.cpp ^
  /link /DLL /OUT:"Release\RTSysPlugInNative.dll" ^
  /DEF:PlugInShimNative.def mscoree.lib ole32.lib

set "SHIMBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%SHIMBUILD_RESULT%"=="0" (
    echo [错误] 原生 shim 编译失败
    exit /b %SHIMBUILD_RESULT%
)
echo       OK: PlugInNativeShim\Release\RTSysPlugInNative.dll

REM ---------------------------------------------------------------------------
REM 4) 编译 C# worker 进程
REM ---------------------------------------------------------------------------
echo [4/5] 编译 C# worker 进程 (PlugInWorker)...

pushd "%WORKER_DIR%"
msbuild "PlugInWorker.csproj" /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
set "WORKERBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%WORKERBUILD_RESULT%"=="0" (
    echo [错误] Worker 编译失败
    exit /b %WORKERBUILD_RESULT%
)
echo       OK: PlugInWorker\bin\Release\RTSysPlugInWorker.exe

REM ---------------------------------------------------------------------------
REM 5) 部署（可选）
REM ---------------------------------------------------------------------------
if /i "%~1"=="deploy" (
    echo [5/5] 部署到 %DEPLOY_PATH% ...
    if not exist "%DEPLOY_PATH%" mkdir "%DEPLOY_PATH%"

    REM 停止 RTSys 以便覆盖文件
    echo       停止 RTSys.exe (如正在运行)...
    tasklist /FI "IMAGENAME eq RTSys.exe" 2>nul | find "RTSys.exe" >nul && (
        taskkill /F /IM RTSys.exe >nul 2>&1
        timeout /t 2 /nobreak >nul
    )

    echo       复制文件...
    copy /Y "%SHIM_DIR%\Release\RTSysPlugInNative.dll" "%DEPLOY_PATH%\" >nul
    copy /Y "%ADAPTER_DIR%\bin\Release\RTSysPlugInAdapter.dll" "%DEPLOY_PATH%\" >nul

    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.exe" "%DEPLOY_PATH%\" >nul
    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.exe.config" "%DEPLOY_PATH%\" >nul
    copy /Y "%WORKER_DIR%\bin\Release\RTSysPlugInWorker.pdb" "%DEPLOY_PATH%\" >nul 2>nul

    REM 技能数据
    if exist "%WORKER_DIR%\skills" (
        if not exist "%DEPLOY_PATH%\skills" mkdir "%DEPLOY_PATH%\skills"
        xcopy /E /I /Y "%WORKER_DIR%\skills\*" "%DEPLOY_PATH%\skills\" >nul
    )

    REM 依赖的外部程序集（按需，从项目根目录拷贝）
    for %%F in (
        "TrioSharedLibrary.dll"
        "TrioCommunicationsLibrary.dll"
        "TrioBaseLib.dll"
        "Newtonsoft.Json.dll"
        "TrioCustomControlsLib.dll"
    ) do (
        if exist "%%F" copy /Y "%%F" "%DEPLOY_PATH%\" >nul
    )

    echo       已部署到: %DEPLOY_PATH%
    echo.
    echo       提示: 请重启 RTSys 以加载新插件。
) else (
    echo [5/5] 跳过部署（使用 build_deploy.bat deploy 执行部署）
)

echo.
echo ============================================================
echo  构建完成
echo ============================================================
exit /b 0