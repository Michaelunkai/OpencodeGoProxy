@echo off
:: Uninstall the OpenCode Go Proxy startup task
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
schtasks /Delete /TN "OpenCodeGoProxy" /F >nul 2>&1
taskkill /F /IM OpencodeGoProxy.exe >nul 2>&1
echo Startup task removed and proxy stopped.
pause
