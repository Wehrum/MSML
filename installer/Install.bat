@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
set "code=%ERRORLEVEL%"
echo.
pause
exit /b %code%
