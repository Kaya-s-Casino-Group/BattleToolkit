@echo off
setlocal
chcp 65001 >nul
:launch
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\bootstrap.ps1" %*
set "launch_status=%ERRORLEVEL%"
if "%launch_status%"=="0" exit /b 0
if not "%~1"=="" exit /b %launch_status%
echo.
echo Launch failed. See the message above. Exit code: %launch_status%
choice /C RQ /N /M "[R] Retry / [Q] Quit: "
if errorlevel 2 exit /b %launch_status%
goto launch
