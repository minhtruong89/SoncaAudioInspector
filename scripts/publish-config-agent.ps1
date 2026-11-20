# Publishes one model config and approves it in the same call (POST /api/app/audio-config/agent-publish), using the token from
# scripts/new-config-agent-token.ps1. The server signs the ACTIVE revision exactly like a web approval; stations load it on
# sign-in / model selection. Windows PowerShell 5.1 compatible. The token is never printed.
#   scripts/publish-config-agent.ps1 -File tools/AudioLifecycleCheck/Fixtures/C360-rev5.json -Note "C360 rev 5"
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$Note = '',
    [string]$Server = 'https://inventory.acnos.store',
    [string]$TokenPath = (Join-Path $env:USERPROFILE '.sonca-update\config-agent\token.txt'),
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$json = [IO.File]::ReadAllText((Resolve-Path $File), [Text.Encoding]::UTF8).TrimStart([char]0xFEFF)
$config = $json | ConvertFrom-Json
if (-not $config.model -or -not $config.testItems.InOut.Tests) { throw "File không phải cấu hình model (thiếu model / testItems.InOut.Tests)." }
$enabled = @($config.testItems.InOut.Tests | Where-Object { $_.enable -ne $false -and $_.IsEnabled -ne $false })
Write-Host "Model $($config.model): $($enabled.Count) bài đang bật — $((@($enabled | ForEach-Object { $_.id })) -join ', ')"
if ($WhatIf) { Write-Host "WhatIf: không gửi."; return }
if (-not (Test-Path $TokenPath)) { throw "Chưa có token agent ($TokenPath). Chủ chạy scripts/new-config-agent-token.ps1 trước." }
$token = [IO.File]::ReadAllText($TokenPath).Trim()
# Keep the config bytes as written: embed the raw JSON instead of re-serialising it through PowerShell.
$noteJson = ConvertTo-Json ([string]$Note)
$modelJson = ConvertTo-Json ([string]$config.model)
$body = ([char]123) + [char]34 + "speakerModel" + [char]34 + ":" + $modelJson + "," + [char]34 + "note" + [char]34 + ":" + $noteJson + "," + [char]34 + "config" + [char]34 + ":" + $json + ([char]125)
$response = Invoke-WebRequest -UseBasicParsing -Method Post -Uri ($Server.TrimEnd('/') + '/api/app/audio-config/agent-publish') `
    -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body))
$data = ($response.Content | ConvertFrom-Json).data
Write-Host "Đã duyệt: $($data.speakerModel) serial $($data.serial) · revision $($data.id) · $($data.status) · sha256 $($data.configSha256)"
