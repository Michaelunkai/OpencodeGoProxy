@echo off
:: OpenCode Go Proxy — one-click startup registration for fresh Windows
:: Run this ONCE after copying the folder to a new Windows install.
:: It installs a scheduled task that auto-starts the proxy at every boot (hidden, no pop-ups).
setlocal
set "DIR=%~dp0"

:: Requires admin for schtasks registration
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

schtasks /Create /F /TN "OpenCodeGoProxy" /TR "wscript.exe \"%DIR%run-hidden.vbs\"" /SC ONLOGON /RL HIGHEST /F

if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo   OpenCode Go Proxy installed successfully!
    echo   - Auto-starts at every boot (hidden, no pop-ups)
    echo   - Start now manually: run-hidden.vbs
    echo   - Stop: stop.cmd
    echo ============================================================
) else (
    echo Failed to install startup task.
)
pause
