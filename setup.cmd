@echo off
:: OpenCode Go Proxy - zero-step setup for ANY Windows PC.
:: 1. Creates api.txt when missing (opens it so you can paste keys).
:: 2. Creates config.json from config.default.json with a fresh local key.
:: 3. Registers the invisible ONLOGON task (zero terminal frames ever).
:: 4. Bridges 127.0.0.1:4000 -> 127.0.0.1:4001 for stale clients.
:: 5. Starts the proxy and proves both health endpoints.
setlocal EnableExtensions
set "DIR=%~dp0"
set "EXE=%DIR%OpencodeGoProxy.exe"
if exist "%DIR%OpencodeGoProxy_v10_1.exe" set "EXE=%DIR%OpencodeGoProxy_v10_1.exe"
if exist "%DIR%OpencodeGoProxy_v10.exe" set "EXE=%DIR%OpencodeGoProxy_v10.exe"
if exist "%DIR%OpencodeGoProxy_v9.exe" set "EXE=%DIR%OpencodeGoProxy_v9.exe"
set "CFG=%DIR%config.json"
set "KEYS=%DIR%api.txt"
set "VBS=%DIR%run-hidden.vbs"

if not exist "%KEYS%" (
  echo # Put one OpenCode Go API key per line. Lines starting with # are ignored.> "%KEYS%"
  echo # Get keys from your OpenCode workspaces, then save and re-run setup.cmd.>> "%KEYS%"
  echo SETUP: created api.txt - paste your keys in it, save, then re-run setup.cmd.
  start "" notepad.exe "%KEYS%"
  exit /b 3
)
set "HASKEY="
for /F "usebackq eol=# tokens=*" %%L in ("%KEYS%") do set "HASKEY=1"
if not defined HASKEY (
  echo SETUP: api.txt has no keys - paste one key per line, save, then re-run setup.cmd.
  start "" notepad.exe "%KEYS%"
  exit /b 3
)

if not exist "%CFG%" (
  copy /Y "%DIR%config.default.json" "%CFG%" >nul
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$c=Get-Content -LiteralPath '%CFG%' -Raw|ConvertFrom-Json; $r=New-Object byte[] 32; (New-Object Security.Cryptography.RNGCryptoServiceProvider).GetBytes($r); $c.local_api_key=[Convert]::ToBase64String($r).TrimEnd('=').Replace('+','-').Replace('/','_'); $c.credential_count=0; $c.zen_credential_count=0; $c|ConvertTo-Json -Compress|Set-Content -LiteralPath '%CFG%' -Encoding UTF8; Write-Output 'SETUP: fresh local_api_key generated.'"
)

schtasks /Query /TN "OpenCodeGoProxy" >nul 2>&1
if errorlevel 1 (
  schtasks /Create /F /TN "OpenCodeGoProxy" /SC ONLOGON /TR "wscript.exe \"%VBS%\"" >nul
  echo SETUP: logon task registered.
) else (
  echo SETUP: logon task already present.
)

netsh interface portproxy show v4tov4 | findstr /C:"127.0.0.1         4000" >nul 2>&1
if errorlevel 1 (
  netsh interface portproxy add v4tov4 listenport=4000 listenaddress=127.0.0.1 connectport=4001 connectaddress=127.0.0.1 >nul 2>&1
  if errorlevel 1 ( echo SETUP: portproxy bridge needs admin - continuing without it. ) else ( echo SETUP: port 4000 bridge installed. )
) else (
  echo SETUP: port 4000 bridge already present.
)

tasklist /FI "IMAGENAME eq OpencodeGoProxy.exe" 2>nul | findstr /I "OpencodeGoProxy" >nul 2>&1
if not errorlevel 1 (
  echo SETUP: proxy already running - leaving it alone.
) else (
  tasklist 2>nul | findstr /I /R "OpencodeGoProxy_v[0-9].*\.exe" >nul 2>&1
  if not errorlevel 1 (
    echo SETUP: proxy already running - leaving it alone.
  ) else (
  start "" /B "%EXE%" -Mode Serve -ConfigPath "%CFG%"
  echo SETUP: proxy started.
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ok=$true; foreach ($u in @('http://127.0.0.1:4001/health','http://127.0.0.1:4000/health')) { try { $h=Invoke-RestMethod -Uri $u -TimeoutSec 20; Write-Output (\"HEALTH $u count=\" + $h.credential_count) } catch { Write-Output (\"RETRYING $u\"); Start-Sleep -Seconds 5; try { $h=Invoke-RestMethod -Uri $u -TimeoutSec 20; Write-Output (\"HEALTH $u count=\" + $h.credential_count) } catch { Write-Output (\"FAILED $u\"); $ok=$false } } }; if (-not $ok) { exit 1 }"
if errorlevel 1 (
  echo SETUP FAILED: health check did not pass. Check Logs\proxy.out.log.
  exit /b 1
)
echo SETUP COMPLETE: proxy healthy. Point clients at http://127.0.0.1:4001/v1
