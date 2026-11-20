# Quick stable patch x.y.z.N or next x.y.(z+1) (skill .agents/skills/sonca-quick-patch). Windows PowerShell 5.1 compatible.
# Does the mechanical part of sonca-version-update in one call, reusing the cached previous feed:
#   live check -> previous feed from cache -> csproj bump -> portable build -> signed feed + verify -> [deploy paced -> UpdateReleaseCheck]
# Never commits, tags or pushes (the skill does that after this script passes). Never prints secrets.
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(\.[1-9]\d*)?$')][string]$Version,
    [string]$FeaturesFile,
    # Stop after the read-only checks (live versions, cache, git state); changes nothing.
    [switch]$CheckOnly,
    # Deploy to the VPS and run UpdateReleaseCheck. Without it the script stops after the local signed feed.
    [switch]$Deploy,
    [ValidateRange(0, 60)][int]$PauseSeconds = 8,
    [string]$Server = 'https://inventory.acnos.store/updates/audio-inspector'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$csproj = Join-Path $root 'SoncaAudioInspector.csproj'
if (-not $FeaturesFile) { $FeaturesFile = Join-Path $root "docs/release-notes/$Version.txt" }
function Step([string]$text) { Write-Host ("[quick-patch] " + $text) }
function Sha([byte[]]$bytes) { $h = [Security.Cryptography.SHA256]::Create(); try { ($h.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $h.Dispose() } }
function Get-Bytes([string]$url) { $c = New-Object Net.WebClient; try { $c.Headers['Cache-Control'] = 'no-cache'; $c.DownloadData($url + '?t=' + [DateTime]::UtcNow.Ticks) } finally { $c.Dispose() } }

# 1. Inputs.
if (-not (Test-Path -LiteralPath $FeaturesFile -PathType Leaf)) { throw "Thiếu ghi chú phát hành $FeaturesFile (UTF-8, dòng 1 = tóm tắt)." }
$featureBytes = [IO.File]::ReadAllBytes($FeaturesFile)
if ($featureBytes.Length -ge 3 -and $featureBytes[0] -eq 0xEF -and $featureBytes[1] -eq 0xBB -and $featureBytes[2] -eq 0xBF) { throw "$FeaturesFile có BOM; lưu UTF-8 không BOM." }
$notes = ([Text.Encoding]::UTF8.GetString($featureBytes) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
if (-not $notes) { throw "$FeaturesFile trống." }
$new = [Version]$Version
$csprojBom = [IO.File]::ReadAllBytes($csproj)[0] -eq 0xEF
$csprojText = [IO.File]::ReadAllText($csproj)
if ($csprojText -notmatch '<SoncaReleaseChannel>\s*stable\s*</SoncaReleaseChannel>') { throw 'csproj không ở kênh stable; bản beta dùng sonca-beta-release.' }

# 2. Live feed: the new version must be above every published version (stable, beta, archived).
$liveLatest = Get-Bytes "$Server/latest.json"; $liveCatalog = Get-Bytes "$Server/versions.json"
$live = [Text.Encoding]::UTF8.GetString($liveLatest) | ConvertFrom-Json
$catalog = [Text.Encoding]::UTF8.GetString($liveCatalog) | ConvertFrom-Json
$published = @($catalog.versions | ForEach-Object { [Version]$_.version }) + @([Version]$live.version)
$highest = ($published | Sort-Object -Descending | Select-Object -First 1)
Step "Live stable $($live.version); cao nhất đã phát hành $highest; bản mới $Version."
if ($new -le $highest) { throw "Version $Version phải cao hơn $highest." }
# Allowed: x.y.z.N above x.y.z[.M] (4th number), or x.y.(z+1) with no 4th number. Anything else: sonca-version-update.
$sameBuild = $new.Build -eq [Math]::Max(0, $highest.Build) -and $new.Revision -gt 0
$nextBuild = $new.Build -eq [Math]::Max(0, $highest.Build) + 1 -and $new.Revision -lt 0
if ($new.Major -ne $highest.Major -or $new.Minor -ne $highest.Minor -or -not ($sameBuild -or $nextBuild)) {
    throw "Bản vá nhanh: tăng số thứ tư ($($highest.Major).$($highest.Minor).$([Math]::Max(0,$highest.Build)).N) hoặc lên $($highest.Major).$($highest.Minor).$([Math]::Max(0,$highest.Build) + 1); số khác dùng sonca-version-update."
}
# csproj convention: Version/InformationalVersion as written, AssemblyVersion/FileVersion always 4 parts (1.5.6 -> 1.5.6.0).
$assemblyVersion = if ($new.Revision -lt 0) { "$Version.0" } else { $Version }

# 3. Previous feed = a cached signed feed byte-identical to the live latest.json and versions.json.
$liveHash = Sha $liveLatest; $catalogHash = Sha $liveCatalog
$previous = Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Directory -Filter '*-update-feed' | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'feed' } |
    Where-Object { (Test-Path "$_/latest.json") -and (Test-Path "$_/versions.json") -and (Sha ([IO.File]::ReadAllBytes("$_/latest.json"))) -eq $liveHash -and (Sha ([IO.File]::ReadAllBytes("$_/versions.json"))) -eq $catalogHash } |
    Select-Object -First 1
if (-not $previous) { throw 'Không có feed cache khớp từng byte feed live (artifact quá 7 ngày hoặc bị dọn). Dùng sonca-version-update để hydrate feed trước.' }
Step "Feed trước (cache khớp live): $previous"

# 4. Git: only release files may differ from HEAD in the build inputs.
$dirty = @(git -C $root status --porcelain -- src tools scripts Assets App.xaml App.xaml.cs SoncaAudioInspector.csproj checking_config.json | Where-Object { $_ })
if ($dirty.Count -gt 0) { throw "Còn thay đổi chưa commit trong đầu vào build:`n$($dirty -join "`n")`nCommit bản sửa trước (người điều phối), rồi chạy lại." }
$source = (git -C $root rev-parse HEAD).Trim()
Step "Mã nguồn HEAD $source"
if ($CheckOnly) { Step 'CheckOnly: không đổi gì.'; return }

# 5. Bump the four csproj version fields.
$bumped = $csprojText
foreach ($pair in @(@('Version', $Version), @('AssemblyVersion', $assemblyVersion), @('FileVersion', $assemblyVersion), @('InformationalVersion', $Version))) {
    $pattern = "<$($pair[0])>[^<]*</$($pair[0])>"
    if ($bumped -notmatch $pattern) { throw "csproj thiếu <$($pair[0])>." }
    $bumped = [regex]::Replace($bumped, $pattern, "<$($pair[0])>$($pair[1])</$($pair[0])>", 1)
}
[IO.File]::WriteAllText($csproj, $bumped, (New-Object Text.UTF8Encoding($csprojBom)))
Step "csproj -> $Version"

# 6. Portable build (one build, reused for feed, check and deploy).
$before = Get-Date
& (Join-Path $root 'scripts/publish-portable.ps1') -Architecture x64 -SkipStationData -Description "Portable $Version (quick patch)" | Out-Host
$portableArt = Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Directory -Filter '*-portable-x64' | Where-Object { $_.CreationTime -ge $before } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $portableArt) { throw 'Không thấy artifact portable vừa build.' }
$portable = Join-Path $portableArt.FullName 'SoncaAudioInspector-win-x64'
$exe = Get-Item (Join-Path $portable 'SoncaAudioInspector.exe')
$dll = Join-Path $portable 'SoncaAudioInspector.dll'
if ([Version]$exe.VersionInfo.FileVersion -ne [Version]$assemblyVersion) { throw "Binary FileVersion $($exe.VersionInfo.FileVersion) khác $assemblyVersion." }
foreach ($forbidden in @('save standards', 'checking_config_realRoute.json', 'verify.txt', '.env')) {
    if (Test-Path -LiteralPath (Join-Path $portable $forbidden)) { throw "Portable chứa dữ liệu trạm: $forbidden" }
}
Step "Portable $portable (FileVersion $($exe.VersionInfo.FileVersion))"

# 7. Signed feed + local verify against the production public key.
$feedOut = & (Join-Path $root 'scripts/publish-update.ps1') -PortableDir $portable -Version $Version -Notes $notes -Previous $previous -FeaturesFile $FeaturesFile
$feedLine = @($feedOut) | Where-Object { $_ -match 'đã build \+ verify: (.+)\. Chưa deploy\.' } | Select-Object -Last 1
if (-not $feedLine) { throw "publish-update không báo feed: $($feedOut -join ' ')" }
$feed = [regex]::Match($feedLine, 'verify: (.+)\. Chưa deploy\.').Groups[1].Value
$feedArt = Split-Path -Parent $feed
$built = Get-Content -LiteralPath "$feedArt/logs/build-feed.log" -Encoding UTF8 | Where-Object { $_ -match '^PASS build' } | Select-Object -Last 1
Step "Feed $feed"
Step "$built"

$summary = [ordered]@{ version = $Version; source = $source; portable = $portable; previous = $previous; feed = $feed; build = $built; deployed = $false; releaseCheck = $null }
if (-not $Deploy) { $summary | ConvertTo-Json | Set-Content -LiteralPath "$feedArt/quick-patch.json" -Encoding UTF8; Step 'Chưa deploy (thiếu -Deploy).'; return }

# 8. Deploy, paced for the VPS UFW limit (6 SSH connections / 30 s).
& (Join-Path $root 'scripts/deploy-update.ps1') -FeedDir $feed -PauseSeconds $PauseSeconds | Out-Host
$summary.deployed = $true
$check = [Text.Encoding]::UTF8.GetString((Get-Bytes "$Server/latest.json")) | ConvertFrom-Json
if ($check.version -ne $Version) { throw "latest.json live là $($check.version), chưa phải $Version." }
Step "Live latest.json = $Version"

# 9. Real updater against the live feed on a synthetic station (delta, transactional install, data kept, full ZIP hash).
$checkOut = Join-Path $feedArt 'release-check'
& dotnet run --project (Join-Path $root 'tools/UpdateReleaseCheck/UpdateReleaseCheck.csproj') --artifacts-path (Join-Path $feedArt 'release-check-build') "-p:AudioAssemblyPath=$dll" -- $previous $feed $checkOut *> "$feedArt/logs/update-release-check.log"
if ($LASTEXITCODE -ne 0) { throw "UpdateReleaseCheck FAIL; xem $feedArt/logs/update-release-check.log" }
$summary.releaseCheck = @(Get-Content -LiteralPath "$feedArt/logs/update-release-check.log" -Encoding UTF8 | Where-Object { $_ -match '^PASS' })
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$feedArt/quick-patch.json" -Encoding UTF8
$summary.releaseCheck | ForEach-Object { Step $_ }
Step "XONG $Version live. Tiếp theo theo skill: commit 'release: v$Version', tag, push private. Tóm tắt: $feedArt/quick-patch.json"
