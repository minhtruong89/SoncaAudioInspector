param(
    [Parameter(Mandatory)][string]$FeedDir,
    [string]$VpsHost = 'root@178.18.249.39',
    [string]$SshKey = "$HOME\.ssh\tracking_shopee_codex",
    [string]$RemoteDir = '/opt/speaker-inventory/updates/audio-inspector',
    [string]$PublicUrl = 'https://inventory.acnos.store/updates/audio-inspector/config/',
    [switch]$WhatIf,
    [string]$OutputRoot = 'artifacts'
)
# Uploads a feed produced by publish-config.ps1: immutable content first, then the signed pointer, each via a
# temporary dotfile + sha256sum -c + mv. Afterwards downloads the public URLs and compares exact bytes.
# Rollback = publish the old content again with a higher serial (clients refuse a lower serial).
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 compatible (agents run powershell.exe). TLS 1.2 for Cloudflare.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
if ($VpsHost -notmatch '^[a-zA-Z0-9_.@-]+$' -or $RemoteDir -notmatch '^/[a-zA-Z0-9_/-]+$' -or $RemoteDir.Contains('..')) { throw 'Host hoặc đường dẫn remote không hợp lệ.' }
$RemoteDir = $RemoteDir.TrimEnd('/'); $remoteConfig = "$RemoteDir/config"
$FeedDir = [IO.Path]::GetFullPath($FeedDir).TrimEnd('\','/')

# The publish artifact (…/config-feed/config → artifact root) holds the same-code verifier and the production public key.
$publishArtifact = Split-Path (Split-Path $FeedDir -Parent) -Parent
$tool = Join-Path $publishArtifact 'bin/UpdateFeedTool.dll'; $publicKeyPath = Join-Path $publishArtifact 'public-key.pem'
if (-not (Test-Path -LiteralPath $tool) -or -not (Test-Path -LiteralPath $publicKeyPath)) { throw 'FeedDir phải là CONFIG_FEED do publish-config.ps1 in ra (cần bin/UpdateFeedTool.dll và public-key.pem trong artifact).' }
function VerifyFeed([string]$dir) {
    $output = & dotnet $tool verify-config --feed $dir --public-key $publicKeyPath 2>&1
    if ($LASTEXITCODE -ne 0) { throw "verify-config thất bại cho ${dir}: $output" }
    return $output
}
# Local verification before any transfer: production public key, exact size/SHA-256, JSON shape.
VerifyFeed $FeedDir
$manifest = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes("$FeedDir/latest.json")) | ConvertFrom-Json
if ($manifest.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Manifest config không hợp lệ.' }
$contentPath = "$FeedDir/files/$($manifest.sha256).json"

function NativeTool([string]$relative, [string]$fallback) {
    $native = Join-Path $env:WINDIR "System32/$relative"
    if (Test-Path -LiteralPath $native -PathType Leaf) { return $native }
    return (Get-Command $fallback -ErrorAction Stop).Source
}
$ssh = NativeTool 'OpenSSH/ssh.exe' 'ssh'; $scp = NativeTool 'OpenSSH/scp.exe' 'scp'
$sshOptions = @('-i', $SshKey, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=15')
function External([string]$command, [string[]]$arguments) {
    Write-Host ($command + ' ' + (($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '))
    if (-not $WhatIf) { & $command @arguments; if ($LASTEXITCODE -ne 0) { throw "$command thất bại: $LASTEXITCODE" } }
}
if ($WhatIf) { $art = '<new-deploy-config-artifact>' }
else { $art = & "$PSScriptRoot/new-artifact.ps1" -Description "Deploy server config #$($manifest.serial)" -Slug 'deploy-config' -OutputRoot $OutputRoot; New-Item -ItemType Directory -Force "$art/live" | Out-Null }

$id = [Guid]::NewGuid().ToString('N')
$contentRemote = "$remoteConfig/files/$($manifest.sha256).json"; $contentTemp = "$remoteConfig/files/.$($manifest.sha256).$id.tmp"
$manifestTemp = "$remoteConfig/.latest.$id.json"; $signatureTemp = "$remoteConfig/.latest.$id.sig"
$manifestHash = (Get-FileHash -LiteralPath "$FeedDir/latest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
$signatureHash = (Get-FileHash -LiteralPath "$FeedDir/latest.json.sig" -Algorithm SHA256).Hash.ToLowerInvariant()

External $ssh ($sshOptions + @($VpsHost, "mkdir -p -- '$remoteConfig/files'"))
External $scp ($sshOptions + @($contentPath, "${VpsHost}:$contentTemp"))
External $ssh ($sshOptions + @($VpsHost, "echo '$($manifest.sha256)  $contentTemp' | sha256sum -c - && mv -f -- '$contentTemp' '$contentRemote'"))
External $scp ($sshOptions + @("$FeedDir/latest.json", "${VpsHost}:$manifestTemp"))
External $scp ($sshOptions + @("$FeedDir/latest.json.sig", "${VpsHost}:$signatureTemp"))
# Pointer last; the two renames run back to back in one shell.
External $ssh ($sshOptions + @($VpsHost, "echo '$($manifest.sha256)  $contentRemote' | sha256sum -c - && echo '$manifestHash  $manifestTemp' | sha256sum -c - && echo '$signatureHash  $signatureTemp' | sha256sum -c - && mv -f -- '$signatureTemp' '$remoteConfig/latest.json.sig' && mv -f -- '$manifestTemp' '$remoteConfig/latest.json'"))
if ($WhatIf) { Write-Output 'WhatIf: sau khi chạy thật sẽ tải latest.json/.sig/files qua URL công khai và so byte.'; return }

$bust = '?t=' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
Invoke-WebRequest -Uri ($PublicUrl + 'latest.json' + $bust) -OutFile "$art/live/latest.json" -UseBasicParsing
Invoke-WebRequest -Uri ($PublicUrl + 'latest.json.sig' + $bust) -OutFile "$art/live/latest.json.sig" -UseBasicParsing
New-Item -ItemType Directory -Force "$art/live/files" | Out-Null
Invoke-WebRequest -Uri ($PublicUrl + "files/$($manifest.sha256).json") -OutFile "$art/live/files/$($manifest.sha256).json" -UseBasicParsing
foreach ($pair in @(@("$art/live/latest.json", "$FeedDir/latest.json"), @("$art/live/latest.json.sig", "$FeedDir/latest.json.sig"), @("$art/live/files/$($manifest.sha256).json", $contentPath))) {
    if ((Get-FileHash -LiteralPath $pair[0]).Hash -ne (Get-FileHash -LiteralPath $pair[1]).Hash) { throw "Bản công khai khác bản đã ký: $($pair[0])" }
}
VerifyFeed "$art/live"
Write-Output "PASS public config #$($manifest.serial): latest.json, latest.json.sig và files/$($manifest.sha256).json khớp từng byte qua $PublicUrl"
