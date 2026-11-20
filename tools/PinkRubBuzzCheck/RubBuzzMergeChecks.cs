using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoncaAudioInspector;

static partial class Program
{
    static T Control<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        { try { return Control<T>(child, name); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(name);
    }
    static void MergeChecks(string output, PinkRubBuzzRun pink, float[] bg)
    {
        var source = new TestConfig { id = "manual-test", RubBuzzMode = "PinkNoiseChirp", TestRubBuzz = true,
            RubBuzzPink = new(Repeats: 2), RubBuzzChirp = new(Repeats: 2) };
        var context = new AdditionalTestContext(44100, Rate, 1, "Chẩn đoán thủ công · fake Out / Mic")
            { AcquisitionKey = "offline", ReferenceDirectory = Path.Combine(output, "manual-standards") };
        var plans = Enum.GetValues<RubBuzzSoundChoice>().Select(choice => RubBuzzManualCapture.Plan(source, choice, context)).ToArray();
        Check(plans[0].BackgroundSeconds == 3 && plans[0].Steps.Count == 6 && plans[0].Steps.Take(3).All(s => s.DurationSeconds == 6)
            && plans[0].Steps.Skip(3).All(s => s.DurationSeconds == 3), "manual a: background 3 s, pink ×3 then chirp ×3 despite config Repeats=2");
        Check(plans[1].Steps.Count == 3 && plans[1].Steps.All(s => s.DurationSeconds == 6 && s.LevelDbFs == -18), "manual b: pink ×3, configured duration and level");
        Check(plans[2].Steps.Count == 3 && plans[2].Steps.All(s => s.DurationSeconds == 3 && s.LevelDbFs == -12 && s.PhysicalFrequencyHz == 2000), "manual c: chirp ×3, 3 s, −12 dBFS, 2000 Hz");
        Check(plans[3].Steps.Count == 3 && plans[3].Steps.All(s => s.DurationSeconds == 10 && s.LevelDbFs == -12 && s.PhysicalFrequencyHz == 2000), "manual d: slow chirp ×3, 10 s, same configured level/range");
        Check(plans[4].Steps.Count == 3 && plans[4].Steps.All(s => s.DurationSeconds == 524288 / 44100.0 && s.LevelDbFs == -15 && s.PhysicalFrequencyHz == 20000 && s.Samples.Length == 524288), "manual e: sweep ×3, 512K/44100 s, −15 dBFS, 20 kHz");
        Check(source.PinkSettings.Repeats == 2 && source.ChirpSettings.Repeats == 2
            && AcousticRubBuzzConfiguredTest.Plan(source, context).Steps.Count == 4 && new ChirpRubBuzzSettings().Repeats == 3, "manual plan does not mutate Auto Test config; new chirp default=3");
        Check(RubBuzzManualCapture.DefaultChoice(new TestConfig { RubBuzzMode = "PinkNoise" }) == RubBuzzSoundChoice.Pink
            && RubBuzzManualCapture.DefaultChoice(new TestConfig { RubBuzzMode = "Chirp" }) == RubBuzzSoundChoice.Chirp
            && RubBuzzManualCapture.DefaultChoice(null) == RubBuzzSoundChoice.PinkAndChirp, "sound default follows selected pink/chirp test, otherwise combined");
        Check(!RubBuzzManualCapture.AllowsStandard(RubBuzzSoundChoice.SlowChirp) && !RubBuzzManualCapture.AllowsStandard(RubBuzzSoundChoice.Sweep512K)
            && RubBuzzManualCapture.AllowsStandard(RubBuzzSoundChoice.PinkAndChirp), "diagnostic choices cannot use/save Auto Test standards");
        // Run the same coordinator as AudioRouting, with capture delegates only. No engine/devices instantiated.
        var events = new List<string>(); int steps = 0; bool exclusive = false, stopped = false;
        var cs = new ChirpRubBuzzSettings();
        var chirpCaptures = Enumerable.Range(0, 3).Select(i => ChirpCapture(cs, 800 + i, (220, 250, 3e-3))).ToArray();
        var manual = Task.Run(() => RubBuzzManualCapture.RunAsync(source, RubBuzzSoundChoice.PinkAndChirp, context,
            (seconds, ct) => { events.Add("background"); return Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)); },
            (step, ct) => { events.Add(steps < 3 ? "pink" : "chirp"); return Task.FromResult(new RubBuzzCapturedAudio(steps < 3 ? pink.Captures[steps++] : chirpCaptures[steps++ - 3], Rate)); },
            () => { }, value => { exclusive = value; events.Add(value ? "exclusive-on" : "exclusive-off"); }, () => stopped = true, CancellationToken.None)).GetAwaiter().GetResult();
        Check(events.SequenceEqual(new[] { "exclusive-on", "background", "pink", "pink", "pink", "chirp", "chirp", "chirp", "exclusive-off" })
            && !exclusive && stopped && manual.Pink?.Captures.Length == 3 && manual.Chirp?.Captures.Length == 3, "manual coordinator: Exclusive → quiet background → ordered 3+3 captures → reset/stop");
        Check(manual.Pink!.Metrics.CaptureLagSeconds.Length == 3 && manual.Pink.Metrics.RepeatEnvelopes.Length == 3
            && manual.Chirp!.Metrics.CaptureStartSeconds.Length == 3 && manual.Chirp.Metrics.RepeatBumpDb.Length == 3, "additive metrics expose all repeat traces and alignment");
        int issued = 0;
        void FailedCapture(Func<double, CancellationToken, Task<RubBuzzCapturedAudio>> background,
            Func<AdditionalTestStepStimulus, CancellationToken, Task<RubBuzzCapturedAudio>> capture, Action guard, CancellationToken token)
        {
            exclusive = false; stopped = false;
            Reject(() => Task.Run(() => RubBuzzManualCapture.RunAsync(source, RubBuzzSoundChoice.Pink, context,
                background, capture, guard, value => exclusive = value, () => stopped = true, token)).GetAwaiter().GetResult(), "manual failure/cancel/context change rejected");
            Check(!exclusive && stopped, "manual failure always resets Exclusive and stops");
        }
        Task<RubBuzzCapturedAudio> GoodBackground(double seconds, CancellationToken token) => Task.FromResult(new RubBuzzCapturedAudio(bg, Rate));
        Task<RubBuzzCapturedAudio> Step(AdditionalTestStepStimulus step, CancellationToken token) { issued++; return Task.FromResult(new RubBuzzCapturedAudio(pink.Captures[0], Rate)); }
        FailedCapture((seconds, token) => Task.FromResult(new RubBuzzCapturedAudio(Enumerable.Repeat(.1f, Rate * 3).ToArray(), Rate)), Step, () => { }, CancellationToken.None);
        Check(issued == 0, "quiet-background gate prevents any playback");
        FailedCapture(GoodBackground, (step, token) => Task.FromResult(new RubBuzzCapturedAudio(pink.Captures[0], 44100)), () => { }, CancellationToken.None);
        int guardCount = 0;
        FailedCapture(GoodBackground, Step, () => { if (++guardCount >= 4) throw new InvalidOperationException("route changed"); }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        FailedCapture(GoodBackground, (step, token) => { cancellation.Cancel(); return Step(step, token); }, () => { }, cancellation.Token);
        FailedCapture((seconds, token) => throw new IOException("Exclusive unavailable"), Step, () => { }, CancellationToken.None);

        var residual = RubBuzzListeningAudio.Pink(pink);
        Check(residual.Gain > 0 && residual.Samples.All(x => x.Length == pink.Captures[0].Length && x.All(v => float.IsFinite(v) && Math.Abs(v) <= .9)), "pink rattle-only retains capture timeline, soft limit ≤0.9, no clipping");
        var normalized = RubBuzzListeningAudio.Normalize(new[] { Enumerable.Repeat(.001f, 1000).ToArray(), Enumerable.Repeat(.002f, 1000).ToArray() });
        Check(Math.Abs(normalized.Samples[1][0] / normalized.Samples[0][0] - 2) < .02
            && Math.Abs(20 * Math.Log10(Math.Sqrt(normalized.Samples.SelectMany(x => x).Average(v => (double)v * v))) + 26) < .1,
            "one shared normalization gain per test preserves repeat ratios, pooled RMS ≈−26 dBFS");
        float[] Tone(double hz) => Enumerable.Range(0, Rate * 4).Select(i => (float)(.03 * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();
        double Rms(float[] x) => Math.Sqrt(x.Skip(Rate).Take(Rate).Average(v => (double)v * v));
        var filteredLow = RubBuzzListeningAudio.ChirpResidual(Tone(400), Rate, cs, .5);
        var filteredHigh = RubBuzzListeningAudio.ChirpResidual(Tone(8000), Rate, cs, .5);
        Check(Rms(filteredLow) < Rms(filteredHigh) * .001, "chirp STFT mask rejects <1 kHz by >60 dB");
        var pinkLowRun = pink with { Captures = new[] { Tone(400) } };
        var pinkLow = PinkRubBuzzAnalysis.BandPass(pinkLowRun.Captures[0], Rate, 1500, .45 * Rate, 8);
        Check(Math.Sqrt(pinkLow.Skip(Rate).Take(Rate).Average(v => v * v)) < .00001, "pink listening high-pass rejects bass below 1 kHz");
        var chirpResidual = RubBuzzListeningAudio.Chirp(manual.Chirp!);
        Check(chirpResidual.Samples.Length == 3 && chirpResidual.Samples.All(x => x.All(v => float.IsFinite(v) && Math.Abs(v) <= .9)), "chirp rattle-only all repeats have no clipping");
        double lag = manual.Chirp!.Metrics.CaptureStartSeconds[0];
        double time232 = cs.DurationSeconds * Math.Log(232 / cs.F1Hz) / Math.Log(cs.F2Hz / cs.F1Hz);
        Check(Math.Abs(lag - .5) < .01 && Math.Abs(RubBuzzListeningAudio.ChirpFrequency(cs, lag + time232, lag)!.Value - 232) <= 2,
            "chirp playback cursor returns 232±2 Hz using measured capture lag");
        Check(RubBuzzListeningAudio.ChirpFrequency(cs, lag - .01, lag) == null && RubBuzzListeningAudio.ChirpFrequency(cs, lag + 3.01, lag) == null,
            "chirp cursor hidden before/after aligned stimulus");
        var devices = new[] { new RubBuzzRenderDevice("test", "Speaker test"), new RubBuzzRenderDevice("other", "USB DAC"), new RubBuzzRenderDevice("realtek", "Speakers (Realtek)") };
        Check(RubBuzzAudioPlayer.DefaultDevice(devices, "test")?.Id == "realtek" && RubBuzzAudioPlayer.DefaultDevice(devices, "realtek")?.Id != "realtek", "default listening device differs from test, prefers Realtek/headphones");
        Check(RubBuzzAudioPlayer.DefaultDevice([], "test") == null && RubBuzzAudioPlayer.DefaultDevice(devices.Take(1), "test")?.Id == "test", "empty/single render device list handled");
        var micItem = new RubBuzzListeningItem("mic", true, RubBuzzAudioKind.Microphone, 0, [], Rate);
        Check(RubBuzzAudioPlayer.NeedsConfirmation(micItem, "test", "test") && !RubBuzzAudioPlayer.NeedsConfirmation(micItem, "other", "test")
            && !RubBuzzAudioPlayer.NeedsConfirmation(micItem with { Kind = RubBuzzAudioKind.Stimulus }, "test", "test"), "same-test mic/residual replay warns; stimulus/other output need no warning");
        Check(MeasurementRouteInfo.Format(null) == "Chưa chọn ngõ phát/thu — chọn tại Cấu hình đường âm thanh", "R1 empty route text");
        var route = new MeasurementRouteInfo("test", "Speakers", null, 44100, "UMIK-1", 1, 48000);
        Check(MeasurementRouteInfo.Format(route) == "Ngõ phát: Speakers · kênh L+R · 44100 Hz Shared │ Ngõ thu: UMIK-1 · kênh 1 · 48000 Hz · Shared (bài Rè thu Exclusive)", "R1 exact route name/channel/rate/mode text");
        Check(MeasurementRouteInfo.Format(route with { PlaybackChannel = 2, RecordingChannel = null }).Contains("kênh 2")
            && MeasurementRouteInfo.Format(route with { RecordingChannel = null }).Contains("kênh Trộn"), "R1 mono channel and mixed recording formatting");
        // Use one directory for both line standards, then render the combined FAIL presentation.
        string standards = Path.Combine(output, "merged-standards");
        var cleanChirp = ChirpRun(Enumerable.Range(0, 3).Select(i => ChirpCapture(cs, 900 + i)).ToArray(), bg, cs);
        ChirpRubBuzzStandard.SaveAsStandard(cleanChirp, new(), standards, "offline good fixture");
        var cleanPink = Run(Enumerable.Range(0, 3).Select(i => Capture(Lf(6, -34), 950 + i)).ToArray(), bg) with { ContextKey = pink.ContextKey };
        PinkRubBuzzStandard.SaveAsStandard(cleanPink, new(), standards, "offline good fixture");
        var view = new RubBuzzView { RouteProvider = () => route, RenderDevicesProvider = () => devices };
        view.Update(pink, manual.Chirp with { ContextKey = cleanChirp.ContextKey }, standards);
        Check(pink.Verdict == "FAIL" && view.CanSaveStandard, "merged FAIL compares both standards and offers one save control");
        Check(view.ListeningItems.Count == 16 && view.ListeningItems.Count(i => i.Kind == RubBuzzAudioKind.Rattle) == 6,
            "replay dropdown includes both stimuli/backgrounds, all six mic captures and six residuals");
        var selector = Control<ComboBox>(view, "RubBuzzListen"); selector.SelectedItem = view.ListeningItems.First(i => i.IsChirp && i.Kind == RubBuzzAudioKind.Rattle && i.Repeat == 1);
        Check(view.SelectedRepeat == 1, "replay selection maps to chirp repeat 2");
        Render(view, Path.Combine(output, "rubbuzz-merged-fail.png"), 1280, 1300);
        string exported = view.ExportAudio(output);
        Check(Directory.GetFiles(exported, "*.wav").Length == 16 && File.Exists(Path.Combine(exported, "result.json")), "one export writes every replay WAV plus both results JSON");
        var empty = new RubBuzzView();
        Check(!Control<ComboBox>(empty, "RubBuzzListen").IsEnabled && !empty.CanSaveStandard, "empty combined view disables replay and save");
        Render(empty, Path.Combine(output, "rubbuzz-merged-empty.png"), 1280, 1160);
        var invalid = new RubBuzzView(); invalid.Update(pink with { Metrics = new(false, "Nền bị Windows xử lý; đo lại.") }, cleanChirp, standards);
        Check(invalid.DetailsExpanded && !invalid.CanSaveStandard, "INVALID opens details and disables combined standard save");
        Render(invalid, Path.Combine(output, "rubbuzz-merged-invalid.png"), 1280, 1420);
        var diagnostic = new RubBuzzView(); diagnostic.Update(null, manual.Chirp, standards, diagnostic: true, choice: RubBuzzSoundChoice.SlowChirp);
        Check(!diagnostic.CanSaveStandard && diagnostic.ListeningItems.Count == 8 && manual.Chirp.StandardMetrics == null,
            "diagnostic session keeps replay, discards standards, disables standard save");
        var captureView = new RubBuzzView(); int clicks = 0; RubBuzzSoundChoice? selectedChoice = null;
        captureView.Runner = (choice, token) => { clicks++; selectedChoice = choice; return Task.FromResult(manual); };
        // The main button is the standard run (model RubBuzzMode, here none = Pink + Chirp) whatever the diagnostic selector shows.
        Control<ComboBox>(captureView, "RubBuzzSound").SelectedIndex = (int)RubBuzzSoundChoice.Sweep512K;
        Control<Button>(captureView, "RunRubBuzz").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // 16 regular replay items plus one tracking high-pass residual per chirp repeat (diagnostic A).
        Check(clicks == 1 && selectedChoice == RubBuzzSoundChoice.PinkAndChirp
            && captureView.ListeningItems.Count(i => i.Kind != RubBuzzAudioKind.TrackingResidual) == 16
            && manual.Impulsive != null
            && captureView.ListeningItems.Count(i => i.Kind == RubBuzzAudioKind.TrackingResidual) == manual.Chirp!.Captures.Length,
            "one Đo chuẩn click dispatches the standard combined plan once and exposes all captures plus tracking residuals");
        bool canSave = captureView.CanSaveStandard;
        Control<ComboBox>(captureView, "RubBuzzSound").SelectedIndex = (int)RubBuzzSoundChoice.LevelDiagnostic;
        Check(captureView.CanSaveStandard == canSave, "changing the diagnostic selector does not change what the standard result may save");
        captureView.SetDefaultChoice(new TestConfig { RubBuzzMode = "PinkNoise" }, "pink-model");
        Control<Button>(captureView, "RunRubBuzz").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clicks == 2 && selectedChoice == RubBuzzSoundChoice.Pink && Control<Button>(captureView, "RunRubBuzz").Content?.ToString()?.Contains("Pink (3") == true,
            "Đo chuẩn follows the selected model's RubBuzzMode (Pink-only model runs Pink)");
        Control<ComboBox>(captureView, "RubBuzzSound").SelectedIndex = (int)RubBuzzSoundChoice.Sweep512K;
        Control<Button>(captureView, "RunRubBuzzDiagnostic").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clicks == 3 && selectedChoice == RubBuzzSoundChoice.Sweep512K, "Đo chẩn đoán runs the sound chosen under Chẩn đoán nâng cao");
        var pinkButton = Control<Button>(captureView, "RunRubBuzzPink");
        var chirpButton = Control<Button>(captureView, "RunRubBuzzChirp");
        pinkButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clicks == 4 && selectedChoice == RubBuzzSoundChoice.Pink && pinkButton.Content?.ToString() == "▶ Chỉ Pink (3 lượt)",
            "Chỉ Pink button triggers standard Pink-only capture");
        chirpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(clicks == 5 && selectedChoice == RubBuzzSoundChoice.Chirp && chirpButton.Content?.ToString() == "▶ Chỉ Chirp (3 lượt)",
            "Chỉ Chirp button triggers standard Chirp-only capture");
        captureView.SetBusy(true);
        Check(!Control<Button>(captureView, "RunRubBuzz").IsEnabled && !pinkButton.IsEnabled && !chirpButton.IsEnabled,
            "busy state disables RunRubBuzz, RunRubBuzzPink and RunRubBuzzChirp together");
        captureView.SetBusy(false);
        Check(Control<Button>(captureView, "RunRubBuzz").IsEnabled && pinkButton.IsEnabled && chirpButton.IsEnabled,
            "idle state re-enables RunRubBuzz, RunRubBuzzPink and RunRubBuzzChirp together");
        var renderView = new RubBuzzView { RouteProvider = () => route, RenderDevicesProvider = () => devices };
        renderView.RefreshRenderDevices();
        Check((Control<ComboBox>(renderView, "RubBuzzRender").SelectedItem as RubBuzzRenderDevice)?.Id == "realtek", "view defaults replay to alternate Realtek device");
        var alternateRoute = route with { PlaybackId = "realtek" }; renderView.RouteProvider = () => alternateRoute;
        renderView.RefreshPlaybackRoute(alternateRoute.PlaybackId);
        Check((Control<ComboBox>(renderView, "RubBuzzRender").SelectedItem as RubBuzzRenderDevice)?.Id != "realtek", "changing test output reselects an alternate default replay device");
        // The route bar lives outside TabControl and refreshes from the current routing provider.
        var window = new MeasurementDetailsWindow { RouteProvider = () => route };
        window.RefreshRouteInfo();
        var tabs = LogicalTreeHelper.GetChildren((DependencyObject)window.Content).OfType<TabControl>().Single();
        string[] rattleTools = ["H10–H35", "Hạt rác", "Rò khí", "Pink nghe thử"];
        var nested = Control<TabControl>(window.RubBuzz, "RubBuzzToolTabs").Items.OfType<TabItem>().Select(t => t.Header?.ToString() ?? "").ToArray();
        Check(tabs.Items.OfType<TabItem>().Count(t => t.Header?.ToString() == MeasurementDetailsWindow.RubBuzzTabHeader) == 1
            && !tabs.Items.OfType<TabItem>().Any(t => t.Header?.ToString() is string h && (h.StartsWith("Rè Pink") || h.StartsWith("Rè Chirp") || h.StartsWith("Pink noise")
                || rattleTools.Any(h.StartsWith)))
            && rattleTools.All(tool => nested.Count(h => h.StartsWith(tool)) == 1),
            "measurement window has exactly one rattle tab; H10–H35, Hạt rác, Rò khí and listening pink live inside it");
        foreach (TabItem tab in tabs.Items) tabs.SelectedItem = tab;
        var bar = LogicalTreeHelper.GetChildren((DependencyObject)window.Content).OfType<TextBlock>().Single(t => t.Text.StartsWith("Ngõ phát:"));
        Check(bar.Text == MeasurementRouteInfo.Format(route), "route strip remains outside every tab");
        route = route with { PlaybackName = "Changed device", PlaybackChannel = 2 }; window.RefreshRouteInfo();
        Check(bar.Text.Contains("Changed device") && bar.Text.Contains("kênh 2"), "route provider refresh shows changed selection");
        // Visual evidence of the merged tab: standard run on top, advanced section expanded with the nested rattle tools.
        ((ScrollViewer)tabs.Items.OfType<TabItem>().Single(t => t.Header?.ToString() == MeasurementDetailsWindow.RubBuzzTabHeader).Content).Content = null;
        Control<Expander>(window.RubBuzz, "RubBuzzAdvanced").IsExpanded = true;
        Control<TabControl>(window.RubBuzz, "RubBuzzToolTabs").SelectedIndex = 1;
        Render(window.RubBuzz, Path.Combine(output, "rubbuzz-advanced.png"), 1600, 3300);
        window.Close(); view.Shutdown(); empty.Shutdown(); invalid.Shutdown(); diagnostic.Shutdown(); captureView.Shutdown(); renderView.Shutdown();
        Check(new[] { "rubbuzz-merged-fail.png", "rubbuzz-merged-empty.png", "rubbuzz-merged-invalid.png" }.All(f => File.Exists(Path.Combine(output, f))), "all three required merged-view PNGs rendered offline");
        Check(File.ReadAllLines(Path.Combine(output, "rubbuzz-merged-fail.png.layout.txt")).Single(line => line.Contains("RubBuzzDetails")).Contains("visible=True"),
            "PNG host keeps details/footer visible beyond monitor-height clipping");
    }
}
