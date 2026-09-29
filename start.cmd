@echo off
:: OpenCode Go Proxy - Silent Launcher (prefers newest hard-built binary)
set "DIR=%~dp0"
set "EXE=%DIR%OpencodeGoProxy.exe"
if exist "%DIR%OpencodeGoProxy_v4.exe" set "EXE=%DIR%OpencodeGoProxy_v4.exe"
if not exist "%DIR%OpencodeGoProxy_v4.exe" if exist "%DIR%OpencodeGoProxy_v3.exe" set "EXE=%DIR%OpencodeGoProxy_v3.exe"
if not exist "%DIR%OpencodeGoProxy_v4.exe" if not exist "%DIR%OpencodeGoProxy_v3.exe" if exist "%DIR%OpencodeGoProxy_v2.exe" set "EXE=%DIR%OpencodeGoProxy_v2.exe"
set "CFG=%DIR%config.json"
set "LOG=%DIR%Logs"
if not exist "%LOG%" mkdir "%LOG%"
start "" /B "%EXE%" -Mode Serve -ConfigPath "%CFG%" 1>> "%LOG%\proxy.out.log" 2>> "%LOG%\proxy.err.log"
