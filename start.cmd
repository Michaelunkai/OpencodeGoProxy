@echo off
:: OpenCode Go Proxy - Silent Launcher (portable: prefers the newest local build).
set "DIR=%~dp0"
set "EXE=%DIR%OpencodeGoProxy.exe"
if exist "%DIR%OpencodeGoProxy_v10_1.exe" set "EXE=%DIR%OpencodeGoProxy_v10_1.exe"
if exist "%DIR%OpencodeGoProxy_v10.exe" set "EXE=%DIR%OpencodeGoProxy_v10.exe"
if exist "%DIR%OpencodeGoProxy_v9.exe" set "EXE=%DIR%OpencodeGoProxy_v9.exe"
set "CFG=%DIR%config.json"
set "LOG=%DIR%Logs"
if not exist "%LOG%" mkdir "%LOG%"
start "" /B "%EXE%" -Mode Serve -ConfigPath "%CFG%" 1>> "%LOG%\proxy.out.log" 2>> "%LOG%\proxy.err.log"
