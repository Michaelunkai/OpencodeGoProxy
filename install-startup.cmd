@echo off
:: OpenCode Go Proxy — one-click setup for a fresh Windows install
:: Run this ONCE after copying/cloning the folder to a new Windows machine.
::  - Imports your API keys if a backup exists (or creates a template)
::  - Registers a hidden auto-start task (no terminal pop-ups, every boot)
::  - Starts the proxy immediately
setlocal
set "DIR=%~dp0"
set "KEYS=%DIR%api.txt"
set "BACKUP=F:\backup\windowsapps\credentials\opencodego\api.txt"

::- Import keys if the local file has none
set "HASKEYS="
if exist "%KEYS%" for /f "usebackq tokens=*" %%L in ("%KEYS%") do (
    echo %%L | findstr /b "oc_sk_" >nul && set "HASKEYS=1"
)
if not defined HASKEYS (
    if exist "%BACKUP%" (
        copy /y "%BACKUP%" "%KEYS%" >nul
        echo Imported keys from %BACKUP%
    ) else (
        echo # Add your OpenCode API keys here, one per line> "%KEYS%"
        echo # Get keys from: https://opencode.ai/settings/keys>> "%KEYS%"
        echo Created api.txt template - add your keys, then re-run this file.
    )
)

::- Register hidden auto-start (needs admin)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights to register auto-start...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
schtasks /Create /F /TN "OpenCodeGoProxy" /SC ONLOGON /RL HIGHEST /TR "wscript.exe \"%DIR%run-hidden.vbs\"" >nul 2>&1

::- Start now, hidden
wscript.exe "%DIR%run-hidden.vbs"

echo.
echo ============================================================
echo   OpenCode Go Proxy ready.
echo   - Auto-starts at every boot (hidden, no pop-ups)
echo   - Running now on http://127.0.0.1:4001/
echo   - Stop: stop.cmd    Remove autostart: uninstall-startup.cmd
echo ============================================================
pause
