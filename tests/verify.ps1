# Verification harness for OpencodeGoProxy (tests/verify.ps1)
# Passive-first smoke test: health, models, live round-trip on the cheapest
# model, and a secret-leak scan. Reads the port/local key from config.json.
param(
  [string]$ConfigPath = (Join-Path $PSScriptRoot "..\config.json")
)
$ErrorActionPreference = 'Stop'
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$base = $config.listen_prefix.TrimEnd('/')
$localKey = $config.local_api_key

$passes = 0; $failures = 0; $failNames = @()
function Test-Check([string]$name, [bool]$result, [string]$detail) {
  $suffix = if ([string]::IsNullOrEmpty($detail)) { '' } else { ' :: ' + $detail }
  if ($result) { $script:passes++; Write-Output "PASS $name" }
  else { $script:failures++; $script:failNames += $name; Write-Output "FAIL $name$suffix" }
}

try {
  $h = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 15
  Test-Check "health-200" ($h.status -eq 'ok' -and [int]$h.credential_count -ge 1) ""
} catch { Test-Check "health-200" $false $_.Exception.Message }

try {
  $m = Invoke-RestMethod -Uri "$base/v1/models" -Headers @{ Authorization = "Bearer $localKey" } -TimeoutSec 20
  $ids = @($m.data.id)
  $distinct = @($ids | Sort-Object -Unique)
  Test-Check "models-unique" (($ids.Count -gt 10) -and ($distinct.Count -eq $ids.Count)) ""
} catch { Test-Check "models-unique" $false $_.Exception.Message }

try {
  $body = '{"model":"mimo-v2.5","messages":[{"role":"user","content":"Reply with exactly: OK"}],"max_tokens":600}'
  $r = Invoke-RestMethod -Uri "$base/v1/chat/completions" -Method Post -Headers @{ Authorization = "Bearer $localKey" } -ContentType 'application/json' -Body $body -TimeoutSec 150
  Test-Check "live-completion-mimo" (($r.model -eq 'mimo-v2.5') -and ("$($r.choices[0].message.content)".Contains('OK'))) ""
} catch { Test-Check "live-completion-mimo" $false $_.Exception.Message }

try {
  $body = '{"model":"mimo-v2.5","input":[{"role":"user","content":[{"type":"input_text","text":"Reply with exactly: OK"}]}],"max_output_tokens":600}'
  $r = Invoke-RestMethod -Uri "$base/v1/responses" -Method Post -Headers @{ Authorization = "Bearer $localKey" } -ContentType 'application/json' -Body $body -TimeoutSec 150
  Test-Check "responses-wire" ($r.status -eq 'completed') ""
} catch { Test-Check "responses-wire" $false $_.Exception.Message }

try {
  $body = '{"model":"mimo-v2.5","messages":[{"role":"user","content":"Reply with exactly: OK"}],"max_tokens":600}'
  $raw = Invoke-WebRequest -UseBasicParsing -Uri "$base/v1/chat/completions" -Method Post -Headers @{ Authorization = "Bearer $localKey" } -ContentType 'application/json' -Body $body -TimeoutSec 150
  $text = "$($raw.Content)"
  $leak = $false
  foreach ($line in (Get-Content (Join-Path $PSScriptRoot "..\api.txt"))) { $k = "$($line)".Trim(); if ($k -and $text.Contains($k)) { $leak = $true; break } }
  Test-Check "secret-redaction" (-not $leak) ""
} catch { Test-Check "secret-redaction" $false $_.Exception.Message }

Write-Output ""
Write-Output "RESULT passes=$passes failures=$failures"
if ($failures -gt 0) { exit 1 }

