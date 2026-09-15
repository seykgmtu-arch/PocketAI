@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\download-sd15-training-model.ps1" -PocketAIRoot "%~dp0"
echo.
pause
