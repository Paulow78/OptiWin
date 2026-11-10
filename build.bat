@echo off
:: Compile OptiWin (aucun SDK requis)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause
