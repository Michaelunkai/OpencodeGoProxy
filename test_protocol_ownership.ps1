# Protocol-ownership regression test.
#
# Reproduces the OpenCode Go 400 "Model does not support this protocol."
# (error.type = ModelProtocolUnsupported) against a mock gateway that serves a
# set of models ONLY on /responses, and proves the proxy now:
#   1. repairs an uncalibrated Responses-only model by retrying it on /responses
#      and remembering the winner in model-protocols.json;
#   2. translates a chat caller onto the Responses wire (JSON and SSE);
#   3. keeps a chat-native model on its own wire, and translates the reverse
#      direction (a Responses caller onto a chat-only model).
#
# Build a sidecar executable first so a running proxy is not disturbed:
#   & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -ExecutionPolicy Bypass `
#       -File .\build.ps1 -OutputPath 'OpencodeGoProxy.protocol-test.exe'
#   & 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -ExecutionPolicy Bypass `
#       -File .\test_protocol_ownership.ps1 -ExePath '.\OpencodeGoProxy.protocol-test.exe'
param([string]$ExePath = '.\OpencodeGoProxy.protocol-test.exe')

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = [IO.Path]::GetFullPath((Join-Path $project $ExePath))
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Executable not found: $exe" }

$root = Join-Path $project ('test-artifacts\protocol-ownership-' + (Get-Date -Format 'yyyyMMddTHHmmssZ'))
New-Item -ItemType Directory -Force -Path $root | Out-Null

$keys = Join-Path $root 'fake-keys.txt'
Set-Content -LiteralPath $keys -Value 'test-key-1' -Encoding ASCII
$configPath = Join-Path $root 'config.json'
$config = [ordered]@{
    listen_prefix = 'http://127.0.0.1:4099/'
    upstream_base_url = 'http://127.0.0.1:4098/v1'
    credential_source = $keys
    local_api_key = 'test-local-key'
    public_model = 'opencode-go'
    upstream_model = 'kimi-k3'
    credential_count = 1
    retry_server_error_from = 500
    retry_http_statuses = @(401, 402, 403, 408, 425, 429)
    streaming_mode = 'buffer-before-response-to-preserve-failover'
}
Set-Content -LiteralPath $configPath -Value ($config | ConvertTo-Json -Depth 5) -Encoding UTF8

$mockLog = Join-Path $root 'mock.log'
$env:MOCK_LOG = $mockLog
$mock = Start-Process -FilePath 'node' -ArgumentList (Join-Path $project 'mock-model-protocol-upstream.js') `
    -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root 'mock.out.log') -RedirectStandardError (Join-Path $root 'mock.err.log')
Start-Sleep -Milliseconds 900
$proxy = Start-Process -FilePath $exe -ArgumentList @('-Mode', 'Serve', '-ConfigPath', $configPath) `
    -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root 'proxy.out.log') -RedirectStandardError (Join-Path $root 'proxy.err.log')
Start-Sleep -Milliseconds 1200

$headers = @{ Authorization = 'Bearer test-local-key' }
function Invoke-Post([string]$path, [string]$body) {
    return Invoke-WebRequest -UseBasicParsing -Uri ('http://127.0.0.1:4099' + $path) -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body
}

$checks = New-Object System.Collections.ArrayList
function Check([string]$name, $actual, $expected) {
    [void]$checks.Add([pscustomobject]@{
        Name = $name; Actual = $actual; Expected = $expected; Passed = ($actual -eq $expected)
    })
}

try {
    # 1. Uncalibrated Responses-only model over chat: repair + remember.
    $d0 = (Invoke-Post '/v1/chat/completions' '{"model":"opencode-go-failover/space-bunny-free","messages":[{"role":"user","content":"ping"}],"stream":false}').Content | ConvertFrom-Json
    Check 'repair.object' $d0.object 'chat.completion'
    Check 'repair.content' $d0.choices[0].message.content 'pong-from-responses'

    # 2. Seeded Responses-only model over chat: translated straight away.
    $d1 = (Invoke-Post '/v1/chat/completions' '{"model":"opencode-go-failover/muse-spark-1.3-contributor","messages":[{"role":"user","content":"ping"}],"stream":false}').Content | ConvertFrom-Json
    Check 'muse.object' $d1.object 'chat.completion'
    Check 'muse.content' $d1.choices[0].message.content 'pong-from-responses'
    Check 'muse.usage.prompt_tokens' $d1.usage.prompt_tokens 3

    # 3. Chat-native model keeps its own wire.
    $d2 = (Invoke-Post '/v1/chat/completions' '{"model":"opencode-go-failover/deepseek-v4-flash","messages":[{"role":"user","content":"ping"}],"stream":false}').Content | ConvertFrom-Json
    Check 'native.content' $d2.choices[0].message.content 'pong-from-chat'

    # 4. Reverse direction: a Responses caller onto a chat-only model.
    $d3 = (Invoke-Post '/v1/responses' '{"model":"opencode-go-failover/deepseek-v4-flash","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}],"stream":false}').Content | ConvertFrom-Json
    Check 'reverse.object' $d3.object 'response'
    Check 'reverse.text' $d3.output[0].content[0].text 'pong-from-chat'

    # 5. A translated streaming chat call is re-framed as chat SSE.
    $sse = (Invoke-Post '/v1/chat/completions' '{"model":"opencode-go-failover/muse-spark-1.3-contributor","messages":[{"role":"user","content":"ping"}],"stream":true}').Content
    Check 'sse.chunk' $sse.Contains('chat.completion.chunk') $true
    Check 'sse.done' $sse.Contains('data: [DONE]') $true

    Start-Sleep -Milliseconds 300
    $calls = (Get-Content -LiteralPath $mockLog) -join ' | '
    Check 'repair.retried_on_responses' ($calls -match 'chat/completions model=space-bunny-free.*responses model=space-bunny-free') $true
    $learned = Get-Content -LiteralPath (Join-Path $root 'model-protocols.json') -Raw
    Check 'learned.space_bunny' ($learned.Contains('"space-bunny-free":"responses"')) $true
}
finally {
    try { Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue } catch { }
    try { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue } catch { }
}

$checks | Format-Table -AutoSize
$failed = @($checks | Where-Object { -not $_.Passed })
Write-Output ('ARTIFACT_DIR ' + $root)
if ($failed.Count -gt 0) { throw ($failed.Count.ToString() + ' protocol-ownership check(s) failed.') }
Write-Output ('PROTOCOL_OWNERSHIP_OK checks=' + $checks.Count)
