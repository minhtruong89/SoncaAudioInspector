param(
    # Reuse an artifact created by scripts/new-artifact.ps1. Default: create a new one.
    [string]$Artifact,
    # Check ids or group names to run (see -List). Default: every check that is not slow or quarantined.
    [string[]]$Only,
    [string[]]$Skip,
    # Print the catalogue (id, group, what it proves) and exit without building.
    [switch]$List,
    # Reuse binaries already present in <Artifact>/build.
    [switch]$SkipBuild,
    # Also run checks tagged slow.
    [switch]$IncludeSlow,
    # Also run quarantined checks (known to fail at the commit named in their note).
    [switch]$IncludeQuarantined,
    [ValidateRange(30, 3600)][int]$TimeoutSeconds = 600,
    [string]$OutputRoot = 'artifacts'
)
# Offline regression suite: builds the app once into an artifact, builds every check harness against that exact
# build and runs the modes that need no audio device, BLE radio, network, server or operator.
# Evidence level of a green run: build + synthetic/recorded-fixture logic. It is NOT a hardware, live-server or
# REW-equivalence result (see AGENTS.md and AUTO_TEST_PERFORMANCE.vi.md).
# Windows PowerShell 5.1 compatible (agents run powershell.exe).
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
# powershell.exe -File passes comma-separated values as one argument, unlike a PowerShell invocation.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# id; group; tool ('' = PowerShell script); arguments ({art} {app} {bin:<Tool>} {repo} are expanded); note.
# needsApp: the harness is compiled against the app assembly through -p:AudioAssemblyPath.
$catalogue = @(
    @{ id = 'source-layout';        group = 'scripts'; script = 'check-source-layout.ps1'; args = @();
       note = 'Compile/Page items, co-located partials, startup URI, linked test sources exist' }
    @{ id = 'skills';               group = 'scripts'; script = 'check-skills.ps1'; args = @();
       note = 'Agent skills: front matter, links, script parameters and harness flags they name really exist' }
    @{ id = 'artifact-scripts';     group = 'scripts'; script = 'check-artifact-scripts.ps1'; args = @('-Output', '{art}/checks/artifact-scripts'); hosts = 'both';
       note = 'new-artifact/cleanup-artifacts in a sandbox repo: metadata, retention boundary, protected folders, junction refusal' }

    @{ id = 'audio-logic';          group = 'audio';   tool = 'AudioLifecycleCheck'; args = @('--logic-only');
       note = 'Reference selection, fresh capture window, response level, sweep amplitude, scope waveform/WAV' }
    @{ id = 'login-owner-guard';    group = 'audio';   tool = 'AudioLifecycleCheck'; args = @('--login-owner-guard');
       note = 'Owned device dialog is deferred until MainWindow is shown' }
    @{ id = 'fasttrack-logic';      group = 'audio';   tool = 'AudioLifecycleCheck'; args = @('--fasttrack-logic');
       note = 'FastTrack reconnect decision logic (no device access)' }
    @{ id = 'clock-equivalence';    group = 'measure'; tool = 'AudioLifecycleCheck'; args = @('--clock-equivalence');
       note = 'Clock-drift correction result hash on synthetic sweep' }
    @{ id = 'sweep-equivalence';    group = 'measure'; tool = 'AudioLifecycleCheck'; args = @('--sweep-equivalence');
       note = '512K sweep analysis hashes: clean, clipping, silence, noise' }
    @{ id = 'qa-graph-storage';     group = 'audio';   tool = 'AudioLifecycleCheck'; args = @('--qa-graph-storage={art}/checks/qa-graph');
       note = 'Pending/readback/completion lifecycle of QA graphs; path traversal rejected' }
    @{ id = 'update-logic';         group = 'update';  tool = 'AudioLifecycleCheck'; args = @('--update-logic', '--output={art}/checks/update-logic', '--feed-tool={bin:UpdateFeedTool}'); needs = @('UpdateFeedTool');
       note = 'Signed feed, delta install, rollback, history page, release-notes encoding guard (test keys, no network)' }
    @{ id = 'server-config';        group = 'config';  tool = 'ServerConfigCheck'; args = @('{art}/checks/server');
       note = 'Mandatory signed station config: online/offline/rollback/tamper (fake server, test keys)' }
    @{ id = 'config-acceptance';    group = 'config';  tool = 'ConfigAcceptanceCheck'; needsApp = $true; args = @('{app}', '{art}/checks/config');
       note = 'Model editor, criteria, approval workflow and WPF config screens (fake server)' }
    @{ id = 'rubbuzz';              group = 'rubbuzz'; tool = 'PinkRubBuzzCheck'; needsApp = $true; args = @('{app}', '{art}/checks/rubbuzz');
       note = 'Pink/chirp rattle analysis on synthetic and recorded UMIK-1 fixtures, merged tab render' }
    @{ id = 'c360-sequence';        group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--c360-sequence-logic');
       note = 'C360 stage order, 3/2 voting, restore, acoustic draft gate (no radio, no audio)' }
    @{ id = 'c360-v152';            group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--c360-v152-logic={repo}/tools/AudioLifecycleCheck/Fixtures/C360-rev3.json', '--output={art}/checks/c360-v152');
       note = 'v1.5.3: IO008 Rè stage, A/B no-signal retry, auto LINE CHUẨN, FBX note, 2/1 runs, port report, screen fit' }
    @{ id = 'c360-re-probe';        group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--c360-re-probe={repo}/tools/AudioLifecycleCheck/Fixtures/C360-rev5.json', '--output={art}/checks/c360-re-probe');
       note = 'C360 two-pair wiring probe, IO008 route and limits, chirp graph validity and WASAPI retry (offline)' }
    @{ id = 'c360-verdict';         group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--c360-verdict-logic', '--output={art}/checks/c360-verdict');
       note = 'A1-A7: FAIL+INVALID immediate upload, PENDING prefixes, gray optional IO009, fault notes, INVALID row retest + same uploadSessionId, off-thread QA snapshot, THD-decoupled auto LINE CHUẨN, popup guard' }
    @{ id = 'acoustic3-logic';      group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--acoustic3-model-logic');
       note = 'ACOUSTIC 3 model factory, firmware identity guard, mapped controls' }
    @{ id = 'frequency-shift';      group = 'c360';    tool = 'AudioLifecycleCheck'; args = @('--frequency-shift-logic');
       note = 'FBX frequency-shift estimator on synthetic carriers' }
    @{ id = 'ble-logic';            group = 'ble';     tool = 'AudioLifecycleCheck'; args = @('--ble-logic');
       note = 'Sonca BLE protocol decoding from captured frames (no radio)' }
    @{ id = 'ble-model-names';      group = 'ble';     tool = 'AudioLifecycleCheck'; args = @('--ble-model-names');
       note = 'Firmware model catalogue and name filtering (no radio)' }
    @{ id = 'bt-playback';          group = 'ble';     tool = 'AudioLifecycleCheck'; args = @('--bluetooth-playback-logic');
       note = 'Bluetooth render endpoint matching by address (recorded PnP paths)' }
    @{ id = 'bt-removal';           group = 'ble';     tool = 'AudioLifecycleCheck'; args = @('--bluetooth-removal-logic');
       note = 'Exact-address Bluetooth removal workflow (no Windows unpair)' }
    @{ id = 'brief-p1';             group = 'policy';  tool = 'AudioLifecycleCheck'; args = @('--brief-phase=P1', '--output={art}/checks/p1');
       note = 'Response limits and dip windows (P1)' }
    @{ id = 'brief-p2';             group = 'policy';  tool = 'AudioLifecycleCheck'; args = @('--brief-phase=P2', '--output={art}/checks/p2', '--config={app}/checking_config.json');
       note = 'Test reordering and reference identity (P2) on the checked-in models' }
    @{ id = 'brief-p6';             group = 'policy';  tool = 'AudioLifecycleCheck'; args = @('--brief-phase=P6', '--output={art}/checks/alc-P6'); slow = $true;
       note = 'IO001 gate vs IO003 survey (P6), about one minute' }

    @{ id = 'ui-version';           group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--brief-phase=U1', '--output={art}/checks/version-ui');
       note = 'Version tab and release notes view' }
    @{ id = 'ui-version-header';    group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--update-version', '--version-xaml={repo}/src/UI/Shell/MainWindow.xaml', '--output={art}/checks/version-header');
       note = 'Header label is bound to AppVersion.Display and shows the built version' }
    @{ id = 'ui-p1';                group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--brief-phase=P1', '--output={art}/checks/ui-p1');
       note = 'P1 WPF assertions and QA spectrum PNG' }
    @{ id = 'ui-p2';                group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--brief-phase=P2', '--output={art}/checks/ui-p2');
       note = 'P2 WPF assertions and preview PNG' }
    @{ id = 'ui-impedance';         group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--impedance-ui', '{art}/checks/ui-impedance');
       note = 'Manual impedance tab with injected capture' }
    @{ id = 'ui-ble-options';       group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--ble-options-ui');
       note = 'BLE options with mock advertisements and injected state' }
    @{ id = 'ui-ble-model-names';   group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--ble-model-names-ui', '{art}/checks/ui-ble-model-names');
       note = 'Model filter modes, zero hardware packets' }
    @{ id = 'ui-ble-bt-audio';      group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--ble-bluetooth-audio-ui', '{art}/checks/ui-ble-bt-audio');
       note = 'BLE-ready Bluetooth audio connection with injected Windows connector' }
    @{ id = 'ui-response-db-labels'; group = 'ui';     tool = 'FeedbackUiCheck'; args = @('{app}', '--response-db-labels', '{art}/checks/ui-db-labels');
       note = 'Adaptive dB tick labels' }
    @{ id = 'ui-source-layout';     group = 'ui';      tool = 'FeedbackUiCheck'; args = @('{app}', '--source-layout-ui', '{art}/checks/ui-source-layout');
       note = 'Relocated Splash/Login XAML and themes load' }

    @{ id = 'std-default';          group = 'measure'; tool = 'StandardMeasurementCheck'; args = @();
       note = 'ESS generation, deconvolution, response, THD and decay on synthetic systems' }
    @{ id = 'std-sweep-fft';        group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--sweep-fft');
       note = 'FFT backend against MathNet, AVX and scalar' }
    @{ id = 'std-impedance';        group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--impedance');
       note = 'Impedance calculation and calibration validation' }
    @{ id = 'std-reverb-decay';     group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--reverb-decay');
       note = 'Dry/wet reverb decay diagnostics' }
    @{ id = 'std-distortion-audit'; group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--distortion-audit');
       note = 'Distortion audit on synthetic harmonics' }
    @{ id = 'std-distortion-preservation'; group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--distortion-preservation');
       note = 'Distortion numbers preserved across analysis options' }
    @{ id = 'std-impulse-plot';     group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--impulse-plot');
       note = 'Impulse/ETC/Schroeder plot data' }
    @{ id = 'std-impulse-effects';  group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--impulse-effects', '{art}/checks/std-impulse-effects');
       note = 'Echo/reverb classification fixtures' }
    @{ id = 'std-signal-diagnostics'; group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--signal-diagnostics', '{art}/checks/std-signal');
       note = 'Polarity, IMD, compression diagnostics' }
    @{ id = 'std-mechanical';       group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--mechanical', '{art}/checks/std-mechanical');
       note = 'H10-H35, loose particle and air leak on synthetic captures' }
    @{ id = 'std-subwoofer-controls'; group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--subwoofer-controls');
       note = 'Ctrl+Shift+T subwoofer crossover/delay/phase comparison on a simulated sub with latency jitter' }
    @{ id = 'std-rew-regression';   group = 'measure'; tool = 'StandardMeasurementCheck'; args = @('--rew-regression');
       note = 'Regression numbers taken from earlier REW comparison (not a new REW run)' }
    @{ id = 'measurement-check';    group = 'measure'; tool = 'MeasurementCheck'; args = @();
       note = 'Tone analysis and speaker diagnosis wording' }
    @{ id = 'feedback-shift';       group = 'measure'; tool = 'FeedbackShiftCheck'; args = @();
       note = 'Feedback frequency-shift measurement' }
    @{ id = 'pink-noise';           group = 'measure'; tool = 'PinkNoiseCheck'; args = @('{art}/checks/pink-noise');
       note = 'Pink noise generator, WAV export and tab (no playback)' }
    @{ id = 'pink-rubbuzz';         group = 'measure'; tool = 'PinkRubBuzzCheck'; needsApp = $true; args = @('{app}', '{art}/checks/pink-rubbuzz', '{repo}/tools/PinkRubBuzzCheck/Fixtures/pink', '{repo}/tools/PinkRubBuzzCheck/Fixtures/gated', '{repo}/tools/PinkRubBuzzCheck/Fixtures/chirp'); slow = $true; timeout = 600;
       note = 'Pink and chirp DSP, real recorded fixtures, manual orchestration and WPF render (no playback)' }
    @{ id = 'bom-csv';              group = 'config';  tool = 'CheckApp'; args = @();
       note = 'BOM CSV rules' }
)

if ($List) {
    $catalogue | ForEach-Object {
        $tags = @(); if ($_.slow) { $tags += 'slow' }; if ($_.quarantine) { $tags += 'quarantined' }
        [pscustomobject]@{ Id = $_.id; Group = $_.group; Harness = $(if ($_.tool) { $_.tool } else { $_.script }); Tags = ($tags -join ','); Proves = $_.note }
    } | Format-Table -AutoSize -Wrap
    return
}

$selected = @($catalogue | Where-Object {
    $wanted = if ($Only) { $_.id -in $Only -or $_.group -in $Only } else { ($IncludeSlow -or -not $_.slow) -and ($IncludeQuarantined -or -not $_.quarantine) }
    $wanted -and -not ($Skip -and ($_.id -in $Skip -or $_.group -in $Skip))
})
if ($Only) {
    $known = @($catalogue | ForEach-Object { $_.id; $_.group })
    $unknown = @($Only | Where-Object { $_ -notin $known })
    if ($unknown.Count) { throw "Unknown check id/group: $($unknown -join ', '). Run with -List." }
}
if ($selected.Count -eq 0) { throw 'No check selected.' }

if ($Artifact) {
    $art = [IO.Path]::GetFullPath($Artifact)
    if (-not (Test-Path -LiteralPath (Join-Path $art 'artifact.json') -PathType Leaf)) { throw "Not an artifact created by new-artifact.ps1: $art" }
}
else {
    $art = & (Join-Path $PSScriptRoot 'new-artifact.ps1') -Description 'Offline regression checks' -Slug 'offline-checks' -OutputRoot $OutputRoot
}
$logs = Join-Path $art 'logs'
New-Item -ItemType Directory -Force $logs, (Join-Path $art 'checks') | Out-Null
$bin = Join-Path $art 'build/bin'
$app = Join-Path $bin 'SoncaAudioInspector/debug'
$appDll = Join-Path $app 'SoncaAudioInspector.dll'
# Offline means no NuGet endpoint or audit lookup either. Resolve existing references from the local cache only.
$offlineNuget = Join-Path $art 'NuGet.Offline.Config'
[IO.File]::WriteAllText($offlineNuget, '<configuration><packageSources><clear /></packageSources></configuration>', (New-Object Text.UTF8Encoding($false)))

function Quote([string]$value) {
    # CommandLineToArgvW rules: double a trailing run of backslashes and escape embedded quotes.
    if ($value -notmatch '[\s"]' -and $value.Length -gt 0) { return $value }
    $escaped = [regex]::Replace($value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}
function Invoke-Logged([string]$file, [string[]]$arguments, [string]$log, [int]$timeout) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $file
    $start.Arguments = (($arguments | ForEach-Object { Quote $_ }) -join ' ')
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8; $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($timeout * 1000)
    if ($timedOut) {
        # Kill the whole tree: a WPF check may have spawned helpers.
        & (Join-Path $env:WINDIR 'System32/taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
        $process.WaitForExit(10000) | Out-Null
    }
    else { $process.WaitForExit() }
    $out = $stdout.GetAwaiter().GetResult(); $err = $stderr.GetAwaiter().GetResult()
    $text = $out
    if ($err) { $text += [Environment]::NewLine + '--- stderr ---' + [Environment]::NewLine + $err }
    [IO.File]::WriteAllText($log, $text, (New-Object Text.UTF8Encoding($false)))
    $lines = @($out -split "`r?`n" | Where-Object { $_.Trim() })
    $errorLines = @($err -split "`r?`n" | Where-Object { $_.Trim() })
    return [pscustomobject]@{
        ExitCode = $(if ($timedOut) { $null } else { $process.ExitCode }); TimedOut = $timedOut
        Last = $(if ($lines.Count) { $lines[-1] } else { '' }); FirstError = $(if ($errorLines.Count) { $errorLines[0] } else { '' })
    }
}
function Short([string]$text, [int]$length) { if ($text.Length -gt $length) { return $text.Substring(0, $length - 1) + [char]0x2026 } return $text }

$buildFailed = @{}
function Build-Project([string]$name, [string]$project, [string[]]$extra) {
    $log = Join-Path $logs "build-$name.log"
    $result = Invoke-Logged 'dotnet' (@('build', $project, '-c', 'Debug', '--artifacts-path', (Join-Path $art 'build'), '-nologo', '-v:q', "-p:RestoreConfigFile=$offlineNuget", '-p:NuGetAudit=false') + $extra) $log 1800
    if ($result.TimedOut -or $result.ExitCode -ne 0) {
        $first = (Select-String -LiteralPath $log -Pattern ': error ' | Select-Object -First 1).Line
        $buildFailed[$name] = $(if ($first) { $first.Trim() } else { "dotnet build exit $($result.ExitCode)" })
        Write-Host ("BUILD FAIL {0}: {1}" -f $name, (Short $buildFailed[$name] 220))
    }
    else { Write-Host "built $name" }
}

$tools = @($selected | Where-Object { $_.tool } | ForEach-Object { $_.tool; if ($_.needs) { $_.needs } } | Select-Object -Unique)
if ($tools.Count -and -not $SkipBuild) {
    Build-Project 'SoncaAudioInspector' (Join-Path $repo 'SoncaAudioInspector.csproj') @()
    if ($buildFailed.ContainsKey('SoncaAudioInspector')) { foreach ($tool in $tools) { $buildFailed[$tool] = 'app build failed' } }
    else {
        foreach ($tool in $tools) {
            $needsApp = @($catalogue | Where-Object { $_.tool -eq $tool -and $_.needsApp }).Count -gt 0
            Build-Project $tool (Join-Path $repo "tools/$tool/$tool.csproj") $(if ($needsApp) { @("-p:AudioAssemblyPath=$appDll") } else { @() })
        }
    }
}
function Tool-Dll([string]$tool) {
    $root = Join-Path $bin $tool
    if (-not (Test-Path -LiteralPath $root)) { return $null }
    $found = Get-ChildItem -LiteralPath $root -Recurse -Filter "$tool.dll" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($found) { return $found.FullName } return $null
}
function Expand([string]$value) {
    $value = $value.Replace('{art}', $art).Replace('{app}', $app).Replace('{repo}', $repo)
    return [regex]::Replace($value, '\{bin:([A-Za-z0-9]+)\}', { param($m) $dll = Tool-Dll $m.Groups[1].Value; if (-not $dll) { throw "Missing build output for $($m.Groups[1].Value)" } $dll })
}

# Native SkiaSharp/MKL and app-local assemblies are resolved from the app directory by some WPF checks.
$env:PATH = $app + ';' + (Join-Path $app 'runtimes/win-x64/native') + ';' + $env:PATH
$hosts = @()
foreach ($candidate in @('powershell', 'pwsh')) { $command = Get-Command $candidate -ErrorAction SilentlyContinue; if ($command) { $hosts += [pscustomobject]@{ Name = $candidate; Path = $command.Source } } }
$currentHost = (Get-Process -Id $PID).Path
if ([IO.Path]::GetFileNameWithoutExtension($currentHost) -notin @('powershell', 'pwsh')) {
    if (-not $hosts.Count) { throw 'No PowerShell executable is available for script checks.' }
    $currentHost = $hosts[0].Path
}

$results = New-Object Collections.Generic.List[object]
foreach ($check in $selected) {
    $runs = @()
    if ($check.script) {
        $scriptPath = Join-Path $PSScriptRoot $check.script
        $targets = if ($check.hosts -eq 'both' -and $hosts.Count) { $hosts } else { @([pscustomobject]@{ Name = ''; Path = $currentHost }) }
        foreach ($target in $targets) {
            $id = if ($target.Name) { "$($check.id)-$($target.Name)" } else { $check.id }
            $arguments = @($check.args | ForEach-Object { (Expand $_) + $(if ($target.Name -and $_ -like '{art}/checks/*') { "-$($target.Name)" } else { '' }) })
            $runs += [pscustomobject]@{ Id = $id; File = $target.Path; Args = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) + $arguments }
        }
    }
    else { $runs += [pscustomobject]@{ Id = $check.id; File = 'dotnet'; Args = $null } }
    foreach ($run in $runs) {
        $row = [ordered]@{ id = $run.Id; group = $check.group; harness = $(if ($check.tool) { $check.tool } else { $check.script }); status = ''; seconds = 0.0; detail = ''; log = "logs/$($run.Id).log" }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            if ($check.tool) {
                $blocked = @(@($check.tool) + @($check.needs) | Where-Object { $_ -and $buildFailed.ContainsKey($_) })
                if ($blocked.Count) { $row.status = 'BUILD-FAIL'; $row.detail = "$($blocked[0]): $($buildFailed[$blocked[0]])"; $row.log = "logs/build-$($blocked[0]).log" }
                else {
                    $dll = Tool-Dll $check.tool
                    if (-not $dll) { throw "No build output for $($check.tool); run without -SkipBuild." }
                    $run.Args = @($dll) + @($check.args | ForEach-Object { Expand $_ })
                }
            }
            if (-not $row.status) {
                $outcome = Invoke-Logged $run.File $run.Args (Join-Path $logs "$($run.Id).log") $TimeoutSeconds
                if ($outcome.TimedOut) { $row.status = 'TIMEOUT'; $row.detail = "no result within $TimeoutSeconds s" }
                elseif ($outcome.ExitCode -eq 0) { $row.status = 'PASS'; $row.detail = $outcome.Last }
                else { $row.status = 'FAIL'; $row.detail = $(if ($outcome.FirstError) { $outcome.FirstError } else { "exit $($outcome.ExitCode): $($outcome.Last)" }) }
            }
        }
        catch { $row.status = 'ERROR'; $row.detail = $_.Exception.Message }
        $row.seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
        if ($check.quarantine) { $row.detail = "[quarantined: $($check.quarantine)] " + $row.detail }
        $results.Add([pscustomobject]$row)
        Write-Host ('{0,-10} {1,-30} {2,6:N1}s  {3}' -f $row.status, $row.id, $row.seconds, (Short $row.detail 150))
    }
}

$failed = @($results | Where-Object { $_.status -ne 'PASS' })
$sha = (& git -C $repo rev-parse HEAD)
$dirty = [bool](& git -C $repo status --porcelain --untracked-files=normal)
$summary = [ordered]@{
    ranUtc = [DateTimeOffset]::UtcNow.ToString('o'); sourceCommit = "$sha"; workingTreeDirty = $dirty
    evidence = 'build + offline logic on synthetic and recorded fixtures; no audio device, BLE radio, network, server, hardware or REW comparison'
    passed = @($results | Where-Object { $_.status -eq 'PASS' }).Count; failed = $failed.Count
    notRun = @($catalogue | Where-Object { $_.id -notin @($selected | ForEach-Object { $_.id }) } | ForEach-Object { [ordered]@{ id = $_.id; reason = $(if ($_.quarantine) { "quarantined: $($_.quarantine)" } elseif ($_.slow) { 'slow (use -IncludeSlow)' } else { 'not selected' }) } })
    results = $results
}
[IO.File]::WriteAllText((Join-Path $art 'offline-checks.json'), ($summary | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
$markdown = @('', "## Offline checks ($($summary.ranUtc))", '',
    "Command: ``scripts/run-offline-checks.ps1``$(if ($Only) { ' -Only ' + ($Only -join ',') })$(if ($Skip) { ' -Skip ' + ($Skip -join ',') })$(if ($IncludeSlow) { ' -IncludeSlow' })$(if ($IncludeQuarantined) { ' -IncludeQuarantined' })",
    "Source commit: $sha; working tree dirty: $dirty",
    "Result: $($summary.passed) passed, $($summary.failed) not passed, $(@($summary.notRun).Count) not run.",
    "Evidence level: $($summary.evidence).", '', '| Status | Check | Seconds | Detail |', '|---|---|---|---|')
$markdown += $results | ForEach-Object { "| $($_.status) | $($_.id) | $($_.seconds) | $((Short $_.detail 160).Replace('|', '\|')) |" }
if (@($summary.notRun).Count) { $markdown += @('', 'Not run: ' + ((@($summary.notRun) | ForEach-Object { "$($_.id) ($($_.reason))" }) -join '; ') + '.') }
Add-Content -LiteralPath (Join-Path $art 'ARTIFACT.md') -Value $markdown -Encoding utf8

Write-Host ''
Write-Host ("{0} passed, {1} not passed, {2} not run. Evidence: offline only. Report: {3}" -f $summary.passed, $summary.failed, @($summary.notRun).Count, (Join-Path $art 'offline-checks.json'))
if ($failed.Count) { $failed | ForEach-Object { Write-Host ("  {0} {1}: {2}" -f $_.status, $_.id, (Short $_.detail 200)) }; exit 1 }
