@echo off
REM ============================================================================
REM build_shim.bat —— 仅编译纯原生 shim (RTSysPlugInNative.dll)
REM
REM 源码: PlugInNativeShim\PlugInShimNative.cpp + PlugInShimNative.def
REM 输出: PlugInNativeShim\Release\RTSysPlugInNative.dll
REM
REM 注意: 源码路径含空格时 cl.exe 需引号；必须加 /utf-8，
REM       否则中文注释按 GBK 解码会破坏后续代码。
REM ============================================================================
setlocal enabledelayedexpansion

call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x86 || (
    echo ERROR: VsDevCmd failed
    exit /b 1
)

set "SHIM=%~dp0PlugInNativeShim"

pushd "%SHIM%"
if not exist "Release" mkdir "Release"

cl.exe /c /nologo /utf-8 /W3 /EHsc /LD PlugInShimNative.cpp ^
  /link /DLL /OUT:"Release\RTSysPlugInNative.dll" ^
  /DEF:PlugInShimNative.def mscoree.lib ole32.lib

set "RESULT=%ERRORLEVEL%"
popd

if %RESULT%==0 (
    echo OK: %SHIM%\Release\RTSysPlugInNative.dll
) else (
    echo ERROR: build failed
    exit /b %RESULT%
)
