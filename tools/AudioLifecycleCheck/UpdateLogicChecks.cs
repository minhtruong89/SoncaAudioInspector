using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class UpdateLogicChecks
{
    public static async Task Run(string[] args)
    {
        string Option(string name) => args.Single(a => a.StartsWith(name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
        string root = Path.GetFullPath(Option("--output"));
        if (Directory.Exists(root)) throw new IOException("Update test output must be new.");
        Directory.CreateDirectory(root);
        string artifact = Directory.GetParent(root)!.Parent!.FullName;
        if (!File.Exists(Path.Combine(artifact, "artifact.json"))) throw new IOException("Test output must be artifact/checks/<new-dir>.");
        var checks = new List<string>();
        void Check(bool value, string text)
        {
            if (!value) throw new Exception("FAIL " + text);
            checks.Add(text); Console.WriteLine("PASS " + text);
            File.WriteAllLines(Path.Combine(root, "checks.txt"), checks.Select(c => "PASS " + c));
        }
        async Task Reject(Func<Task> run, string text)
        {
            bool failed = false; try { await run(); } catch { failed = true; }
            Check(failed, text);
        }
        void RejectSync(Action run, string text) => Reject(() => { run(); return Task.CompletedTask; }, text).GetAwaiter().GetResult();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = key.ExportSubjectPublicKeyInfoPem();
        string privateKeyPath = Path.Combine(root, "TEST-ONLY-private.pem");
        string publicKeyPath = Path.Combine(root, "TEST-ONLY-public.pem");
        File.WriteAllText(privateKeyPath, key.ExportECPrivateKeyPem()); File.WriteAllText(publicKeyPath, publicKey);
        UpdateFile Entry(string path, string text) => new(path, Encoding.UTF8.GetByteCount(text), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        var entries = new List<UpdateFile> { Entry("app.dll", "new-app"), Entry("native/mkl.dll", "new-mkl"), Entry("new.dll", "new-file"), Entry("same.dll", "same") };
        var manifest = new UpdateManifest(1, "SoncaAudioInspector", "1.0.1", "win-x64", DateTimeOffset.UtcNow, "Test offline", entries, ["feature-A"]);
        byte[] Bytes(UpdateManifest m) => JsonSerializer.SerializeToUtf8Bytes(m, UpdateManifest.JsonOptions);
        byte[] good = Bytes(manifest);
        string signature = UpdateSignature.Sign(good, key.ExportECPrivateKeyPem());
        UpdateSignature.Verify(good, signature, publicKey); Check(true, "valid P-256/SHA-256/P1363 signature");
        RejectSync(() => UpdateSignature.Verify(good, signature, wrongKey.ExportSubjectPublicKeyInfoPem()), "wrong signing key rejected");
        RejectSync(() => UpdateSignature.Verify(good, "not-base64", publicKey), "invalid signature rejected");
        byte[] tampered = good.ToArray(); tampered[0] ^= 1;
        RejectSync(() => UpdateSignature.Verify(tampered, signature, publicKey), "one-byte manifest tamper rejected");
        RejectSync(() => UpdateSignature.Verify(good, signature), "test key is not trusted by embedded production public key");
        using (var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            RejectSync(() => UpdateSignature.Sign(good, p384.ExportECPrivateKeyPem()), "non-P256 curve rejected");
        Check(UpdateManifest.ParseVersion("1.0.0") < UpdateManifest.ParseVersion("1.0.1")
            && UpdateManifest.ParseVersion("1.0.1") < UpdateManifest.ParseVersion("1.1")
            && UpdateManifest.ParseVersion("1.1") == UpdateManifest.ParseVersion("1.1.0"), "normalized version ordering 1.0.0 < 1.0.1 < 1.1");
        Check(AppVersion.Display == "v" + AppVersion.Current.ToString(AppVersion.Current.Revision > 0 ? 4 : AppVersion.Current.Build > 0 ? 3 : 2)
            && AppVersion.Format(new Version("1.0.1")) == "v1.0.1", "actual assembly version and patch display");
        var routing = (AudioRouting)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AudioRouting));
        var shell = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(MainWindow).GetField("_audioRoutingView", hidden)!.SetValue(shell, routing);
        bool Busy() => (bool)typeof(MainWindow).GetMethod("UpdateBusy", hidden)!.Invoke(shell, null)!;
        Check(!Busy(), "update is not blocked with no model/disabled Start; no UI constructor");
        typeof(AudioRouting).GetField("_isRoutingHeadroomRunning", hidden)!.SetValue(routing, true);
        Check(!Busy(), "passive Headroom monitoring does not block update");
        foreach (string field in new[] { "_routingWorkflowActive", "_isExecutingAutoSuite", "_isFindingStandardReference", "_qaCompletionInProgress" })
        {
            typeof(AudioRouting).GetField(field, hidden)!.SetValue(routing, true);
            Check(Busy(), "actual MainWindow gate blocks " + field);
            typeof(AudioRouting).GetField(field, hidden)!.SetValue(routing, false);
        }
        Check(!Busy(), "completed QA completion/retry restores update idle state");
        using var publishGate = new System.Threading.SemaphoreSlim(1, 1);
        typeof(MainWindow).GetField("_audioConfigPublishGate", hidden)!.SetValue(shell, publishGate);
        await publishGate.WaitAsync();
        Check(Busy(), "held background model publish gate blocks update/restart");
        publishGate.Release();
        Check(!Busy(), "released model publish gate restores update idle state");
        typeof(AudioRouting).GetField("_activeMeasurementTask", hidden)!.SetValue(routing, Task.CompletedTask);
        Check(Busy(), "active acoustic measurement task blocks update");
        typeof(AudioRouting).GetField("_activeMeasurementTask", hidden)!.SetValue(routing, null);
        typeof(MainWindow).GetField("_modelEditorOpen", hidden)!.SetValue(shell, true);
        Check(Busy(), "open model editor blocks update");
        var asm = typeof(AppVersion).Assembly;
        Check(asm.GetName().Version == AppVersion.Current && asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()!.InformationalVersion == AppVersion.Current.ToString(AppVersion.Current.Revision > 0 ? 4 : 3)
            && FileVersionInfo.GetVersionInfo(asm.Location).FileVersion == AppVersion.Current.ToString(4), "actual assembly/file/informational versions agree without source revision");
        foreach (string path in new[] { "../x", "C:\\x", "\\\\srv\\x", "/x", "", "a//b", "a/../b", "a:b", "a/CON.dll", "a/x. ", "checking_config.json", "checking_config_realRoute.json", "save standards/a.csv", "fail data/x", "calibrations/mic.csv", "standards/a", "update-staging/a", "a.old-1.0", "a.log", "a.jsonl", "Chay_SoncaAudioInspector.bat" })
            RejectSync(() => UpdatePaths.Validate(path), "dangerous/excluded path rejected: " + path);
        RejectSync(() => (manifest with { Files = [Entry("a", "x"), Entry("A/b", "y")] }).Validate(), "parent/file collision rejected");
        RejectSync(() => (manifest with { Files = [Entry("a", "x"), Entry("A", "y")] }).Validate(), "case-insensitive duplicate rejected");
        RejectSync(() => (manifest with { Files = [entries[0] with { Sha256 = new string('g', 64) }] }).Validate(), "non-hex SHA rejected");
        RejectSync(() => (manifest with { Schema = 2 }).Validate(), "unsupported schema rejected");
        var handler = new FeedHandler(good, signature);
        using var client = new UpdateClient(new Uri("https://offline.invalid/feed/"), handler, publicKey);
        Check((await client.CheckAsync(new Version("1.0.0")))?.Version == "1.0.1", "signed local handler feed accepted by real client");
        Check(await client.CheckAsync(new Version("1.0.1")) == null && await client.CheckAsync(new Version("1.1")) == null, "equal and older versions skipped");
        foreach (var invalid in new[] { manifest with { App = "Other" }, manifest with { Architecture = "win-x86" } })
        {
            byte[] bytes = Bytes(invalid);
            using var badClient = new UpdateClient(new Uri("https://offline.invalid/"), new FeedHandler(bytes, UpdateSignature.Sign(bytes, key.ExportECPrivateKeyPem())), publicKey);
            await Reject(async () => await badClient.CheckAsync(new Version("1.0")), "wrong app/architecture rejected after signature validation: " + invalid.App + "/" + invalid.Architecture);
        }
        using (var badClient = new UpdateClient(new Uri("https://offline.invalid/"), new FeedHandler(tampered, signature), publicKey))
            await Reject(async () => await badClient.CheckAsync(new Version("1.0")), "client rejects tampered raw manifest bytes");
        byte[] alteredFeatures = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good).Replace("feature-A", "feature-B", StringComparison.Ordinal));
        Check(good.Zip(alteredFeatures).Count(p => p.First != p.Second) == 1, "features tamper fixture changes exactly one character");
        using (var badClient = new UpdateClient(new Uri("https://offline.invalid/"), new FeedHandler(alteredFeatures, signature), publicKey))
            await Reject(async () => await badClient.CheckAsync(new Version("1.0")), "client rejects signed manifest with one altered feature character");
        string summary = (manifest with { Features = Enumerable.Range(1, 10).Select(n => "Tính năng " + n + "\nextra").ToArray() }).FormatFeatures();
        Check(summary == "Tính năng 1 extra", "update prompt always shows only first feature as a single-line summary");
        Check((manifest with { Notes = "", Features = Enumerable.Range(1, 10).Select(n => "Tính năng " + n).ToArray() }).FeatureDetails().Length == 9,
            "update details retain every feature after the summary without the old eight-line cutoff");
        var legacy = manifest with { Features = null }; byte[] legacyBytes = Bytes(legacy);
        using (var legacyClient = new UpdateClient(new Uri("https://offline.invalid/"), new FeedHandler(legacyBytes, UpdateSignature.Sign(legacyBytes, key.ExportECPrivateKeyPem())), publicKey))
            Check(await legacyClient.CheckAsync(new Version("1.0")) != null, "legacy signed manifest without optional features remains accepted");
        handler.Blobs[entries[0].Sha256] = Encoding.UTF8.GetBytes("new-app");
        handler.Blobs[entries[1].Sha256] = Encoding.UTF8.GetBytes("new-mkl");
        handler.Blobs[entries[2].Sha256] = Encoding.UTF8.GetBytes("new-file");
        string app = Path.Combine(root, "station"); Directory.CreateDirectory(app);
        void Put(string path, string text) { string full = Path.Combine(app, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text, new UTF8Encoding(false)); }
        Put("app.dll", "old-app"); Put("native/mkl.dll", "old-mkl"); Put("same.dll", "same");
        foreach (string path in new[] { "checking_config.json", "save standards/a.csv", "calibrations/mic.csv", "standards/a.csv", "fail data/x", "unknown.dat", "Chay_SoncaAudioInspector.bat", "station.log", "station.jsonl" }) Put(path, "station-unique-bytes:" + path);
        var before = Snapshot(app);
        UpdatePlan plan = UpdateClient.CalculateChanges(manifest, app);
        Check(plan.Files.Select(f => f.Path).SequenceEqual(new[] { "app.dll", "native/mkl.dll", "new.dll" }) && plan.TotalBytes == 22, "delta contains changed/missing files only with exact total bytes");
        RejectSync(() => UpdateInstaller.CheckWritable(Path.Combine(root, "absent", "app")), "non-writable app directory reports Vietnamese error");
        handler.Blobs[entries[1].Sha256] = Encoding.UTF8.GetBytes("bad-mkl");
        await Reject(async () => await client.DownloadAsync(plan, app), "wrong download hash aborts entire stage");
        Check(Equal(before, Snapshot(app)) && !Directory.Exists(UpdateInstaller.StageDirectory(app, manifest.Version)), "corrupt download changed no app/station file and removed staging");
        handler.Blobs[entries[1].Sha256] = Encoding.UTF8.GetBytes("new-mkl");
        for (int nth = 1; nth <= plan.Files.Count; nth++)
        {
            await client.DownloadAsync(plan, app);
            int fault = nth;
            RejectSync(() => UpdateInstaller.Apply(plan, app, new Version("1.0"), () => false, n => { if (n == fault) throw new IOException("injected move " + n); }), "injected failure after backup at file " + nth);
            Check(Equal(before, Snapshot(app)), "rollback restored all bytes and removed newly installed files at fault " + nth);
        }
        await client.DownloadAsync(plan, app);
        int gates = 0;
        RejectSync(() => UpdateInstaller.Apply(plan, app, new Version("1.0"), () => ++gates == 2), "busy recheck immediately before replacement blocks update");
        Check(gates == 2 && Equal(before, Snapshot(app)), "measurement gate preserves every file");
        await client.DownloadAsync(plan, app);
        File.WriteAllText(UpdatePaths.Resolve(UpdateInstaller.StageDirectory(app, manifest.Version), "app.dll"), "tampered");
        RejectSync(() => UpdateInstaller.Apply(plan, app, new Version("1.0"), () => false), "staged tamper rejected before any replacement");
        Check(Equal(before, Snapshot(app)), "staging tamper preserved station");
        await client.DownloadAsync(plan, app);
        FileStream? lockedTarget = null;
        try
        {
            RejectSync(() => UpdateInstaller.Apply(plan, app, new Version("1.0"), () => false, n =>
            {
                if (n == 2)
                {
                    lockedTarget = new FileStream(Path.Combine(app, "app.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
                    throw new IOException("Injected rollback lock");
                }
            }), "real file lock during rollback reports incomplete recovery");
        }
        finally { lockedTarget?.Dispose(); }
        UpdateInstaller.Cleanup(app);
        string recovery = Path.Combine(app, "update-staging", "rollback-required.txt");
        Check(File.Exists(recovery) && File.Exists(Path.Combine(app, "app.dll.old-1.0.0")), "startup preserves backups/staging after incomplete rollback");
        RejectSync(() => UpdateInstaller.CheckWritable(app), "incomplete rollback blocks next update");
        File.Delete(Path.Combine(app, "app.dll")); File.Move(Path.Combine(app, "app.dll.old-1.0.0"), Path.Combine(app, "app.dll"));
        File.Delete(recovery); UpdateInstaller.Cleanup(app);
        Check(Equal(before, Snapshot(app)), "manual recovery fixture restores all original bytes");
        await client.DownloadAsync(plan, app);
        UpdateInstaller.Apply(plan, app, new Version("1.0"), () => false);
        Check(manifest.Files.All(f => UpdateSignature.Matches(UpdatePaths.Resolve(app, f.Path), f)), "successful transaction installed every changed file");
        Check(before.Where(p => !plan.Files.Any(f => f.Path == p.Key)).All(p => File.ReadAllBytes(Path.Combine(app, p.Key)).SequenceEqual(p.Value)), "config/standards/calibrations/fail data/launcher/logs and unknown file byte-preserved");
        Check(File.ReadAllBytes(Path.Combine(app, "app.dll.old-1.0.0")).SequenceEqual(before["app.dll"]), "old version backup retained until next startup");
        Put("save standards/keep.csv.old-1.0.0", "protected-backup");
        string stale = UpdateInstaller.StageDirectory(app, "0.9"); Directory.CreateDirectory(stale); File.WriteAllText(Path.Combine(stale, "stale.dll"), "x");
        UpdateInstaller.Cleanup(app);
        Check(!File.Exists(Path.Combine(app, "app.dll.old-1.0.0")) && !Directory.Exists(Path.Combine(app, "update-staging"))
            && File.ReadAllText(Path.Combine(app, "save standards/keep.csv.old-1.0.0")) == "protected-backup", "startup cleanup deletes app backups/staging and preserves protected backups");
        Put("recover.dll.old-1.0.0", "last-good"); UpdateInstaller.Cleanup(app);
        Check(File.ReadAllText(Path.Combine(app, "recover.dll")) == "last-good", "cleanup restores last good backup if target is absent");
        Check(handler.Requests.All(r => r.StartsWith("GET ", StringComparison.Ordinal)) && !handler.HadAuthorization, "dedicated HTTP client sent GET only and no authentication header");
        Check(UpdateClient.DefaultFileTimeout(0) == TimeSpan.FromSeconds(120)
            && UpdateClient.DefaultFileTimeout(6 * 1024 * 1024) == TimeSpan.FromSeconds(192), "default total budget is at least 120s and allows 6 MiB at 32 KiB/s");
        string timingApp = Path.Combine(root, "timing-station"); Directory.CreateDirectory(timingApp);
        File.WriteAllText(Path.Combine(timingApp, "checking_config.json"), "untouched-station-config");
        File.WriteAllText(Path.Combine(timingApp, "existing.dll"), "untouched-app-file");
        var timingBefore = Snapshot(timingApp);
        byte[] payload = Encoding.UTF8.GetBytes("chunks");
        var timingManifest = manifest with { Version = "1.0.2", Files = [Entry("slow.dll", "chunks")] };
        var timingPlan = UpdateClient.CalculateChanges(timingManifest, timingApp);
        // 1:100 scale: the old 15s cap is 150ms, silence 30s is 300ms, total floor 120s is 1200ms.
        TimeSpan silence = TimeSpan.FromMilliseconds(300), total = TimeSpan.FromMilliseconds(1200);
        using (var steady = new ChunkedHandler(payload, TimeSpan.FromMilliseconds(80)))
        using (var slowClient = new UpdateClient(new Uri("https://offline.invalid/"), steady, publicKey))
        {
            var watch = Stopwatch.StartNew();
            string stage = await slowClient.DownloadAsync(timingPlan, timingApp, idleTimeout: silence, fileTimeout: _ => total);
            Check(watch.Elapsed > silence && steady.Body.ChunksRead == payload.Length
                && UpdateSignature.Matches(Path.Combine(stage, "slow.dll"), timingManifest.Files[0]),
                $"steady chunks succeed beyond scaled old 15s cap and one silence interval ({watch.ElapsedMilliseconds}ms); SHA-256 matches");
            Check(Equal(timingBefore, Snapshot(timingApp)), "successful delayed download does not replace app/station files");
            UpdateInstaller.DeleteStage(stage);
        }
        foreach (string scenario in new[] { "silence", "total", "oversize" })
        {
            byte[] content = scenario == "oversize" ? payload.Concat(new byte[] { 1 }).ToArray() : payload;
            using var delayed = new ChunkedHandler(content, TimeSpan.FromMilliseconds(80), scenario == "silence" ? TimeSpan.FromMilliseconds(600) : null);
            using var timedClient = new UpdateClient(new Uri("https://offline.invalid/"), delayed, publicKey);
            bool cancelled = false, oversized = false;
            try { await timedClient.DownloadAsync(timingPlan, timingApp, idleTimeout: silence, fileTimeout: _ => scenario == "total" ? TimeSpan.FromMilliseconds(200) : total); }
            catch (OperationCanceledException) { cancelled = true; }
            catch (InvalidDataException ex) { oversized = ex.Message.Contains("vượt kích thước", StringComparison.Ordinal); }
            Check(scenario == "oversize" ? oversized : cancelled && delayed.Body.ReadCancelled && delayed.Body.ChunksRead > 0,
                "chunked stream rejects " + scenario + (scenario == "total" ? " despite regular progress" : ""));
            Check(!Directory.Exists(UpdateInstaller.StageDirectory(timingApp, timingManifest.Version)) && Equal(timingBefore, Snapshot(timingApp)),
                scenario + " abort removes all staging and changes no app/station file");
        }
        string portable = Path.Combine(root, "fake-portable"); Directory.CreateDirectory(portable);
        void Portable(string path, string text) { string target = Path.Combine(portable, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text, new UTF8Encoding(false)); }
        Portable("SoncaAudioInspector.dll", "fake-dll-1"); Portable("SoncaAudioInspector.exe", "FAKE-DO-NOT-RUN"); Portable("libMathNetNumericsMKL.dll", "fake-mkl-kept");
        foreach (string excluded in new[] { "checking_config.json", "save standards/a.csv", "calibrations/mic.csv", "standards/a.csv", "fail data/x", "update-staging/a", "a.old-1.0", "a.log", "a.jsonl", "Chay_SoncaAudioInspector.bat" }) Portable(excluded, "excluded");
        string feed = args.FirstOrDefault(a => a.StartsWith("--sample-feed=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? Path.Combine(root, "sample-feed");
        string tool = Path.GetFullPath(Option("--feed-tool"));
        async Task<(int ExitCode, string Log)> RunTool(string label, params string[] command)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(tool); foreach (string argument in command) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string log = await stdout + await stderr;
            File.WriteAllText(Path.Combine(root, label + ".log"), log);
            return (process.ExitCode, log);
        }
        async Task Tool(string label, params string[] command)
        {
            var (exitCode, log) = await RunTool(label, command);
            Check(exitCode == 0, "UpdateFeedTool " + label + ": " + log.Trim());
        }
        string featuresFile = Path.Combine(root, "features.txt");
        File.WriteAllText(featuresFile, "Cập nhật an toàn\n\n<script>alert('test')</script>\n", new UTF8Encoding(false));
        await Tool("build", "build", "--portable", portable, "--version", "1.0.1", "--notes", "TEST KEY ONLY - <b>offline sample</b>", "--key", privateKeyPath, "--out", feed,
            "--features-file", featuresFile, "--released", "2026-11-04T03:00:00Z");
        await Tool("verify", "verify", "--feed", feed, "--public-key", publicKeyPath);
        using var builderClient = new UpdateClient(new Uri("https://offline.invalid/"), new DirectoryFeedHandler(feed), publicKey);
        var built = await builderClient.CheckAsync(new Version("1.0.0"));
        Check(built != null && built.Files.Count == 4 && built.Files.Any(f => f.Path == "libMathNetNumericsMKL.dll") && built.Files.All(f => !UpdatePaths.IsExcluded(f.Path)), "real client accepts builder feed; all exclusions shared; MKL kept");
        var installedInfo = JsonSerializer.Deserialize<InstalledVersionInfo>(File.ReadAllBytes(Path.Combine(portable, "version-info.json")), UpdateManifest.JsonOptions)!;
        Check(installedInfo.Version == "1.0.1" && installedInfo.ReleasedUtc == DateTimeOffset.Parse("2026-11-04T03:00:00Z")
            && installedInfo.Features.Length == 2 && built!.Features!.SequenceEqual(installedInfo.Features)
            && built.Files.Any(f => f.Path == "version-info.json" && UpdateSignature.Matches(Path.Combine(portable, f.Path), f)), "builder writes version-info before hashing, includes it in signed manifest, ignores blank feature lines, honors --released");
        using (var zip = ZipFile.OpenRead(Path.Combine(feed, "packages/1.0.1/SoncaAudioInspector-win-x64.zip")))
            Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(built!.Files.Select(f => f.Path).Order()), "full ZIP matches manifest and excludes station data");
        using (var different = new UpdateClient(new Uri("https://offline.invalid/"), new DirectoryFeedHandler(feed), wrongKey.ExportSubjectPublicKeyInfoPem()))
            await Reject(async () => await different.CheckAsync(new Version("1.0.0")), "real client rejects builder feed after public key changed");
        string newer = Path.Combine(root, "feed-next"); Portable("SoncaAudioInspector.dll", "fake-dll-2");
        await Tool("build-reuse", "build", "--portable", portable, "--version", "1.1", "--notes", "Reuse test <script>notes</script>", "--key", privateKeyPath, "--out", newer, "--previous", feed,
            "--features-file", featuresFile, "--released", "2026-11-05T03:00:00Z");
        await Tool("verify-reuse", "verify", "--feed", newer, "--public-key", publicKeyPath);
        Check(File.ReadAllText(Path.Combine(root, "build-reuse.log")).Contains("2 file dùng lại"), "content-addressed previous feed reuses exactly two unchanged files");
        string page = File.ReadAllText(Path.Combine(newer, "index.html"));
        Check(page.IndexOf("data-version=\"1.1\"", StringComparison.Ordinal) < page.IndexOf("data-version=\"1.0.1\"", StringComparison.Ordinal)
            && page.Contains("Phiên bản mới nhất", StringComparison.Ordinal), "static history contains two releases newest first and highlights latest");
        Check(!page.Contains("<script", StringComparison.OrdinalIgnoreCase) && page.Contains("&lt;script&gt;", StringComparison.Ordinal)
            && page.Contains("&lt;b&gt;offline sample&lt;/b&gt;", StringComparison.Ordinal) && !page.Contains("<b>offline sample", StringComparison.Ordinal), "history HTML escapes malicious features and notes without JavaScript");
        Check(page.Contains("<base href=\"/updates/audio-inspector/\">", StringComparison.Ordinal)
            && page.Contains("rel=\"canonical\" href=\"https://inventory.acnos.store/update\"", StringComparison.Ordinal)
            && page.Contains("href=\"packages/", StringComparison.Ordinal)
            && new[] { "href=\"/\"", "href=\"/report\"", "href=\"/update\"" }.All(link => page.Contains(link, StringComparison.Ordinal))
            && !System.Text.RegularExpressions.Regex.IsMatch(page, "<a href=\"/[^\"]+/\""), "history page works at canonical /update (base href) and lists site paths without trailing slashes");
        // 1.4.0 signed a summary that Windows PowerShell 5.1 had read as ANSI ("DuyÃ¡Â»‡t"): the builder must refuse such notes.
        string misdecoded = "v9: Duyá»‡t cáº¥u hÃ¬nh";
        var refused = await RunTool("build-misdecoded", "build", "--portable", portable, "--version", "9.0.0", "--notes", misdecoded, "--key", privateKeyPath,
            "--out", Path.Combine(root, "feed-misdecoded"), "--previous", newer, "--features-file", featuresFile);
        Check(refused.ExitCode != 0 && refused.Log.Contains("lỗi mã hóa", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(root, "feed-misdecoded", "versions")),
            "builder refuses release notes mis-decoded as ANSI before writing any feed file");
        string summaryFeed = Path.Combine(root, "feed-summary");
        await Tool("build-summary", "build", "--portable", portable, "--version", "1.2", "--notes", "Cập nhật an toàn", "--key", privateKeyPath, "--out", summaryFeed,
            "--previous", newer, "--features-file", featuresFile, "--released", "2026-11-06T03:00:00Z");
        string summaryPage = File.ReadAllText(Path.Combine(summaryFeed, "index.html"));
        int summaryStart = summaryPage.IndexOf("data-version=\"1.2\"", StringComparison.Ordinal);
        string summaryCard = summaryStart < 0 ? "" : summaryPage[summaryStart..summaryPage.IndexOf("</article>", summaryStart, StringComparison.Ordinal)];
        string summaryHtml = System.Net.WebUtility.HtmlEncode("Cập nhật an toàn");
        Check(summaryCard.Split(summaryHtml).Length == 2 && summaryCard.Contains("<p class=\"notes\">" + summaryHtml + "</p>", StringComparison.Ordinal)
            && summaryCard.Contains("&lt;script&gt;", StringComparison.Ordinal), "history shows a summary that equals the first feature once, as the summary");
        int featuresAt = summaryCard.IndexOf("<details class=\"features\"><summary>Chi tiết tính năng (", StringComparison.Ordinal);
        Check(featuresAt > summaryCard.IndexOf("<p class=\"notes\">", StringComparison.Ordinal) && summaryCard.IndexOf("<ul>", StringComparison.Ordinal) > featuresAt
            && !summaryCard.Contains("<details class=\"features\" open", StringComparison.Ordinal),
            "history shows only the summary; the feature list is collapsed until clicked, like the portable section");
        var packageLinks = System.Text.RegularExpressions.Regex.Matches(page, "href=\"(packages/[^\"]+)\"")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
        var links = packageLinks.Select(link => Path.Combine(newer, link.Split('?')[0])).ToArray();
        Check(links.Length == 2 && links.All(File.Exists), "all history package links resolve to real local ZIPs");
        Check(packageLinks.Zip(links).All(pair => pair.First.EndsWith("?sha256=" + UpdateSignature.Hash(pair.Second), StringComparison.Ordinal)),
            "download URLs include exact content hash to avoid cached partial package probes");
        Check(links.All(p => page.Contains(UpdateSignature.Hash(p), StringComparison.Ordinal)
            && page.Contains(new FileInfo(p).Length + " byte", StringComparison.Ordinal)), "history displays exact ZIP size and SHA-256 for every release");
        // --- Beta channel, signed catalog and downgrade (skill sonca-beta-release) ---------------------------------
        byte[] betaJson = Bytes(manifest with { Channel = "beta" }); string stableJson = Encoding.UTF8.GetString(good);
        Check(Encoding.UTF8.GetString(betaJson).Contains("\"channel\": \"beta\"", StringComparison.Ordinal) && !stableJson.Contains("channel", StringComparison.OrdinalIgnoreCase)
            && !stableJson.Contains("isBeta", StringComparison.OrdinalIgnoreCase) && !stableJson.Contains("displayVersion", StringComparison.OrdinalIgnoreCase),
            "beta manifest carries channel; stable manifest bytes keep the schema-1 shape (no channel/computed fields)");
        Check(JsonSerializer.Deserialize<LegacyManifest>(betaJson, UpdateManifest.JsonOptions)?.Version == "1.0.1", "clients that predate channel ignore the unknown property");
        RejectSync(() => (manifest with { Channel = "nightly" }).Validate(), "unknown release channel rejected");
        string betaFeed = Path.Combine(root, "feed-beta");
        Portable("SoncaAudioInspector.dll", "fake-dll-beta");
        var noPrevious = await RunTool("build-beta-no-previous", "build", "--portable", portable, "--version", "1.3", "--notes", "Beta", "--key", privateKeyPath,
            "--out", Path.Combine(root, "feed-beta-refused"), "--channel", "beta");
        Check(noPrevious.ExitCode != 0 && !Directory.Exists(Path.Combine(root, "feed-beta-refused", "versions")), "beta build without --previous refused before writing a feed");
        await Tool("build-beta", "build", "--portable", portable, "--version", "1.3", "--notes", "Bản beta thử nghiệm", "--key", privateKeyPath, "--out", betaFeed,
            "--previous", summaryFeed, "--channel", "beta", "--features-file", featuresFile, "--released", "2026-11-07T03:00:00Z");
        await Tool("verify-beta", "verify", "--feed", betaFeed, "--public-key", publicKeyPath, "--channel", "beta", "--previous", summaryFeed);
        Check(File.ReadAllBytes(Path.Combine(betaFeed, "latest.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(summaryFeed, "latest.json")))
            && File.ReadAllBytes(Path.Combine(betaFeed, "latest.json.sig")).SequenceEqual(File.ReadAllBytes(Path.Combine(summaryFeed, "latest.json.sig")))
            && File.Exists(Path.Combine(betaFeed, "beta.json")), "beta build copies latest.json/.sig byte-for-byte and writes only beta.json");
        var betaCatalog = UpdateCatalog.Read(File.ReadAllBytes(Path.Combine(betaFeed, "versions.json")));
        UpdateSignature.Verify(File.ReadAllBytes(Path.Combine(betaFeed, "versions.json")), File.ReadAllText(Path.Combine(betaFeed, "versions.json.sig")), publicKey);
        Check(betaCatalog.Validate().StableVersion == "1.2" && betaCatalog.BetaVersion == "1.3" && betaCatalog.Versions.Length == 4
            && betaCatalog.Versions.Single(v => v.Version == "1.3").IsBeta && betaCatalog.Versions.Where(v => v.Version != "1.3").All(v => !v.IsBeta),
            "signed catalog lists every archive with stable 1.2 and beta 1.3 pointers");
        string betaPage = File.ReadAllText(Path.Combine(betaFeed, "index.html"));
        int betaCard = betaPage.IndexOf("data-version=\"1.3\" data-channel=\"beta\"", StringComparison.Ordinal);
        int stableCard = betaPage.IndexOf("data-version=\"1.2\"", StringComparison.Ordinal);
        Check(betaCard >= 0 && stableCard > betaCard && betaPage.IndexOf("BETA · chưa kiểm tra kỹ", betaCard, StringComparison.Ordinal) is > 0 and var badge && badge < stableCard
            && betaPage.IndexOf("Phiên bản mới nhất", stableCard, StringComparison.Ordinal) > stableCard, "history marks the beta and keeps the 'newest' badge on stable 1.2");
        string tamperedLatest = Path.Combine(root, "feed-beta-tampered");
        await Tool("build-beta-tamper-copy", "build", "--portable", portable, "--version", "1.3", "--notes", "Bản beta thử nghiệm", "--key", privateKeyPath,
            "--out", tamperedLatest, "--previous", summaryFeed, "--channel", "beta");
        File.Copy(Path.Combine(newer, "latest.json"), Path.Combine(tamperedLatest, "latest.json"), true);
        File.Copy(Path.Combine(newer, "latest.json.sig"), Path.Combine(tamperedLatest, "latest.json.sig"), true);
        var moved = await RunTool("verify-beta-moved-latest", "verify", "--feed", tamperedLatest, "--public-key", publicKeyPath, "--channel", "beta", "--previous", summaryFeed);
        Check(moved.ExitCode != 0, "verify refuses a beta feed whose latest.json differs from the previous feed");
        using (var betaClient = new UpdateClient(new Uri("https://offline.invalid/"), new DirectoryFeedHandler(betaFeed), publicKey))
        {
            Check(await betaClient.CheckAsync(new Version("1.2"), UpdateChannelKind.Stable) == null && (await betaClient.CheckAsync(new Version("1.2")))?.Version == null
                && (await betaClient.CheckAsync(new Version("1.1"), UpdateChannelKind.Stable))?.Version == "1.2", "stable-channel stations never see the beta; older ones still get stable 1.2");
            var offered = await betaClient.CheckAsync(new Version("1.2"), UpdateChannelKind.Beta);
            Check(offered is { Version: "1.3", IsBeta: true } && await betaClient.CheckAsync(new Version("1.3"), UpdateChannelKind.Beta) == null,
                "beta-channel station is offered beta 1.3 and not re-offered after installing it");
            var catalogLive = await betaClient.GetCatalogAsync();
            Check(catalogLive.NewestFirst().First().Version == "1.3", "client reads and verifies the signed catalog newest first");
            // Downgrade 1.3 beta -> 1.2 stable on a synthetic station: signed archive, delta, transaction, station data kept.
            string betaStation = Path.Combine(root, "beta-station"); Directory.CreateDirectory(betaStation);
            foreach (var file in offered!.Files)
            {
                string target = UpdatePaths.Resolve(betaStation, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(betaFeed, "files", file.Sha256), target);
            }
            File.WriteAllText(Path.Combine(betaStation, "checking_config.json"), "station-config"); Directory.CreateDirectory(Path.Combine(betaStation, "save standards"));
            File.WriteAllText(Path.Combine(betaStation, "save standards", "a.csv"), "station-standard");
            var older = await betaClient.GetVersionAsync("1.2");
            var downgradePlan = UpdateClient.CalculateChanges(older, betaStation);
            await betaClient.DownloadAsync(downgradePlan, betaStation);
            UpdateInstaller.Apply(downgradePlan, betaStation, new Version("1.3"), () => false);
            Check(downgradePlan.Files.Count > 0 && older.Files.All(f => UpdateSignature.Matches(UpdatePaths.Resolve(betaStation, f.Path), f))
                && File.ReadAllText(Path.Combine(betaStation, "checking_config.json")) == "station-config"
                && File.ReadAllText(Path.Combine(betaStation, "save standards", "a.csv")) == "station-standard",
                "downgrade 1.3 beta -> 1.2 installs the signed archive transactionally and preserves station data");
            await Reject(async () => await betaClient.GetVersionAsync("9.9"), "unknown archived version is refused");
        }
        byte[] catalogBytes = File.ReadAllBytes(Path.Combine(betaFeed, "versions.json")); catalogBytes[^3] ^= 1;
        string catalogTamper = Path.Combine(root, "feed-catalog-tamper"); Directory.CreateDirectory(catalogTamper);
        File.WriteAllBytes(Path.Combine(catalogTamper, "versions.json"), catalogBytes); File.Copy(Path.Combine(betaFeed, "versions.json.sig"), Path.Combine(catalogTamper, "versions.json.sig"));
        using (var tamperClient = new UpdateClient(new Uri("https://offline.invalid/"), new DirectoryFeedHandler(catalogTamper), publicKey))
            await Reject(async () => await tamperClient.GetCatalogAsync(), "one-byte catalog tamper rejected");
        string afterBetaLow = Path.Combine(root, "feed-stable-below-beta"), afterBetaHigh = Path.Combine(root, "feed-stable-above-beta");
        Portable("SoncaAudioInspector.dll", "fake-dll-stable-1.2.5");
        await Tool("build-stable-below-beta", "build", "--portable", portable, "--version", "1.2.5", "--notes", "Ổn định", "--key", privateKeyPath, "--out", afterBetaLow, "--previous", betaFeed);
        await Tool("verify-stable-below-beta", "verify", "--feed", afterBetaLow, "--public-key", publicKeyPath);
        Portable("SoncaAudioInspector.dll", "fake-dll-stable-1.4");
        await Tool("build-stable-above-beta", "build", "--portable", portable, "--version", "1.4", "--notes", "Ổn định", "--key", privateKeyPath, "--out", afterBetaHigh, "--previous", betaFeed);
        await Tool("verify-stable-above-beta", "verify", "--feed", afterBetaHigh, "--public-key", publicKeyPath);
        var lowCatalog = UpdateCatalog.Read(File.ReadAllBytes(Path.Combine(afterBetaLow, "versions.json")));
        var highCatalog = UpdateCatalog.Read(File.ReadAllBytes(Path.Combine(afterBetaHigh, "versions.json")));
        Check(lowCatalog.StableVersion == "1.2.5" && lowCatalog.BetaVersion == "1.3" && File.Exists(Path.Combine(afterBetaLow, "beta.json"))
            && highCatalog.StableVersion == "1.4" && highCatalog.BetaVersion == null && !File.Exists(Path.Combine(afterBetaHigh, "beta.json")) && highCatalog.Versions.Length == 5,
            "a later stable release keeps a newer beta pointer and drops a superseded one; archives are kept");
        string prunedFeed = Path.Combine(root, "feed-pruned");
        var pruneRefused = await RunTool("prune-without-stable", "prune", "--feed", afterBetaHigh, "--keep", "1.2", "--key", privateKeyPath, "--out", Path.Combine(root, "feed-pruned-refused"));
        Check(pruneRefused.ExitCode != 0, "prune refuses to drop the stable latest.json release");
        await Tool("prune", "prune", "--feed", afterBetaHigh, "--keep", "1.2,1.4", "--key", privateKeyPath, "--out", prunedFeed);
        await Tool("verify-pruned", "verify", "--feed", prunedFeed, "--public-key", publicKeyPath);
        string[] prunePlan = File.ReadAllLines(Path.Combine(prunedFeed, "prune-plan.txt"));
        var keptManifests = new[] { "1.2", "1.4" }.Select(v => UpdateManifest.Read(File.ReadAllBytes(Path.Combine(prunedFeed, "versions", v + ".json")))).ToArray();
        var prunedCatalog = UpdateCatalog.Read(File.ReadAllBytes(Path.Combine(prunedFeed, "versions.json")));
        Check(prunePlan.Contains("versions/1.0.1.json") && prunePlan.Contains("versions/1.3.json.sig") && prunePlan.Contains("packages/1.1") && !prunePlan.Any(p => p.Contains("1.2") || p.Contains("1.4"))
            && prunePlan.Where(p => p.StartsWith("files/")).All(p => keptManifests.All(m => m.Files.All(f => "files/" + f.Sha256 != p)))
            && prunePlan.All(p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^(versions/[0-9.]+\.json(\.sig)?|packages/[0-9.]+|files/[0-9a-f]{64})$"))
            && prunedCatalog.Versions.Select(v => v.Version).Order().SequenceEqual(new[] { "1.2", "1.4" })
            && File.ReadAllBytes(Path.Combine(prunedFeed, "latest.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(afterBetaHigh, "latest.json"))),
            "prune keeps chosen releases, never lists a kept blob, re-signs the catalog and copies latest.json byte-for-byte");
        var reused = await RunTool("build-existing-version", "build", "--portable", portable, "--version", "1.3", "--notes", "Trùng", "--key", privateKeyPath,
            "--out", Path.Combine(root, "feed-existing"), "--previous", betaFeed);
        Check(reused.ExitCode != 0, "a version already in the previous feed (incl. a beta) cannot be re-published; promote with a higher version");
        string prefs = Path.Combine(root, "channel-prefs");
        Check(UpdateChannelPreference.Load(prefs) == UpdateChannelPreference.Default, "missing channel preference falls back to the build default");
        UpdateChannelPreference.Save(UpdateChannelKind.Beta, prefs);
        Check(UpdateChannelPreference.Load(prefs) == UpdateChannelKind.Beta, "channel preference saved and reloaded");
        File.WriteAllText(UpdateChannelPreference.PathIn(prefs), "{not json");
        Check(UpdateChannelPreference.Load(prefs) == UpdateChannelPreference.Default, "corrupt channel preference falls back to the build default without crashing");
        Check(AppVersion.IsBeta == (AppVersion.Channel == "beta") && AppVersion.DisplayWithChannel == AppVersion.Display + (AppVersion.IsBeta ? " beta" : ""),
            "build channel metadata drives the beta label; numeric versions unchanged");
        var restart = UpdateRestart.StartInfo();
        Check(restart.FileName == Environment.ProcessPath && restart.ArgumentList.SequenceEqual(Environment.GetCommandLineArgs().Skip(1)), "restart plan preserves exact current exe and arguments; no process launched");
        Console.WriteLine($"PASS update-logic: {checks.Count} offline checks; no app EXE, network, BLE or audio.");
    }
    private static Dictionary<string, byte[]> Snapshot(string app) => UpdatePaths.ApplicationFiles(app).ToDictionary(f => Path.GetRelativePath(app, f).Replace('\\', '/'), File.ReadAllBytes);
    private static bool Equal(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var value) && p.Value.SequenceEqual(value));
    private sealed class ChunkedHandler(byte[] payload, TimeSpan delay, TimeSpan? secondDelay = null) : HttpMessageHandler
    {
        public DelayedChunkStream Body { get; } = new(payload, delay, secondDelay);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Body) });
    }
    private sealed class DelayedChunkStream(byte[] payload, TimeSpan delay, TimeSpan? secondDelay) : Stream
    {
        private int _position;
        public int ChunksRead => _position;
        public bool ReadCancelled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == payload.Length) return 0;
            try { await Task.Delay(_position == 1 ? secondDelay ?? delay : delay, cancellationToken); }
            catch (OperationCanceledException) { ReadCancelled = true; throw; }
            buffer.Span[0] = payload[_position++]; return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FeedHandler(byte[] manifest, string signature) : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Blobs { get; } = new();
        public List<string> Requests { get; } = new();
        public bool HadAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            HadAuthorization |= request.Headers.Authorization != null;
            string leaf = request.RequestUri!.Segments.Last();
            byte[]? bytes = leaf == "latest.json" ? manifest : leaf == "latest.json.sig" ? Encoding.UTF8.GetBytes(signature) : Blobs.GetValueOrDefault(leaf);
            return Task.FromResult(new HttpResponseMessage(bytes == null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? []) });
        }
    }
    private sealed class DirectoryFeedHandler(string feed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(feed, request.RequestUri!.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            // Like the real server: an absent pointer (e.g. no beta.json) is a 404, not a transport error.
            return Task.FromResult(File.Exists(path) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
        }
    }
    private sealed record LegacyManifest(int Schema, string App, string Version, string Architecture, DateTimeOffset ReleasedUtc, string Notes, List<UpdateFile> Files, string[]? Features = null);
}
