@echo off
REM Launched from MSI Finish checkbox (WixShellExec). %~dp0 is install root.
powershell.exe -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-OpenWorkmate.ps1"
exit /b 0
