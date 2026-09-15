@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\verify-image-training.ps1" -PocketAIRoot "%~dp0"
echo.
pause
