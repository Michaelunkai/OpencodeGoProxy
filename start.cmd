@echo off
:: OpenCode Go Proxy - Silent Launcher
:: Runs the proxy with zero terminal pop-ups.
:: First run: creates api.txt template if missing.
set "DIR=%~dp0"
set "EXE=%DIR%OpencodeGoProxy.exe"
set "CFG=%DIR%config.json"
set "KEYS=%DIR%api.txt"
set "LOG=%DIR%Logs"
if not exist "%LOG%" mkdir "%LOG%"

:: Create api.txt template if missing
if not exist "%KEYS%" (
    echo # Add your OpenCode API keys here, one per line > "%KEYS%"
    echo # Get keys from: https://opencode.ai/settings/keys >> "%KEYS%"
)

:: Start proxy silently (no window)
start "" /B "%EXE%" -Mode Serve -ConfigPath "%CFG%" 1>> "%LOG%\proxy.out.log" 2>> "%LOG%\proxy.err.log"
