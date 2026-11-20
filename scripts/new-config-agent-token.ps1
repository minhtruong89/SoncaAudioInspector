# Creates the long-lived "config agent" token that lets agents on this machine publish AND approve a model config
# (owner decision 2026-11-19). Run it yourself (the owner), once. Windows PowerShell 5.1 compatible.
#   Token:  %USERPROFILE%\.sonca-update\config-agent\token.txt   (never commit, never print)
#   Server: put the printed SHA-256 in /opt/speaker-inventory/shared/.env as CONFIG_AGENT_TOKEN_SHA256=<sha>
#           plus CONFIG_AGENT_STAFF_ID=<staffId of the ADMIN account approvals are recorded under>, then redeploy/restart the web.
param([switch]$Rotate)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:USERPROFILE '.sonca-update\config-agent'
$tokenPath = Join-Path $dir 'token.txt'
New-Item -ItemType Directory -Force $dir | Out-Null
if ($Rotate -or -not (Test-Path $tokenPath)) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
    $token = 'sca_' + ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'))
    [IO.File]::WriteAllText($tokenPath, $token)
}
$token = [IO.File]::ReadAllText($tokenPath).Trim()
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($token)) | ForEach-Object { $_.ToString('x2') }) -join ''
$sha.Dispose()
& icacls $dir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
Write-Host "Token: $tokenPath (chỉ user Windows này đọc được)"
Write-Host "Ghi vào /opt/speaker-inventory/shared/.env trên VPS:"
Write-Host "  CONFIG_AGENT_TOKEN_SHA256=$hash"
Write-Host "  CONFIG_AGENT_STAFF_ID=<staffId tài khoản ADMIN ghi nhận người duyệt>"
