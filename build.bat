@echo off
:: Compile OptiTech (aucun SDK requis)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
pause
