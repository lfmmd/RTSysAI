@echo off
setlocal enabledelayedexpansion

call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x86 || (
    echo ERROR: VsDevCmd failed
    exit /b 1
)

set ROOT=%USERPROFILE%\Desktop\works\motion perfect\TrioAI_RTSys
set SHIM=%ROOT%\PlugInNativeShim
set REF_DIR=C:\refs
set ADAPTER=%REF_DIR%\RTSysPlugInAdapter.dll

cl.exe /c /EHa /nologo /W3 /LD /clr /std:c++17 /D "_USRDLL" /D "WINDOWS" /D "UNICODE" /D "_UNICODE" ^
  /I "%SHIM%" ^
  /Fo"%OUT%\\" ^
  "%SHIM%\PlugInShim.cpp" ^
  "%SHIM%\pch.cpp" ^
  /link /OUT:"%OUT%\PlugInNativeShim.dll" /DLL ^
  /DEF:"%SHIM%\PlugInShim.def" ^
  mscoree.lib

if %ERRORLEVEL%==0 (
    echo OK: %OUT%\PlugInNativeShim.dll
) else (
    echo ERROR: build failed
    exit /b %ERRORLEVEL%
)
