param(
    # The configuration the stations must use. Default: this station's live checking_config.json.
    [string]$ConfigPath = "$env:LOCALAPPDATA\SoncaAudioInspector\checking_config.json",
    [Parameter(Mandatory)][string]$Notes,
    [string]$KeyPath = "$env:USERPROFILE\.sonca-update\update-signing-key.pem",
    # 0 = current UTC Unix seconds. Must be higher than the live serial (clients refuse rollback).
    [long]$Serial = 0,
    [string]$LiveFeed = 'https://inventory.acnos.store/updates/audio-inspector/config/',
    [string]$OutputRoot = 'artifacts'
)
# Signs a mandatory station configuration into a new artifact. Writes nothing outside the artifact and
# touches no server; deploy with scripts/deploy-config.ps1 -FeedDir <printed path>.
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 compatible (agents run powershell.exe). TLS 1.2 for Cloudflare.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$repo = Split-Path $PSScriptRoot -Parent
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "Không thấy file config: $ConfigPath" }
if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) { throw 'Thiếu khóa ký. Cung cấp -KeyPath; script không tự sinh khóa.' }
if ($Serial -le 0) { $Serial = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }

$live = $null
try {
    $response = Invoke-WebRequest -Uri ($LiveFeed.TrimEnd('/') + '/latest.json?t=' + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) -UseBasicParsing -TimeoutSec 15
    $live = $response.Content | ConvertFrom-Json
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -ne 404) { throw "Không đọc được config live để kiểm serial (HTTP $status): $($_.Exception.Message)" }
    Write-Output 'Chưa có config live (404): đây là lần phát hành config đầu tiên.'
}
if ($live -and [long]$live.serial -ge $Serial) { throw "Serial $Serial phải lớn hơn serial live $($live.serial) (client từ chối rollback)." }

$art = & "$PSScriptRoot/new-artifact.ps1" -Description "Server config #$Serial" -Slug 'config-feed' -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force "$art/logs" | Out-Null
Copy-Item -LiteralPath $ConfigPath -Destination "$art/source-checking_config.json"
if ($live) { $live | ConvertTo-Json | Set-Content -LiteralPath "$art/live-before.json" -Encoding utf8 }

$project = Join-Path $repo 'tools/UpdateFeedTool/UpdateFeedTool.csproj'
& dotnet build $project -c Release -m:1 -p:UseSharedCompilation=false "-p:BaseIntermediateOutputPath=$art/obj/" "-p:OutputPath=$art/bin/" "-p:RestoreSources=$env:USERPROFILE\.nuget\packages" -p:NuGetAudit=false *> "$art/logs/build-tool.log"
if ($LASTEXITCODE -ne 0) { throw 'Build UpdateFeedTool thất bại; xem log.' }
$tool = Join-Path $art 'bin/UpdateFeedTool.dll'
$feed = Join-Path $art 'config-feed/config'
& dotnet $tool config --config "$art/source-checking_config.json" --out $feed --serial $Serial --key $KeyPath --notes $Notes *> "$art/logs/sign-config.log"
if ($LASTEXITCODE -ne 0) { throw 'Ký config thất bại; xem log.' }
$signatureSource = [IO.File]::ReadAllText((Join-Path $repo 'src/Infrastructure/Update/UpdateSignature.cs'))
$publicKey = [regex]::Match($signatureSource, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value
if (-not $publicKey) { throw 'Không đọc được public key production trong UpdateSignature.cs.' }
[IO.File]::WriteAllText("$art/public-key.pem", $publicKey + "`n")
& dotnet $tool verify-config --feed $feed --public-key "$art/public-key.pem" --expect-config $ConfigPath *> "$art/logs/verify-config.log"
if ($LASTEXITCODE -ne 0) { throw 'Config feed không khớp public key production hoặc file nguồn; xem log.' }
Get-Content "$art/logs/verify-config.log"
Write-Output "CONFIG_FEED=$feed"
