@echo off
setlocal
set CL="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231\bin\Hostx86\x86\cl.exe"
set INCL="C:\Program Files (x86)\Windows Kits\10\include\10.0.19041.0\um"
set LIB="C:\Program Files (x86)\Windows Kits\10\lib\10.0.19041.0\um\x86"

echo "=== compile ==="
%CL% /nologo /EHsc /I %INCL% HostSim.c /link user32.lib kernel32.lib gdi32.lib /OUT:HostSim.exe
if %ERRORLEVEL% neq 0 (
    echo ERROR: compile failed
    exit /b %ERRORLEVEL%
)
echo OK compiled.

echo "=== run ==="
HostSim.exe "%USERPROFILE%\Desktop\RTSys\Plug_in\AIPlugin\RTSysPlugInNative.dll"
