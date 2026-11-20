param(
    [Parameter(Mandatory)][string]$FeedDir,
    [string]$VpsHost = 'root@178.18.249.39',
    [string]$SshKey = "$HOME\.ssh\tracking_shopee_codex",
    [string]$RemoteDir = '/opt/speaker-inventory/updates/audio-inspector',
    [switch]$WhatIf,
    [switch]$PageOnly,
    [string]$PageFile,
    # beta: tải blob/archive/ZIP, danh mục và beta.json; KHÔNG BAO GIỜ ghi latest.json (máy kênh Ổn định không bị mời cập nhật).
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    # VPS UFW "22/tcp LIMIT" drops a 7th SSH/SCP connection within 30 s. 8 keeps every 30 s window under the limit
    # (2026-11-18/19 releases used a breakpoint wrapper for the same wait). 0 = no wait (default, unchanged behaviour).
    [ValidateRange(0, 60)][int]$PauseSeconds = 0,
    [string]$OutputRoot = 'artifacts'
)
$ErrorActionPreference = 'Stop'
if ($VpsHost -notmatch '^[a-zA-Z0-9_.@-]+$' -or $RemoteDir -notmatch '^/[a-zA-Z0-9_/-]+$' -or $RemoteDir.Contains('..')) { throw 'Host hoặc đường dẫn remote không hợp lệ.' }
$RemoteDir = $RemoteDir.TrimEnd('/')
if (-not $RemoteDir) { throw 'Không dùng root filesystem làm thư mục feed.' }
$FeedDir = [IO.Path]::GetFullPath($FeedDir).TrimEnd('\','/')
if ($PageFile -and -not $PageOnly) { throw '-PageFile chỉ dùng cùng -PageOnly.' }
function NativeTool([string]$relative, [string]$fallback) {
    $native = Join-Path $env:WINDIR "System32/$relative"
    if (Test-Path -LiteralPath $native -PathType Leaf) { return $native }
    return (Get-Command $fallback -ErrorAction Stop).Source
}
$ssh = NativeTool 'OpenSSH/ssh.exe' 'ssh'
$scp = NativeTool 'OpenSSH/scp.exe' 'scp'
$tar = NativeTool 'tar.exe' 'tar.exe'
# A missing/changed known host is a hard failure; never add or update a host key automatically.
$sshOptions = @('-i', $SshKey, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes', '-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=15')
function Display([string]$command, [string[]]$arguments) {
    Write-Host ($command + ' ' + (($arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' '))
}
function Pace { if ($PauseSeconds -gt 0 -and -not $WhatIf) { Start-Sleep -Seconds $PauseSeconds } }
function External([string]$command, [string[]]$arguments) {
    Display $command $arguments
    if (-not $WhatIf) { Pace; & $command @arguments; if ($LASTEXITCODE -ne 0) { throw "$command thất bại: $LASTEXITCODE" } }
}
function ParseInventory([string[]]$lines, [string]$root) {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $directory = ''
    foreach ($line in $lines) {
        if (-not $line) { continue }
        if ($line.EndsWith(':')) {
            $header = $line.Substring(0, $line.Length - 1)
            if ($header -ne $root -and -not $header.StartsWith($root + '/', [StringComparison]::Ordinal)) { throw 'Inventory ra ngoài thư mục feed.' }
            $directory = $header.Substring($root.Length).TrimStart('/')
        }
        elseif (-not $line.EndsWith('/')) { [void]$names.Add($(if ($directory) { "$directory/$line" } else { $line })) }
    }
    return ,$names
}
function SendArchive([string]$archive, [string]$command) {
    Display $ssh ($sshOptions + @($VpsHost, $command))
    Write-Host "Binary stdin <- $archive (FileStream -> SSH StandardInput.BaseStream)"
    if ($WhatIf) { return }
    Pace
    # Windows PowerShell 5.1 opens StandardInput with Console.InputEncoding. On code page 65001 that is UTF-8 with a BOM,
    # written before the archive bytes, and the remote tar rejects the stream ("does not look like a tar archive", 2026-11-20).
    try { [Console]::InputEncoding = New-Object Text.UTF8Encoding $false } catch { Write-Host "Không đặt được InputEncoding: $($_.Exception.Message)" }
    $start = [Diagnostics.ProcessStartInfo]::new($ssh)
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Arguments = (($sshOptions + @($VpsHost, $command) | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }) -join ' ')
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $copyError = $null
    try { $inputFile = [IO.File]::OpenRead($archive); try { $inputFile.CopyTo($process.StandardInput.BaseStream) } finally { $inputFile.Dispose() } }
    catch { $copyError = $_ }
    finally { $process.StandardInput.Close() }
    $process.WaitForExit()
    $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText("$art/logs/$([IO.Path]::GetFileNameWithoutExtension($archive))-extract.log", $out + $err)
    if ($process.ExitCode -ne 0 -or $copyError) { throw "SSH/tar thất bại ($($process.ExitCode)): $err $copyError" }
}
if ($WhatIf) { $art = '<new-deploy-artifact>' }
else {
    $description = if ($PageOnly) { 'Update history HTML only; signed release unchanged' } else { 'U2 update upload and verification' }
    $art = & "$PSScriptRoot/new-artifact.ps1" -Description $description -Slug 'deploy-update' -OutputRoot $OutputRoot
    New-Item -ItemType Directory -Force "$art/logs", "$art/transfer" | Out-Null
}
if ($PageOnly) {
    $page = if ($PageFile) { [IO.Path]::GetFullPath($PageFile) } else { Join-Path $FeedDir 'index.html' }
    if (-not (Test-Path -LiteralPath $page -PathType Leaf)) { throw 'Không tìm thấy HTML để phát hành.' }
    $expected = (Get-FileHash -LiteralPath "$FeedDir/latest.json" -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedSignature = (Get-FileHash -LiteralPath "$FeedDir/latest.json.sig" -Algorithm SHA256).Hash.ToLowerInvariant()
    $pageHash = (Get-FileHash -LiteralPath $page -Algorithm SHA256).Hash.ToLowerInvariant()
    $feedUrl = 'https://inventory.acnos.store/updates/audio-inspector/'
    $temporary = "$RemoteDir/.index-page.$([Guid]::NewGuid().ToString('N')).html"
    # Refuse stale history at the origin immediately before publishing; verify transfer bytes too.
    $promote = "echo '$expected  $RemoteDir/latest.json' | sha256sum -c - && echo '$expectedSignature  $RemoteDir/latest.json.sig' | sha256sum -c - && echo '$pageHash  $temporary' | sha256sum -c - && mv -f -- '$temporary' '$RemoteDir/index.html'"
    if ($WhatIf) {
        Write-Output 'Verify live manifest/signature against cached feed before and after publishing HTML.'
        External $scp ($sshOptions + @($page, "${VpsHost}:$temporary"))
        External $ssh ($sshOptions + @($VpsHost, $promote))
        Write-Output 'GET /update; compare exact HTML SHA-256. No blob, ZIP, archive or latest upload.'
        return
    }
    function AssertLiveRelease([string]$phase) {
        Invoke-WebRequest -Uri ($feedUrl + 'latest.json') -OutFile "$art/$phase-latest.json" -UseBasicParsing
        Invoke-WebRequest -Uri ($feedUrl + 'latest.json.sig') -OutFile "$art/$phase-latest.json.sig" -UseBasicParsing
        if ((Get-FileHash -LiteralPath "$art/$phase-latest.json").Hash.ToLowerInvariant() -ne $expected -or
            (Get-FileHash -LiteralPath "$art/$phase-latest.json.sig").Hash.ToLowerInvariant() -ne $expectedSignature) {
            throw 'Release live không khớp feed nguồn. Tạo lại trang từ release hiện tại; không ghi đè trang lịch sử cũ.'
        }
    }
    AssertLiveRelease 'before'
    Invoke-WebRequest -Uri 'https://inventory.acnos.store/update' -MaximumRedirection 5 -OutFile "$art/history-before.html" -UseBasicParsing
    External $scp ($sshOptions + @($page, "${VpsHost}:$temporary"))
    External $ssh ($sshOptions + @($VpsHost, $promote))
    Invoke-WebRequest -Uri 'https://inventory.acnos.store/update' -MaximumRedirection 5 -OutFile "$art/history-after.html" -UseBasicParsing
    if ((Get-FileHash -LiteralPath "$art/history-after.html").Hash.ToLowerInvariant() -ne $pageHash) {
        # Cloudflare may append its beacon and JS-detection loader to public HTML.
        # Allow those identified additions and inter-tag whitespace; origin bytes above
        # are still checked exactly before rename.
        function CanonicalPage([string]$content) {
            $content = [regex]::Replace($content, '(?is)<script\b(?=[^>]*\bsrc="https://static\.cloudflareinsights\.com/beacon\.min\.js/)[^>]*>\s*</script>', '')
            $content = [regex]::Replace($content, '(?is)<script>(?=(?:(?!</script>).)*window\.__CF\$cv\$params)(?=(?:(?!</script>).)*/cdn-cgi/challenge-platform/scripts/jsd/main\.js\?ray=)(?:(?!</script>).)*</script>', '')
            return [regex]::Replace($content, '>\s+<', '><').Trim()
        }
        if ((CanonicalPage ([IO.File]::ReadAllText("$art/history-after.html"))) -cne (CanonicalPage ([IO.File]::ReadAllText($page)))) {
            throw 'HTML public khác bản đã tạo, kể cả sau khi bỏ các script Cloudflare đã biết.'
        }
        Write-Output 'PASS public HTML matches after known Cloudflare script normalization; origin transfer hash checked exactly.'
    }
    AssertLiveRelease 'after'
    Write-Output "PASS page-only origin HTML SHA-256: $pageHash; public content verified."
    Write-Output "PASS release manifest/signature unchanged: $expected"
    return
}
$feedUrl = 'https://inventory.acnos.store/updates/audio-inspector/'
$localLatestHash = (Get-FileHash -LiteralPath "$FeedDir/latest.json" -Algorithm SHA256).Hash
foreach ($required in @('versions.json', 'versions.json.sig')) { if (-not (Test-Path -LiteralPath "$FeedDir/$required" -PathType Leaf)) { throw "Feed thiếu $required; build lại bằng UpdateFeedTool mới." } }
if ($Channel -eq 'beta') {
    foreach ($required in @('beta.json', 'beta.json.sig')) { if (-not (Test-Path -LiteralPath "$FeedDir/$required" -PathType Leaf)) { throw "Feed beta thiếu $required." } }
    # A beta must leave the live stable pointer exactly as it is. Refuse before uploading anything if live moved on.
    if ($WhatIf) { Write-Output "GET ${feedUrl}latest.json; must equal $FeedDir/latest.json (SHA-256 $localLatestHash) before any upload." }
    else {
        Invoke-WebRequest -Uri ($feedUrl + 'latest.json') -OutFile "$art/live-latest-before.json" -UseBasicParsing
        if ((Get-FileHash -LiteralPath "$art/live-latest-before.json" -Algorithm SHA256).Hash -ne $localLatestHash) {
            throw 'latest.json live khác latest.json trong feed beta (có bản ổn định mới?). Build lại beta với -Previous là feed live hiện tại.'
        }
        Write-Output "PASS live latest.json khớp feed beta trước khi tải: $localLatestHash"
    }
}
External $ssh ($sshOptions + @($VpsHost, "mkdir -p -- '$RemoteDir/files' '$RemoteDir/versions' '$RemoteDir/packages'"))
foreach ($directory in @('files','versions','packages')) {
    $remoteRoot = "$RemoteDir/$directory"
    $listingCommand = "ls -1R -p -- '$remoteRoot'"
    Display $ssh ($sshOptions + @($VpsHost, $listingCommand))
    if ($WhatIf) { $existing = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    else {
        Pace
        $lines = @(& $ssh @sshOptions $VpsHost $listingCommand)
        if ($LASTEXITCODE -ne 0) { throw "Không liệt kê được $remoteRoot" }
        $existing = ParseInventory $lines $remoteRoot
        [IO.File]::WriteAllLines("$art/transfer/$directory-existing.txt", [string[]]$lines)
    }
    $localRoot = Join-Path $FeedDir $directory
    $prefix = $localRoot + [IO.Path]::DirectorySeparatorChar
    $missing = @([IO.Directory]::EnumerateFiles($localRoot, '*', [IO.SearchOption]::AllDirectories) | ForEach-Object {
        $relative = $_.Substring($prefix.Length).Replace('\','/')
        if (-not $existing.Contains($relative)) { "$directory/$relative" }
    } | Sort-Object)
    Write-Host "$directory : $($missing.Count) local candidates; WhatIf assumes all missing, U2b computes the exact remote difference."
    if ($missing.Count -eq 0) { continue }
    $list = "$art/transfer/$directory-missing.nul"; $archive = "$art/transfer/$directory.tar"
    if (-not $WhatIf) { [IO.File]::WriteAllText($list, ($missing -join [char]0) + [char]0, [Text.UTF8Encoding]::new($false)) }
    External $tar @('-cf', $archive, '-C', $FeedDir, '--null', '-T', $list)
    SendArchive $archive "tar -xf - -C '$RemoteDir' --skip-old-files --no-same-owner --no-same-permissions"
}
$suffix = [Guid]::NewGuid().ToString('N')
# Signature first, then manifest: a client that sees the new manifest always finds its signature.
function Promote([string]$name) {
    External $scp ($sshOptions + @("$FeedDir/$name.sig", "${VpsHost}:$RemoteDir/.$name.sig.$suffix"))
    External $scp ($sshOptions + @("$FeedDir/$name", "${VpsHost}:$RemoteDir/.$name.$suffix"))
    External $ssh ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.$name.sig.$suffix' '$RemoteDir/$name.sig' && mv -f -- '$RemoteDir/.$name.$suffix' '$RemoteDir/$name'"))
}
# The catalog only lists archives that are already uploaded above.
Promote 'versions.json'
if ($Channel -eq 'beta') { Promote 'beta.json' }
else {
    Promote 'latest.json'
    if (Test-Path -LiteralPath "$FeedDir/beta.json" -PathType Leaf) { Promote 'beta.json' }
}
External $scp ($sshOptions + @("$FeedDir/index.html", "${VpsHost}:$RemoteDir/.index.html.$suffix"))
External $ssh ($sshOptions + @($VpsHost, "mv -f -- '$RemoteDir/.index.html.$suffix' '$RemoteDir/index.html'"))
$pointer = if ($Channel -eq 'beta') { 'beta.json' } else { 'latest.json' }
if ($WhatIf) {
    Write-Output ("GET ${feedUrl}latest.json; must equal $FeedDir/latest.json" + $(if ($Channel -eq 'beta') { ' (unchanged by this beta)' } else { '' }))
    Write-Output "GET ${feedUrl}$pointer and ${feedUrl}versions.json; compare SHA-256 with $FeedDir"
    Write-Output 'GET https://inventory.acnos.store/update (follow redirect); check version'; return
}
foreach ($name in @('latest.json', $pointer, 'versions.json') | Select-Object -Unique) {
    Invoke-WebRequest -Uri ($feedUrl + $name) -OutFile "$art/$name.downloaded" -UseBasicParsing
    $localHash = (Get-FileHash -LiteralPath "$FeedDir/$name" -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath "$art/$name.downloaded" -Algorithm SHA256).Hash -ne $localHash) { throw "SHA-256 $name trên server khác bản local." }
    Write-Output "PASS remote $name SHA-256: $localHash"
}
if ($Channel -eq 'beta') { Write-Output "PASS latest.json live vẫn là bản ổn định cũ ($localLatestHash); máy kênh Ổn định không được mời cài beta." }
$expected = ([IO.File]::ReadAllText("$FeedDir/$pointer") | ConvertFrom-Json).version
Invoke-WebRequest -Uri 'https://inventory.acnos.store/update' -MaximumRedirection 5 -OutFile "$art/history-downloaded.html" -UseBasicParsing
$history = [IO.File]::ReadAllText("$art/history-downloaded.html")
if (-not $history.Contains("data-version=`"$expected`"")) { throw 'Trang /update không có phiên bản vừa phát hành.' }
if ($Channel -eq 'beta' -and -not $history.Contains("data-version=`"$expected`" data-channel=`"beta`"")) { throw 'Trang /update chưa đánh dấu BETA cho phiên bản vừa phát hành.' }
Write-Output ("PASS /update followed redirect and contains version $expected" + $(if ($Channel -eq 'beta') { ' (BETA)' } else { '' }))
