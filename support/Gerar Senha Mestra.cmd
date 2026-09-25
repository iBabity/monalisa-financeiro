@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GerarSenhaMestra.ps1"
echo.
pause
