@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x86 -host_arch=x86
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "%USERPROFILE%\Desktop\works\motion perfect\TrioAI_RTSys\PlugInNativeShim\PlugInNativeShim.vcxproj" /p:Configuration=Release /p:Platform=Win32 /nologo /v:minimal
exit /b %ERRORLEVEL%