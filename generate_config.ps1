$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $projectRoot 'OpencodeGoProxy.exe'
$configPath = Join-Path $projectRoot 'config.json'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the proxy first with build.ps1.' }
& $exePath -Mode GenerateConfig -ConfigPath $configPath
if ($LASTEXITCODE -ne 0) { throw "Config generation failed with exit code $LASTEXITCODE." }
Write-Output "ACTIVE_CONFIG $configPath"
