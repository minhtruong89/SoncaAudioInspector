param(
    [Parameter(Mandatory)][string]$Version,
    # Dòng 1 = tóm tắt thay đổi (hiện trên /temp-updates và trong popup cập nhật), các dòng sau = chi tiết.
    [Parameter(Mandatory)][string]$FeaturesFile,
    [string]$Server = 'https://inventory.acnos.store',
    [string]$Submitter = $env:COMPUTERNAME,
    # Bỏ trống: tự build portable x64 vào artifact mới.
    [string]$PortableDir,
    [string]$OutputRoot = 'artifacts'
)
# Gửi BẢN TẠM lên server (v1.5.3): không cần khóa ký, không SSH. Server chỉ ký và phát cho máy kênh Beta sau khi ADMIN
# duyệt trên /temp-updates. Token gửi: biến môi trường SONCA_UPDATE_SUBMIT_TOKEN (chủ cấp, không commit, không in ra log).
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $env:SONCA_UPDATE_SUBMIT_TOKEN) { throw 'Chưa có token gửi bản tạm: đặt biến môi trường SONCA_UPDATE_SUBMIT_TOKEN (xin chủ hệ thống).' }
if (-not (Test-Path -LiteralPath $FeaturesFile -PathType Leaf)) { throw "Không thấy file ghi chú: $FeaturesFile" }
$csprojText = [IO.File]::ReadAllText((Join-Path $repo 'SoncaAudioInspector.csproj'))
$builtVersion = [regex]::Match($csprojText, '<Version>\s*([0-9.]+)\s*</Version>').Groups[1].Value
$builtChannel = [regex]::Match($csprojText, '<SoncaReleaseChannel>\s*([a-z]+)\s*</SoncaReleaseChannel>').Groups[1].Value
if ($builtVersion -ne $Version) { throw "csproj <Version> là '$builtVersion', khác -Version '$Version'. Sửa 4 trường version trong csproj rồi chạy lại." }
if ($builtChannel -ne 'beta') { throw "Bản tạm phải build với <SoncaReleaseChannel>beta</SoncaReleaseChannel> (đang là '$builtChannel')." }
$art = & "$PSScriptRoot/new-artifact.ps1" -Description "Temp update $Version" -Slug 'temp-update' -OutputRoot $OutputRoot
New-Item -ItemType Directory -Force "$art/logs" | Out-Null
if (-not $PortableDir) {
    & "$PSScriptRoot/publish-portable.ps1" -Architecture x64 -SkipStationData -OutputRoot $art -Description "Portable for temp update $Version" *> "$art/logs/publish-portable.log"
    if ($LASTEXITCODE -ne 0) { throw 'Publish portable thất bại; xem log.' }
    $portableArtifact = Get-ChildItem -LiteralPath $art -Directory | Where-Object Name -Like '*-portable-x64' | Select-Object -Last 1
    $PortableDir = Join-Path $portableArtifact.FullName 'SoncaAudioInspector-win-x64'
}
$project = Join-Path $repo 'tools/UpdateFeedTool/UpdateFeedTool.csproj'
& dotnet build $project -c Release -m:1 -p:UseSharedCompilation=false "-p:BaseIntermediateOutputPath=$art/obj/" "-p:OutputPath=$art/bin/" -p:NuGetAudit=false *> "$art/logs/build-tool.log"
if ($LASTEXITCODE -ne 0) { throw 'Build UpdateFeedTool thất bại; xem log.' }
& dotnet (Join-Path $art 'bin/UpdateFeedTool.dll') submit-temp --portable $PortableDir --version $Version --features-file $FeaturesFile `
    --server $Server --submitter $Submitter 2>&1 | Tee-Object -FilePath "$art/logs/submit-temp.log"
if ($LASTEXITCODE -ne 0) { throw 'Gửi bản tạm thất bại; xem log.' }
Write-Output "Đã gửi bản tạm $Version. ADMIN duyệt tại $Server/temp-updates; sau khi duyệt chạy: dotnet `"$art/bin/UpdateFeedTool.dll`" verify-temp --version $Version"
