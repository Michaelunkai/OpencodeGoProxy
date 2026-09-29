param(
    [switch]$SimulationOnly,
    [switch]$LiveOnly,
    [string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) { Join-Path $projectRoot 'OpencodeGoProxy.exe' } else { [IO.Path]::GetFullPath($ExecutablePath) }
$configPath = Join-Path $projectRoot 'config.json'
$credentialPath = 'F:\backup\windowsapps\credentials\opencodego\api.txt'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$artifactRoot = Join-Path $projectRoot ('test-artifacts\' + $runId)
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw 'Build the proxy first with build.ps1.' }
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw 'Generate config first with generate_config.ps1.' }
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

function Get-FreePort {
    $tcp = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Parse('127.0.0.1'), 0)
    $tcp.Start()
    $port = ([System.Net.IPEndPoint]$tcp.LocalEndpoint).Port
    $tcp.Stop()
    return $port
}

function Wait-HttpReady([string]$url, [System.Diagnostics.Process]$process, [int]$timeoutSeconds = 20) {
    $until = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $until) {
        if ($process.HasExited) { throw "Process exited before becoming ready. See its test log." }
        try { return Invoke-RestMethod -UseBasicParsing -Uri $url -TimeoutSec 2 } catch { Start-Sleep -Milliseconds 250 }
    }
    throw "Timed out waiting for local test listener."
}

function Invoke-FailoverCase([int]$failureStatus, [string]$requestPath) {
    $upstreamPort = Get-FreePort
    $proxyPort = Get-FreePort
    $caseName = switch ($requestPath) {
        '/v1/chat/completions' { 'chat' }
        '/v1/responses' { 'responses' }
        '/v1/messages' { 'messages' }
        default { throw 'Unknown synthetic protocol path.' }
    }
    $caseRoot = Join-Path $artifactRoot ($caseName + '-' + [string]$failureStatus)
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $reportPath = Join-Path $caseRoot 'mock-report.json'
    $fakeCredentialPath = Join-Path $caseRoot 'fake-keys.txt'
    $testConfigPath = Join-Path $caseRoot 'test-config.json'
    $mockStdout = Join-Path $caseRoot 'mock.stdout.log'
    $mockStderr = Join-Path $caseRoot 'mock.stderr.log'
    $proxyStdout = Join-Path $caseRoot 'simulation.stdout.log'
    $proxyStderr = Join-Path $caseRoot 'simulation.stderr.log'
    [IO.File]::WriteAllText($fakeCredentialPath, "test-key-1`r`n", (New-Object Text.UTF8Encoding($false)))
    $testConfig = [ordered]@{
        listen_prefix = "http://127.0.0.1:$proxyPort/"
        upstream_base_url = "http://127.0.0.1:$upstreamPort/v1"
        credential_source = $fakeCredentialPath
        local_api_key = 'test-local-key'
        public_model = 'opencode-go'
        upstream_model = 'kimi-k3'
        credential_count = 2
        retry_server_error_from = 500
        retry_http_statuses = @(401,402,403,408,425,429)
        streaming_mode = 'buffer-before-response-to-preserve-failover'
    }
    [IO.File]::WriteAllText($testConfigPath, ($testConfig | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $mock = $null
    $proxy = $null
    try {
        $mock = Start-Process -FilePath $exePath -ArgumentList @('-Mode','MockUpstream','-Port',([string]$upstreamPort),'-FailureStatus',([string]$failureStatus),'-ReportPath',('"' + $reportPath + '"')) `
            -WorkingDirectory $projectRoot -RedirectStandardOutput $mockStdout -RedirectStandardError $mockStderr -PassThru -WindowStyle Hidden
        $proxy = Start-Process -FilePath $exePath -ArgumentList @('-Mode','Serve','-ConfigPath',('"' + $testConfigPath + '"')) `
            -WorkingDirectory $projectRoot -RedirectStandardOutput $proxyStdout -RedirectStandardError $proxyStderr -PassThru -WindowStyle Hidden
        $health = Wait-HttpReady ("http://127.0.0.1:$proxyPort/health") $proxy
        if ($health.credential_count -ne 1) { throw 'Simulation proxy did not start with exactly one test credential.' }
        [IO.File]::AppendAllText($fakeCredentialPath, "test-key-2`r`n", (New-Object Text.UTF8Encoding($false)))
        $health = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:$proxyPort/health") -TimeoutSec 5
        if ($health.credential_count -ne 2) { throw 'Running proxy did not refresh the newly appended second credential.' }
        [IO.File]::AppendAllText($fakeCredentialPath, "test-key-3`r`n", (New-Object Text.UTF8Encoding($false)))
        $health = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:$proxyPort/health") -TimeoutSec 5
        if ($health.credential_count -ne 3) { throw 'Running proxy did not refresh the newly appended third credential.' }
        [IO.File]::AppendAllText($fakeCredentialPath, "test-key-2`r`n", (New-Object Text.UTF8Encoding($false)))
        $health = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:$proxyPort/health") -TimeoutSec 5
        if ($health.credential_count -ne 3) { throw 'Duplicate credentials were not removed from the live key pool.' }
        $localHeaders = @{ Authorization = 'Bearer test-local-key' }
        $catalog = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:$proxyPort/v1/models") -Headers $localHeaders -TimeoutSec 20
        $catalogIds = @($catalog.data | ForEach-Object { [string]$_.id })
        foreach ($expectedCatalogModel in @('deepseek-v4-flash', 'qwen3.6-plus', 'muse-spark-1.3-contributor')) {
            if ($catalogIds -notcontains $expectedCatalogModel) { throw 'Dynamic model catalog did not merge models from every credential.' }
        }
        if (@($catalogIds | Select-Object -Unique).Count -ne 3) { throw 'Dynamic model catalog did not return the union of three distinct key-specific catalogs.' }
        if ($requestPath -eq '/v1/responses') {
            $requestBody = @{ model='opencode-go-failover/deepseek-v4-flash'; input='Hello world failover'; max_output_tokens=1 }
            $localHeaders = @{ Authorization = 'Bearer test-local-key' }
        }
        elseif ($requestPath -eq '/v1/messages') {
            $requestBody = @{ model='opencode-go-failover/deepseek-v4-flash'; messages=@(@{role='user';content='Hello world failover'}); max_tokens=1 }
            $localHeaders = @{ 'x-api-key' = 'test-local-key'; 'anthropic-version' = '2023-06-01' }
        }
        else {
            $requestBody = @{ model='opencode-go-failover/deepseek-v4-flash'; messages=@(@{role='user';content='Hello world failover'}); max_tokens=1 }
            $localHeaders = @{ Authorization = 'Bearer test-local-key' }
        }
        $body = $requestBody | ConvertTo-Json -Depth 8 -Compress
        $response = Invoke-WebRequest -UseBasicParsing -Uri ("http://127.0.0.1:$proxyPort" + $requestPath) -Method Post -Headers $localHeaders -ContentType 'application/json' -Body $body -TimeoutSec 20
        $responseJson = $response.Content | ConvertFrom-Json
        if ($responseJson.credentials -ne '[REDACTED] [REDACTED] [REDACTED]') { throw 'Synthetic credentials were not fully redacted from the upstream response.' }
        if ($response.Headers['x-request-id'] -match 'test-key') { throw 'Synthetic credential leaked in an upstream response header.' }
        $until = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $reportPath) -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Mock upstream did not record all failover attempts.' }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $orderedSlots = @($report.auth_slots) -join ','
        $orderedStatuses = @($report.statuses) -join ','
        $reportedApiKeySlots = @($report.api_key_slots) -join ','
        $reportedPaths = @($report.paths | Select-Object -Unique)
        if ($orderedSlots -ne 'key-1,key-2,key-3') { throw "Expected ordered key rotation; received $orderedSlots." }
        if ($orderedStatuses -ne ($failureStatus.ToString() + ',429,200')) { throw "Expected $failureStatus,429,200; received $orderedStatuses." }
        if ($reportedPaths.Count -ne 1 -or $reportedPaths[0] -ne ('/v1' + $requestPath.Substring(3))) { throw 'The requested protocol path was not preserved upstream.' }
        $expectedApiKeySlots = if ($requestPath -eq '/v1/messages') { 'key-1,key-2,key-3' } else { 'none,none,none' }
        if ($reportedApiKeySlots -ne $expectedApiKeySlots) { throw 'The Anthropic API key header was not forwarded using the active failover key.' }
        if (@($report.models | Select-Object -Unique).Count -ne 1 -or $report.models[0] -ne 'deepseek-v4-flash') { throw 'The requested DeepSeek model was not preserved upstream.' }
        if (-not $report.request_body_identical) { throw 'The exact forwarded payload differed on retry.' }
        $proxyText = (Get-Content -LiteralPath $proxyStdout -Raw) + (Get-Content -LiteralPath $proxyStderr -Raw)
        if ($proxyText.Contains('test-key-1') -or $proxyText.Contains('test-key-2') -or $proxyText.Contains('test-key-3') -or $proxyText.Contains('test-local-key')) { throw 'Synthetic credential leaked into proxy logs.' }
        Write-Output ("SIMULATION_PASS protocol=" + $caseName + " status_sequence=" + $failureStatus + ",429,200 key_slots=1,2,3 same_payload=true model=deepseek-v4-flash all_key_catalog_union=true duplicate_key_removal=true dynamic_key_refresh=true response_and_log_redaction=true")
    }
    finally {
        foreach ($child in @($proxy,$mock)) {
            if ($null -ne $child -and -not $child.HasExited) { Stop-Process -Id $child.Id -ErrorAction SilentlyContinue }
        }
    }
}

function Invoke-FailoverSimulation {
    foreach ($failureStatus in @(400, 402, 429, 500)) {
        foreach ($requestPath in @('/v1/chat/completions', '/v1/responses', '/v1/messages')) {
            Invoke-FailoverCase $failureStatus $requestPath
        }
    }
}

function Invoke-LiveProxyTest {
    if (-not (Test-Path -LiteralPath $credentialPath -PathType Leaf)) { throw 'Configured provider credential file is missing.' }
    $proxyConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$proxyConfig.local_api_key)) { throw 'Local proxy authorization is not configured.' }
    $headers = @{ Authorization = ('Bearer ' + [string]$proxyConfig.local_api_key) }
    $catalogResponse = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:4000/v1/models' -Headers $headers -TimeoutSec 30
    $catalog = $catalogResponse.Content | ConvertFrom-Json
    if (@($catalog.data | ForEach-Object { $_.id }) -notcontains 'deepseek-v4-flash') { throw 'Live model catalog omitted DeepSeek V4 Flash.' }
    $body = @{ model='opencode-go-failover/deepseek-v4-flash'; messages=@(@{role='user';content='Reply with exactly: OK'}); max_tokens=1 } | ConvertTo-Json -Depth 8 -Compress
    $response = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:4000/v1/chat/completions' -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 150
    if ([int]$response.StatusCode -ne 200) { throw "Live proxy request returned HTTP $([int]$response.StatusCode)." }
    $json = $response.Content | ConvertFrom-Json
    if ($null -eq $json.choices -or $json.choices.Count -lt 1) { throw 'Live proxy response did not contain a completion choice.' }
    if ($response.Content.Contains([string]$proxyConfig.local_api_key)) { throw 'Live response exposed the local proxy key.' }
    Write-Output ("LIVE_PROXY_PASS status=200 model=deepseek-v4-flash catalog_models=" + @($catalog.data).Count + " response_chars=" + $response.Content.Length)
}

try {
    if (-not $LiveOnly) { Invoke-FailoverSimulation }
    if (-not $SimulationOnly) { Invoke-LiveProxyTest }
    Write-Output ("TEST_ARTIFACTS " + $artifactRoot)
} catch {
    $line = $_.InvocationInfo.ScriptLineNumber
    Write-Error ($_.Exception.GetType().Name + ': ' + $_.Exception.Message + ' (line ' + $line + ')')
    Write-Output ("TEST_ARTIFACTS " + $artifactRoot)
    exit 1
}
