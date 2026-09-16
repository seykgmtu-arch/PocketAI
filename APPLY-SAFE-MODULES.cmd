@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0PATCH-MAINWINDOW-XAML.ps1" -RepoRoot "%~dp0"
echo.
echo XAML patch finished.
echo Copy the src folder from this package into the repository, then build.
echo.
pause
