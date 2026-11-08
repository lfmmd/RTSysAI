@echo off
REM Automation entry: start/attach RTSys and open the AI plugin panel.
REM Extra args are passed through to rtsys_autoopen.ps1:
REM   rtsys_ai.cmd -Attach
REM   rtsys_ai.cmd -Project "D:\demo\TestPro.zpj"
REM   rtsys_ai.cmd -Plugin AIPlugin -NoVerify
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0rtsys_autoopen.ps1" %*
exit /b %ERRORLEVEL%
