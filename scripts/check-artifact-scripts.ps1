param(
    # New directory inside an artifact, e.g. <artifact>/checks/artifact-scripts. Must not exist.
    [Parameter(Mandatory)][string]$Output
)
# Exercises scripts/new-artifact.ps1 and scripts/cleanup-artifacts.ps1 inside a throw-away git repository, so the
# retention rules (ARTIFACTS.md) are proven without touching this repository's artifacts. cleanup-artifacts.ps1
# deletes directories recursively; these checks pin what it may delete and what it must refuse.
# Runs under the calling PowerShell edition; run-offline-checks.ps1 calls it with powershell.exe and pwsh.
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Output must be new.' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$passed = New-Object Collections.Generic.List[string]
function Check([bool]$condition, [string]$text) {
    if (-not $condition) { throw "FAIL $text" }
    $passed.Add("PASS $text"); Write-Output "PASS $text"
    [IO.File]::WriteAllLines((Join-Path $Output 'checks.txt'), $passed)
}
function Rejects([scriptblock]$action, [string]$pattern, [string]$text) {
    $message = $null
    try { & $action | Out-Null } catch { $message = $_.Exception.Message }
    Check ($null -ne $message -and $message -match $pattern) "$text (refused: $message)"
}
function Invoke-Git([string]$directory, [string[]]$arguments) {
    # Not named "git": a function of that name in PowerShell captures recursive calls to git instead of git.exe.
    $oldEap = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        $result = & git.exe -c core.autocrlf=false -C $directory @arguments 2>&1
    }
    finally { $ErrorActionPreference = $oldEap }
    if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed: $result" }
    return $result
}
function Tree([string]$root, [string]$relative, [datetime]$writtenUtc, [string]$createdUtcText) {
    # A candidate directory with one payload file; optional artifact.json carrying createdUtc exactly as given.
    $directory = Join-Path $root $relative
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $payload = Join-Path $directory 'payload.bin'
    [IO.File]::WriteAllBytes($payload, [byte[]](1..64))
    if ($createdUtcText) {
        [IO.File]::WriteAllText((Join-Path $directory 'artifact.json'), "{ `"createdUtc`": `"$createdUtcText`", `"description`": `"sandbox`" }")
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Force) {
        for ($i = 0; $i -lt 5; $i++) {
            try { $file.LastWriteTimeUtc = $writtenUtc; break }
            catch { Start-Sleep -Milliseconds 50; if ($i -eq 4) { throw } }
        }
    }
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $item = Get-Item -LiteralPath $directory -Force
            $item.CreationTimeUtc = $writtenUtc; $item.LastWriteTimeUtc = $writtenUtc
            break
        }
        catch { Start-Sleep -Milliseconds 50; if ($i -eq 4) { throw } }
    }
    return $directory
}

# --- sandbox repository -------------------------------------------------------------------------------------
$sandbox = Join-Path $Output 'repo'
New-Item -ItemType Directory -Path (Join-Path $sandbox 'scripts') -Force | Out-Null
foreach ($name in @('new-artifact.ps1', 'cleanup-artifacts.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $sandbox "scripts/$name") }
[IO.File]::WriteAllText((Join-Path $sandbox '.gitignore'), "artifacts/`n.artifacts/`n.codex-artifacts/`ndemo-builds/`nbin-*/`nobj-*/`n.codex-build/`nhardware-channel-fix/`nlinked/`n")
[IO.File]::WriteAllText((Join-Path $sandbox 'README.md'), "sandbox`n")
Invoke-Git $sandbox @('init', '--quiet') | Out-Null
Invoke-Git $sandbox @('add', '-A') | Out-Null
Invoke-Git $sandbox @('-c', 'user.name=sandbox', '-c', 'user.email=sandbox@invalid', 'commit', '--quiet', '-m', 'sandbox') | Out-Null
$head = "$(Invoke-Git $sandbox @('rev-parse', 'HEAD'))".Trim()
$new = Join-Path $sandbox 'scripts/new-artifact.ps1'
$cleanup = Join-Path $sandbox 'scripts/cleanup-artifacts.ps1'
$now = [DateTime]::UtcNow

# --- new-artifact.ps1 ---------------------------------------------------------------------------------------
$first = & $new -Description 'Sandbox artifact' -Slug 'sandbox-one'
$name = Split-Path -Leaf $first
Check ((Split-Path -Parent $first) -eq (Join-Path $sandbox 'artifacts') -and $name -match '^\d{8}-\d{6}-\d{3}Z-sandbox-one$') "artifact path is artifacts/<UTC yyyyMMdd-HHmmss-fffZ>-<slug>: $name"
$metaText = [IO.File]::ReadAllText((Join-Path $first 'artifact.json'))
$createdText = [regex]::Match($metaText, '"createdUtc"\s*:\s*"([^"]+)"').Groups[1].Value
$created = [DateTimeOffset]::Parse($createdText, [Globalization.CultureInfo]::InvariantCulture)
$stamp = [DateTime]::ParseExact($name.Substring(0, 20), "yyyyMMdd-HHmmss-fff'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
Check ($created.Offset -eq [TimeSpan]::Zero -and [Math]::Abs(($created.UtcDateTime - $stamp).TotalMilliseconds) -lt 2 -and [Math]::Abs(($created.UtcDateTime - $now).TotalMinutes) -lt 5) "artifact.json createdUtc is ISO 8601 UTC and equals the folder timestamp ($createdText)"
Check ($metaText -match '"sourceCommit"\s*:\s*"' + $head + '"' -and $metaText -match '"description"\s*:\s*"Sandbox artifact"' -and $metaText -match '"workingTreeDirty"\s*:\s*false') 'artifact.json records description, HEAD commit and a clean working tree'
$notes = [IO.File]::ReadAllText((Join-Path $first 'ARTIFACT.md'))
Check ($notes.Contains('# Sandbox artifact') -and $notes.Contains("Source commit: $head") -and $notes.Contains('Validation: pending')) 'ARTIFACT.md carries description, commit and the validation placeholder to be replaced'
[IO.File]::WriteAllText((Join-Path $sandbox 'untracked.txt'), 'x')
$second = & $new -Description 'Dirty tree' -Slug 'sandbox-two'
Check ([IO.File]::ReadAllText((Join-Path $second 'artifact.json')) -match '"workingTreeDirty"\s*:\s*true' -and $second -ne $first) 'untracked file marks the tree dirty; every call creates a new directory'
Remove-Item -LiteralPath (Join-Path $sandbox 'untracked.txt')
foreach ($slug in @('Bad_Slug', 'UPPER', '-lead', 'trail-', 'two--dashes', 'has space')) {
    Rejects { & $new -Description 'x' -Slug $slug } 'Slug|pattern|validat' "slug '$slug' rejected"
}
Rejects { & $new -Description 'x' -Slug 'escape' -OutputRoot '..\outside' } 'inside the repository' 'output root outside the repository rejected'
Check (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $sandbox) 'outside'))) 'nothing was created outside the sandbox repository'
$target = Join-Path $Output 'junction-target'
New-Item -ItemType Directory -Path $target | Out-Null
New-Item -ItemType Junction -Path (Join-Path $sandbox 'linked') -Target $target | Out-Null
Rejects { & $new -Description 'x' -Slug 'through-link' -OutputRoot 'linked' } 'Reparse point' 'output root that is a junction rejected'
Check (@(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) 'junction target left empty'
# cmd rmdir removes the junction itself, never the target's content.
& cmd /c rmdir (Join-Path $sandbox 'linked') | Out-Null

# --- cleanup-artifacts.ps1: inventory ------------------------------------------------------------------------
$old = $now.AddDays(-30)
$expiredManifest = Tree $sandbox 'artifacts/20200101-000000-000Z-expired' $now $now.AddDays(-30).ToString('o')
$expiredFreshFiles = Tree $sandbox 'artifacts/20200102-000000-000Z-expired-fresh-files' $now $now.AddDays(-8).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'")
$keptManifest = Tree $sandbox 'artifacts/20990101-000000-000Z-kept' $old $now.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'")
$boundaryKeep = Tree $sandbox 'artifacts/boundary-keep' $old $now.AddDays(-7).AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'")
$boundaryDelete = Tree $sandbox 'artifacts/boundary-delete' $old $now.AddDays(-7).AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'")
# The same instants written with a Z suffix (hand-written metadata) must classify identically.
$zuluKeep = Tree $sandbox 'artifacts/zulu-keep' $old $now.AddDays(-7).AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")
$zuluDelete = Tree $sandbox 'artifacts/zulu-delete' $old $now.AddDays(-7).AddHours(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")
$legacyOld = Tree $sandbox 'artifacts/legacy-old' $old $null
$legacyRecent = Tree $sandbox 'artifacts/legacy-recent-file' $old $null
(Get-Item -LiteralPath (Join-Path $legacyRecent 'payload.bin')).LastWriteTimeUtc = $now.AddDays(-2)
$dotOld = Tree $sandbox '.artifacts/old-build' $old $null
$codexOld = Tree $sandbox '.codex-artifacts/old-run' $old $null
$demoOld = Tree $sandbox 'demo-builds/old-demo' $old $null
$rootBin = Tree $sandbox 'bin-old' $old $null
$rootObj = Tree $sandbox 'obj-old' $old $null
$rootCodex = Tree $sandbox '.codex-build' $old $null
$rootHardware = Tree $sandbox 'hardware-channel-fix' $old $null
$protected = @('standards', 'save standards', 'calibrations', 'src', 'docs', 'bin', 'obj', 'binary-notes', 'local-data') | ForEach-Object { Tree $sandbox $_ $old $null }
$expectDelete = @($expiredManifest, $expiredFreshFiles, $boundaryDelete, $zuluDelete, $legacyOld, $dotOld, $codexOld, $demoOld, $rootBin, $rootObj, $rootCodex, $rootHardware)
$expectKeep = @($keptManifest, $boundaryKeep, $zuluKeep, $legacyRecent, $first, $second)

function Inventory([string]$reportLine) {
    $report = ($reportLine -replace '^Report:\s*', '').Trim()
    $text = [IO.File]::ReadAllText((Join-Path $report 'inventory.json'))
    $rows = @{}
    foreach ($match in [regex]::Matches($text, '(?s)\{[^{}]*\}')) {
        $path = [regex]::Match($match.Value, '"path"\s*:\s*"((?:[^"\\]|\\.)*)"').Groups[1].Value.Replace('\\', '\')
        $rows[$path] = [pscustomobject]@{
            action = [regex]::Match($match.Value, '"action"\s*:\s*"(\w+)"').Groups[1].Value
            basis = [regex]::Match($match.Value, '"basis"\s*:\s*"([^"]+)"').Groups[1].Value
            deleted = [regex]::Match($match.Value, '"deleted"\s*:\s*(true|false)').Groups[1].Value -eq 'true'
        }
    }
    return [pscustomobject]@{ Report = $report; Rows = $rows }
}
$dry = @(& $cleanup)
$plan = Inventory ($dry | Where-Object { $_ -like 'Report:*' } | Select-Object -Last 1)
Check (@($expectDelete + $expectKeep + $protected | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0) 'preview run (no -Apply) deleted nothing'
$wrongDelete = @($expectDelete | Where-Object { -not $plan.Rows.ContainsKey($_) -or $plan.Rows[$_].action -ne 'delete' })
Check ($wrongDelete.Count -eq 0) "expired candidates planned for deletion: manifest date wins over fresh files, legacy by newest write, root bin-/obj-/.codex-build/hardware-channel-fix ($($expectDelete.Count)); wrong: $($wrongDelete -join ', ')"
$wrongKeep = @($expectKeep | Where-Object { -not $plan.Rows.ContainsKey($_) -or $plan.Rows[$_].action -ne 'keep' })
Check ($wrongKeep.Count -eq 0) "recent candidates kept: manifest date wins over old files, legacy with one recent file, the two new artifacts ($($expectKeep.Count)); wrong: $($wrongKeep -join ', ')"
Check ($plan.Rows[$boundaryKeep].action -eq 'keep' -and $plan.Rows[$boundaryDelete].action -eq 'delete') 'retention boundary is 7 x 24 h on createdUtc: 6 d 23 h kept, 7 d 1 h expired'
Check ($plan.Rows[$zuluKeep].action -eq 'keep' -and $plan.Rows[$zuluDelete].action -eq 'delete') 'createdUtc written with a Z suffix is read as UTC, not as local time'
Check ($plan.Rows[$expiredManifest].basis -eq 'manifest-createdUtc' -and $plan.Rows[$legacyOld].basis -like 'legacy*') 'inventory states the basis of every timestamp'
$leaked = @($protected | Where-Object { $plan.Rows.ContainsKey($_) })
$outsideSandbox = @($plan.Rows.Keys | Where-Object { -not $_.StartsWith($sandbox + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
Check ($leaked.Count -eq 0 -and $outsideSandbox.Count -eq 0) 'standards, save standards, calibrations, src, docs, bin, obj and look-alike names are never candidates; no path outside the repository'
Check ($plan.Rows.ContainsKey($plan.Report) -and $plan.Rows[$plan.Report].action -eq 'keep' -and ($dry -join ' ') -match 'apply=False') 'the cleanup report is itself a kept artifact and states apply=False'
Rejects { & $cleanup -KeepDays 0 } 'KeepDays|range|validat' 'KeepDays 0 rejected'

# --- cleanup-artifacts.ps1: a junction inside a candidate stops everything ----------------------------------
$sentinelRoot = Join-Path $Output 'outside-data'
New-Item -ItemType Directory -Path $sentinelRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $sentinelRoot 'sentinel.txt'), 'must survive')
$trap = Tree $sandbox 'artifacts/20200103-000000-000Z-expired-with-junction' $old $now.AddDays(-30).ToString('o')
New-Item -ItemType Junction -Path (Join-Path $trap 'link') -Target $sentinelRoot | Out-Null
Rejects { & $cleanup -Apply } 'reparse point' 'expired artifact containing a junction aborts the whole cleanup'
Check ((Test-Path -LiteralPath (Join-Path $sentinelRoot 'sentinel.txt')) -and @($expectDelete | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -eq 0) 'abort happened before any deletion; data behind the junction untouched'
& cmd /c rmdir (Join-Path $trap 'link') | Out-Null
$expectDelete += $trap

# --- cleanup-artifacts.ps1: apply -----------------------------------------------------------------------------
$applied = @(& $cleanup -Apply)
$done = Inventory ($applied | Where-Object { $_ -like 'Report:*' } | Select-Object -Last 1)
$survivors = @($expectDelete | Where-Object { Test-Path -LiteralPath $_ })
Check ($survivors.Count -eq 0) "-Apply removed every expired candidate ($($expectDelete.Count)); left: $($survivors -join ', ')"
$lost = @($expectKeep + $protected + @($plan.Report) | Where-Object { -not (Test-Path -LiteralPath (Join-Path $_ 'payload.bin')) -and -not (Test-Path -LiteralPath (Join-Path $_ 'artifact.json')) })
Check ($lost.Count -eq 0) "-Apply left kept artifacts, the earlier report and protected folders intact; lost: $($lost -join ', ')"
Check (@($expectDelete | Where-Object { -not $done.Rows[$_].deleted }).Count -eq 0 -and @($expectKeep | Where-Object { $done.Rows[$_].deleted }).Count -eq 0 -and ($applied -join ' ') -match "apply=True; expired=$($expectDelete.Count);") 'inventory.json and summary record exactly what was deleted'
Check ((Test-Path -LiteralPath (Join-Path $sentinelRoot 'sentinel.txt')) -and [IO.File]::ReadAllText((Join-Path $sandbox 'README.md')) -eq "sandbox`n" -and "$(Invoke-Git $sandbox @('status', '--porcelain', '-uno'))" -eq '') 'tracked files and data outside the repository unchanged'
$again = @(& $cleanup -Apply)
Check (($again -join ' ') -match 'expired=0;.*deleted=0; failed=0') 'second -Apply finds nothing to delete'

Write-Output "ALL $($passed.Count) ARTIFACT SCRIPT CHECKS PASSED (PowerShell $($PSVersionTable.PSVersion); sandbox $sandbox)"
