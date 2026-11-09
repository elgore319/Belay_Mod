@echo off
title Uninstall Belay
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" -Uninstall %*
echo.
pause
