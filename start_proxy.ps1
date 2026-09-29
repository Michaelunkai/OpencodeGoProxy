param([switch]$Foreground)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $projectRoot 'OpencodeGoProxy.exe'
$configPath = Join-Path $projectRoot 'config.json'
$stdoutPath = Join-Path $projectRoot 'proxy.stdout.log'
$stderrPath = Join-Path $projectRoot 'proxy.stderr.log'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the proxy first with build.ps1.' }
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'Generate config first with generate_config.ps1.' }
if ($Foreground) {
    & $exePath -Mode Serve -ConfigPath $configPath
    exit $LASTEXITCODE
}
$existing = Get-CimInstance Win32_Process -Filter "Name = 'OpencodeGoProxy.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match [regex]::Escape($configPath) }
if ($existing) {
    Write-Output ("ALREADY_RUNNING pid=" + $existing.ProcessId)
    exit 0
}
$process = Start-Process -FilePath $exePath -ArgumentList @('-Mode','Serve','-ConfigPath',('"' + $configPath + '"')) `
    -WorkingDirectory $projectRoot -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru -WindowStyle Hidden
Write-Output ("STARTED pid=" + $process.Id + " executable=" + $exePath + " config=" + $configPath)
