using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SoncaAudioInspector;

static partial class Program
{
    static void ManualDiagnosticsChecks(string output)
    {
        string folder = Path.Combine(output, "manual-diagnostics"); Directory.CreateDirectory(folder);
        var source = new TestConfig { id = "offline-new-manual", TestRubBuzz = true, RubBuzzMode = "PinkNoiseChirp",
            RubBuzzPink = new(Repeats: 2), RubBuzzChirp = new(Repeats: 2) };
        var context = new AdditionalTestContext(Rate, Rate, 1, "Offline · cùng route")
        { AcquisitionKey = "offline-new-modes", ReferenceDirectory = Path.Combine(folder, "must-not-use-standards"), SaveAsStandard = true };
        Directory.CreateDirectory(context.ReferenceDirectory);
        var config = RubBuzzManualCapture.Configuration(source, RubBuzzSoundChoice.LevelDiagnostic);
        // Invalid standards at precisely the keys the normal Pink/Chirp path would load. Any read would produce REVIEW.
        var standardPaths = new List<string>();
        foreach (double level in AcousticRubBuzzConfiguredTest.DiagnosticLevelsDbFs)
        {
            var settings = config.PinkSettings with { LevelRmsDbFs = level };
            string key = PinkRubBuzzStimulus.Key(settings, context.AcquisitionKey + ":" + config.id + ":" + Rate);
            standardPaths.Add(PinkRubBuzzStandard.PathFor(context.ReferenceDirectory, key));
        }
        string chirpKey = ChirpRubBuzzStimulus.Key(config.ChirpSettings, context.AcquisitionKey + ":" + config.id + ":" + Rate);
        standardPaths.Add(ChirpRubBuzzStandard.PathFor(context.ReferenceDirectory, chirpKey));
        foreach (string path in standardPaths) File.WriteAllText(path, "Do not load or overwrite this diagnostic isolation sentinel.");
        string Fingerprint() => string.Join(";", Directory.GetFiles(context.ReferenceDirectory).Order().Select(path =>
            Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        string before = Fingerprint();
        var plan = RubBuzzManualCapture.Plan(source, RubBuzzSoundChoice.LevelDiagnostic, context);
        Check(plan.Steps.Count == 15 && plan.Steps.Take(12).All(s => s.DiagnosticTag == "pink-level")
            && plan.Steps.Skip(12).All(s => s.DiagnosticTag == "chirp")
            && plan.Steps.Take(12).Select(s => s.LevelDbFs).SequenceEqual(new[] { -30.0, -30, -30, -24, -24, -24, -18, -18, -18, -12, -12, -12 })
            && plan.Steps.Take(12).All(s => s.Samples.Max(Math.Abs) < .98f),
            "level diagnostic tagged plan: one background, −30/−24/−18/−12 ×3 (no digital clip) then standard chirp ×3");
        Check(!RubBuzzManualCapture.AllowsStandard(RubBuzzSoundChoice.LevelDiagnostic)
            && !RubBuzzManualCapture.AllowsStandard(RubBuzzSoundChoice.BroadbandPink)
            && source.PinkSettings.Repeats == 2 && source.ChirpSettings.Repeats == 2,
            "new choices prohibit standards and preserve source Auto Test repeat settings");
        var bg = Background(3, 3e-5, 880);
        float[] FakeCapture(AdditionalTestStepStimulus step, int repeat)
        {
            var samples = step.Samples;
            var capture = Noise(samples.Length + Rate / 2, 3e-5, 900 + repeat);
            for (int i = 0; i < samples.Length; i++) capture[i + Rate / 4] += samples[i] * .3f;
            return capture;
        }
        var events = new List<string>(); int count = 0; bool exclusive = false, stopped = false;
        var measured = Task.Run(() => RubBuzzManualCapture.RunAsync(source, RubBuzzSoundChoice.LevelDiagnostic, context,
            (seconds, ct) => { events.Add("background"); return Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)); },
            (step, ct) => { events.Add(step.DiagnosticTag + ":" + step.LevelDbFs); return Task.FromResult(new RubBuzzCapturedAudio(FakeCapture(step, count++), Rate)); },
            () => { }, value => { exclusive = value; events.Add(value ? "exclusive-on" : "exclusive-off"); },
            () => stopped = true, CancellationToken.None)).GetAwaiter().GetResult();
        Check(count == 15 && events.Count(e => e == "background") == 1 && events[0] == "exclusive-on"
            && events[^1] == "exclusive-off" && !exclusive && stopped,
            "level coordinator captures exactly one background and fifteen tagged steps, then resets/stops");
        Check(measured.Levels?.Rows.Length == 4 && measured.Levels.Rows.Select(r => r.StimulusRmsDbFs).SequenceEqual(new[] { -30.0, -24, -18, -12 })
            && measured.Levels.Runs.All(r => r.Captures.Length == 3) && measured.Chirp?.Captures.Length == 3,
            "level analysis retains each level's three captures and three chirps");
        Check(measured.Levels!.Steps.All(s => Math.Abs(s.BassGainDb - 6) < .2)
            && measured.Pink!.StandardMetrics == null && measured.Chirp!.StandardMetrics == null
            && !measured.Pink.SavedAsStandard && !measured.Chirp.SavedAsStandard
            && !measured.Chirp.Message.Contains("Không đọc/ghi") && before == Fingerprint(),
            "linear level model gains 6 dB per step; supplied SaveAsStandard context and corrupt standards remain isolated");
        Check(measured.Impulsive != null && measured.Diagnosis?.Signs.Length == 10
            && measured.Diagnosis.Signs.Single(s => s.Key == "S9").Strength != "Chưa đủ dữ liệu",
            "manual result contains additive impulsive diagnostics and populated S9");

        var wideSettings = new BroadbandPinkSettings(PeriodSamples: 32768, Periods: 4);
        count = 0; stopped = false;
        var wide = Task.Run(() => RubBuzzManualCapture.RunAsync(source, RubBuzzSoundChoice.BroadbandPink, context,
            (seconds, ct) => Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)),
            (step, ct) => Task.FromResult(new RubBuzzCapturedAudio(FakeCapture(step, count++), Rate)),
            () => { }, value => exclusive = value, () => stopped = true, CancellationToken.None,
            broadband: wideSettings)).GetAwaiter().GetResult();
        Check(count == 3 && wide.Broadband != null && wide.Pink == null && wide.Chirp == null
            && !wide.AllowsStandard && !exclusive && stopped && before == Fingerprint(),
            "broadband coordinator uses three identical repeats and no legacy result/standard, resets/stops");

        // CPU only, after the earlier coordinator has warmed the analyzers. Stimulus generation/capture I/O is outside
        // the stopwatch; AnalyzeManual includes the unchanged legacy Pink+Chirp, new tracking filter and all signs.
        var performance = new List<object>();
        var combinedConfig = RubBuzzManualCapture.Configuration(source, RubBuzzSoundChoice.PinkAndChirp);
        var combinedContext = context with { SaveAsStandard = false, ReferenceDirectory = "" };
        var combinedPlan = AcousticRubBuzzConfiguredTest.PlanManual(combinedConfig, combinedContext, RubBuzzSoundChoice.PinkAndChirp);
        var combinedCaptures = combinedPlan.Steps.Select((s, i) => FakeCapture(s, i + 30)).ToArray();
        void TimeCombined(string name, TestConfig test, AdditionalTestContext route, AdditionalTestPlan p,
            float[] background, float[][] captures)
        {
            var timer = Stopwatch.StartNew();
            var result = AcousticRubBuzzConfiguredTest.AnalyzeManual(test, new(Rate, background, captures, route.RouteLabel),
                route, RubBuzzSoundChoice.PinkAndChirp, p);
            timer.Stop();
            Check(result.Pink != null && result.Chirp != null && result.Impulsive != null && result.Diagnosis?.Signs.Length == 10
                && timer.Elapsed.TotalSeconds < 3,
                $"{name} complete Pink+Chirp legacy+A+C analysis {timer.Elapsed.TotalMilliseconds:0} ms < 3000 ms (warmed CPU only)");
            performance.Add(new { name, milliseconds = timer.Elapsed.TotalMilliseconds, scope = "AnalyzeManual: legacy Pink+Chirp + tracking HP + diagnosis", warmed = true });
        }
        TimeCombined("synthetic", combinedConfig, combinedContext, combinedPlan, bg, combinedCaptures);
        const string realFolder = "artifacts/20261114-064502-633Z-rubbuzz-remeasure-review/captures/rubbuzz-20261114-064123-716-6b5ec339";
        var replayFiles = new[] { "pink", "chirp" }.SelectMany(kind => Enumerable.Range(1, 3)
            .Select(i => $"{kind}-microphone-{i}.wav")).ToArray();
        if (replayFiles.Append("pink-background.wav").All(name => File.Exists(Path.Combine(realFolder, name))))
        {
            var replayContext = combinedContext with { PlaybackSampleRate = 44100 };
            var replayPlan = AcousticRubBuzzConfiguredTest.PlanManual(combinedConfig, replayContext, RubBuzzSoundChoice.PinkAndChirp);
            var replayCaptures = replayFiles.Select(name => ReadWav(Path.Combine(realFolder, name))).ToArray();
            TimeCombined("14/11 replay", combinedConfig, replayContext, replayPlan,
                ReadWav(Path.Combine(realFolder, "pink-background.wav")), replayCaptures);
        }
        else Log.Add("SKIP complete-analysis timing real14: optional recordings absent");
        File.WriteAllText(Path.Combine(output, "rubbuzz-analysis-performance.json"), JsonSerializer.Serialize(performance,
            new JsonSerializerOptions { WriteIndented = true }));

        foreach (var choice in new[] { RubBuzzSoundChoice.LevelDiagnostic, RubBuzzSoundChoice.BroadbandPink })
        {
            exclusive = false; stopped = false;
            Reject(() => Task.Run(() => RubBuzzManualCapture.RunAsync(source, choice, context,
                (seconds, ct) => Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)),
                (step, ct) => throw new IOException("offline injected capture failure"), () => { },
                value => exclusive = value, () => stopped = true, CancellationToken.None,
                broadband: wideSettings)).GetAwaiter().GetResult(), choice + " capture failure rejected");
            Check(!exclusive && stopped && before == Fingerprint(), choice + " failure resets Exclusive/stops without standards");
            using var cancellation = new CancellationTokenSource();
            exclusive = false; stopped = false;
            Reject(() => Task.Run(() => RubBuzzManualCapture.RunAsync(source, choice, context,
                (seconds, ct) => Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)),
                (step, ct) => { cancellation.Cancel(); return Task.FromResult(new RubBuzzCapturedAudio(FakeCapture(step, 0), Rate)); },
                () => { }, value => exclusive = value, () => stopped = true, cancellation.Token,
                broadband: wideSettings)).GetAwaiter().GetResult(), choice + " cancellation rejected");
            Check(!exclusive && stopped, choice + " cancellation resets Exclusive/stops");
        }
        var pathStatus = CapturePathEnhancements.Parse("{0.0.1.00000000}.{12635ac4-b45b-420c-b2a4-81edd1a005ee}", "Mic offline",
            new Dictionary<string, object?> { [CapturePathEnhancements.DisableSysFxKey] = 0,
                ["{9e6136e0-57ab-4949-b57a-3627be142855},100"] = @"SWD\DRIVERENUM\offline#VocaEffectPack" });
        measured = measured with { CapturePathEnhancements = pathStatus,
            Diagnosis = RubBuzzDiagnosis.Build(measured.Pink, measured.Chirp, measured.Impulsive?.ToDiagnosisEvidence(),
                levels: measured.Levels, capturePath: pathStatus) };
        var view = new RubBuzzView();
        view.Update(measured.Pink, measured.Chirp, context.ReferenceDirectory, diagnostic: true,
            choice: RubBuzzSoundChoice.LevelDiagnostic, diagnostics: measured);
        Check(!view.CanSaveStandard && Control<DataGrid>(view, "RubBuzzSigns").Items.Count == 10,
            "diagnostic UI exposes ten signs and disables save standard");
        Render(view, Path.Combine(output, "rubbuzz-diagnostics-full.png"), 1600, 4600);
        var section = Control<StackPanel>(view, "RubBuzzDiagnosisSection");
        var details = Control<Expander>(view, "RubBuzzDiagnosisDetails");
        Check(!details.IsExpanded && section.ActualHeight > 60 && measured.Diagnosis!.Brief.Length is >= 2 and <= 4
            && measured.Diagnosis.Brief.Any(l => l.StartsWith("Nghiêng về:") || l.StartsWith("Chưa đủ bằng chứng"))
            && measured.Diagnosis.Brief.Last().StartsWith("Làm tiếp:"),
            $"diagnosis shows a short conclusion ({measured.Diagnosis.Brief.Length} lines) with the ten signs collapsed");
        // Render() lays the view out in a temporary host and detaches it afterwards: re-render to measure the expanded state.
        double collapsedHeight = section.ActualHeight;
        details.IsExpanded = true;
        Render(view, Path.Combine(output, "rubbuzz-diagnostics-expanded.png"), 1600, 4600);
        Check(section.ActualHeight > collapsedHeight + 200 && view.DiagnosisCopyText.StartsWith("KẾT LUẬN NHANH") && view.DiagnosisCopyText.Contains("S9")
            && view.DiagnosisCopyText.Contains("Kiểm tra tiếp"), "expanded details show measured signs; copy text keeps conclusion, signs and next checks");
        string exported = view.ExportAudio(folder);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(exported, "result.json")));
        Check(json.RootElement.GetProperty("diagnosis").GetProperty("Signs").GetArrayLength() == 10
            && json.RootElement.GetProperty("CapturePathEnhancements").GetProperty("MayProcessShared").GetBoolean()
            && json.RootElement.GetProperty("levelDiagnostics").GetProperty("Rows").GetArrayLength() == AcousticRubBuzzConfiguredTest.DiagnosticLevelsDbFs.Length
            && File.ReadAllText(Path.Combine(exported, "diagnosis.txt")).Contains("S9"),
            "result.json and copyable text export all signs, level rows and capture enhancement state");
        Check(Directory.GetFiles(exported, "*.wav").Select(Path.GetFileName).Distinct().Count() == view.ListeningItems.Count,
            "level export retains every listening item without filename collisions");
        Check(before == Fingerprint() && PinkRubBuzzAnalysis.Version == "pink-rattle-2.0.0"
            && ChirpRubBuzzAnalysis.Version == "chirp-rattle-1.0.0", "UI/export preserve station sentinel files and legacy algorithm versions");
        view.Shutdown();
        var wideView = new RubBuzzView();
        wideView.Update(null, null, context.ReferenceDirectory, diagnostic: true, choice: RubBuzzSoundChoice.BroadbandPink, diagnostics: wide);
        Check(!wideView.CanSaveStandard, "broadband UI cannot save a line standard");
        Render(wideView, Path.Combine(output, "rubbuzz-broadband-diagnostics.png"), 1600, 3500);
        string wideExport = wideView.ExportAudio(folder);
        using var wideJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(wideExport, "result.json")));
        Check(wideJson.RootElement.GetProperty("broadband").ValueKind == JsonValueKind.Object
            && wideJson.RootElement.GetProperty("pink").ValueKind == JsonValueKind.Null
            && wideJson.RootElement.GetProperty("chirp").ValueKind == JsonValueKind.Null,
            "broadband-only manual result renders/exports without a fabricated Pink or Chirp verdict");
        wideView.Shutdown();
    }
}
