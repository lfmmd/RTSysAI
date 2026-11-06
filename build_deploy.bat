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

REM RTSys 安装目录（按需修改）
set "RTSYS_DIR=%USERPROFILE%\Desktop\RTSys"
set "DEPLOY_MODULE=TrioAI"
set "DEPLOY_PATH=%RTSYS_DIR%\Plug_in\%DEPLOY_MODULE%"

echo ============================================================
echo  RTSys 插件构建
echo ============================================================
echo.

REM ---------------------------------------------------------------------------
REM 1) 定位 VS 开发环境（提供 msbuild / cl / 引用程序集）
REM ---------------------------------------------------------------------------
echo [1/4] 定位 Visual Studio 开发环境...

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
echo [2/4] 编译 C# 托管库 (PlugInAdapter)...

pushd "%ADAPTER_DIR%"
msbuild "PlugInAdapter.csproj" /p:Configuration=Release /p:Platform=x86 /nologo /v:minimal
set "CSBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%CSBUILD_RESULT%"=="0" (
    echo [错误] C# 编译失败
    exit /b %CSBUILD_RESULT%
)
echo       OK: PlugInAdapter\bin\Release\PlugInAdapter.dll

REM ---------------------------------------------------------------------------
REM 3) 编译 C++/CLI 原生 shim
REM ---------------------------------------------------------------------------
echo [3/4] 编译 C++/CLI 原生 shim (PlugInNativeShim)...

pushd "%SHIM_DIR%"
msbuild "PlugInNativeShim.vcxproj" /p:Configuration=Release /p:Platform=Win32 /nologo /v:minimal
set "SHIMBUILD_RESULT=%ERRORLEVEL%"
popd

if not "%SHIMBUILD_RESULT%"=="0" (
    echo [错误] C++/CLI 编译失败
    exit /b %SHIMBUILD_RESULT%
)
echo       OK: PlugInNativeShim\Release\PlugInNativeShim.dll

REM ---------------------------------------------------------------------------
REM 4) 部署（可选）
REM ---------------------------------------------------------------------------
if /i "%~1"=="deploy" (
    echo [4/4] 部署到 %DEPLOY_PATH% ...
    if not exist "%DEPLOY_PATH%" mkdir "%DEPLOY_PATH%"

    copy /Y "%SHIM_DIR%\Release\PlugInNativeShim.dll" "%DEPLOY_PATH%\" >nul
    copy /Y "%ADAPTER_DIR%\bin\Release\PlugInAdapter.dll" "%DEPLOY_PATH%\" >nul

    REM 依赖的外部程序集（若存在）
    for %%F in (
        "TrioSharedLibrary.dll"
        "Newtonsoft.Json.dll"
    ) do (
        if exist "%%F" copy /Y "%%F" "%DEPLOY_PATH%\" >nul
    )

    echo       已部署到: %DEPLOY_PATH%
    echo.
    echo       提示: 若 RTSys 正在运行，请先关闭再启动以加载新插件。
) else (
    echo [4/4] 跳过部署（使用 build_deploy.bat deploy 执行部署）
)

echo.
echo ============================================================
echo  构建完成
echo ============================================================
exit /b 0
