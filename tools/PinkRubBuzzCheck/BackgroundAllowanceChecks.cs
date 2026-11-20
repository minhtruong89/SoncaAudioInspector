using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SoncaAudioInspector;

static partial class Program
{
    static T Expect<T>(Func<Task> action) where T : Exception
    {
        try { Task.Run(action).GetAwaiter().GetResult(); }
        catch (T error) { return error; }
        throw new Exception("FAIL expected " + typeof(T).Name);
    }

    static float[] ScaledFloor(float[] background, double targetRmsDbFs)
    {
        int skip = (int)(PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds * Rate);
        double rms = Math.Sqrt(background.Skip(skip).Average(v => (double)v * v));
        double gain = Math.Pow(10, targetRmsDbFs / 20) / rms;
        return background.Select(v => (float)(v * gain)).ToArray();
    }

    static void BackgroundAllowanceChecks(string output, PinkRubBuzzRun pink, float[] bg)
    {
        // Station file: missing/corrupt/out-of-range → factory gate; caps enforced on save.
        string dir = Path.Combine(output, "background-allowance"); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, BackgroundAllowance.FileName);
        Check(BackgroundAllowance.Load(file).IsDefault, "allowance: missing file → factory gate −55 dBFS RMS / 0.05 FS peak");
        var raised = new BackgroundAllowance(-45, BackgroundAllowance.PeakFromDbFs(-20)); raised.Save(file);
        Check(BackgroundAllowance.Load(file) == raised, "allowance: station value round-trips");
        File.WriteAllText(file, "{\"RmsDbFs\": -20, \"PeakLimit\": 0.9}");
        Check(BackgroundAllowance.Load(file).IsDefault, "allowance: out-of-range file falls back to the factory gate");
        File.WriteAllText(file, "not json");
        Check(BackgroundAllowance.Load(file).IsDefault, "allowance: corrupt file falls back to the factory gate");
        Reject(() => new BackgroundAllowance(-30).Save(file), "allowance: RMS above −35 dBFS refused");
        Reject(() => new BackgroundAllowance(-60).Save(file), "allowance: stricter-than-factory RMS refused (default is the floor)");
        Check(BackgroundAllowance.Default.AnalysisRmsLimitDbFs == -50 && raised.AnalysisRmsLimitDbFs == -40, "allowance: analyses keep 5 dB headroom (−50 at factory)");

        // Gate numbers on the real fixture floor scaled to −47 dBFS RMS.
        var noisy = ScaledFloor(bg, -47);
        var check = SignalDiagnostics.CheckBackground(noisy, Rate, PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, null, rejectDigitalSilence: true);
        Check(!check.IsQuiet && check.RmsFailed && check.CanRaiseAllowance && Math.Abs(check.RmsDbFs + 47) < .05, "gate: −47 dBFS floor refused at factory and raisable");
        var suggestion = BackgroundAllowance.Default.SuggestFor(check);
        Check(suggestion.RmsDbFs == Math.Ceiling(check.RmsDbFs + 3)
            && SignalDiagnostics.CheckBackground(noisy, Rate, PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, suggestion, true).IsQuiet,
            "gate: suggested allowance = measured + 3 dB and accepts the floor");
        var loud = SignalDiagnostics.CheckBackground(ScaledFloor(bg, -20), Rate, PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, null, true);
        Check(BackgroundAllowance.Default.SuggestFor(loud).RmsDbFs == BackgroundAllowance.MaxRmsDbFs, "gate: suggestion capped at −35 dBFS");
        var silence = SignalDiagnostics.CheckBackground(new float[Rate * 3], Rate, PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, raised, true);
        Check(!silence.IsQuiet && silence.IsDigitalSilence && !silence.CanRaiseAllowance && silence.Reason.Contains("Privacy"),
            "gate: all-zero floor = blocked mic (privacy/mute), not raisable");
        Check(SignalDiagnostics.QuietBackground(new float[Rate * 3], Rate), "gate: default IMD/compression gate unchanged for a zero floor");

        // Coordinator loop: dialog raises the allowance → a fresh background is captured and accepted.
        int asked = 0, captured = 0; var current = BackgroundAllowance.Default;
        var gated = Task.Run(() => RubBuzzManualCapture.GateBackgroundAsync(3, (s, ct) => { captured++; return Task.FromResult(new RubBuzzCapturedAudio(noisy, Rate)); },
            () => current, (c, ct) => { asked++; current = current.SuggestFor(c); return Task.FromResult(true); }, () => { }, CancellationToken.None)).GetAwaiter().GetResult();
        Check(asked == 1 && captured == 2 && gated.Allowance == current && !current.IsDefault, "loop: noisy → dialog raises allowance → re-captured background accepted");
        asked = 0;
        var refused = Expect<RubBuzzBackgroundException>(() => RubBuzzManualCapture.GateBackgroundAsync(3, (s, ct) => Task.FromResult(new RubBuzzCapturedAudio(noisy, Rate)),
            () => BackgroundAllowance.Default, (c, ct) => { asked++; return Task.FromResult(false); }, () => { }, CancellationToken.None));
        Check(asked == 1 && refused.Check.RmsFailed && refused.Message.Contains("RMS") && refused.Message.Contains("tăng nền cho phép"), "loop: cancelled dialog → refusal carries the numbers");
        asked = 0;
        var blocked = Expect<RubBuzzBackgroundException>(() => RubBuzzManualCapture.GateBackgroundAsync(3, (s, ct) => Task.FromResult(new RubBuzzCapturedAudio(new float[Rate * 3], Rate)),
            () => raised, (c, ct) => { asked++; return Task.FromResult(true); }, () => { }, CancellationToken.None));
        Check(asked == 0 && blocked.Check.IsDigitalSilence, "loop: digital silence never offers a higher allowance");

        // Analyses follow the allowance: the same −47 dBFS floor is "abnormal" at factory, accepted when raised.
        var atFactory = PinkRubBuzzAnalysis.Analyze(pink.Captures, noisy, Rate, pink.Settings);
        var atRaised = PinkRubBuzzAnalysis.Analyze(pink.Captures, noisy, Rate, pink.Settings, suggestion.AnalysisRmsLimitDbFs);
        Check(atFactory.Reason.Contains("Nền thu bất thường") && !atRaised.Reason.Contains("Nền thu bất thường"), "analysis: background limit moves with the allowance");

        // Manual run records a raised allowance in the result route; the factory gate leaves the route unchanged.
        var source = new TestConfig { id = "allowance-test", RubBuzzMode = "PinkNoise", TestRubBuzz = true };
        var context = new AdditionalTestContext(44100, Rate, 1, "fake Out / Mic") { AcquisitionKey = "offline-allowance", ReferenceDirectory = Path.Combine(output, "allowance-standards") };
        RubBuzzManualResult Manual(Func<BackgroundAllowance>? allowance)
        {
            int i = 0;
            return Task.Run(() => RubBuzzManualCapture.RunAsync(source, RubBuzzSoundChoice.Pink, context,
                (s, ct) => Task.FromResult(new RubBuzzCapturedAudio(bg, Rate)),
                (step, ct) => Task.FromResult(new RubBuzzCapturedAudio(pink.Captures[i++ % pink.Captures.Length], Rate)),
                () => { }, _ => { }, () => { }, CancellationToken.None, allowance)).GetAwaiter().GetResult();
        }
        Check(Manual(() => raised).Pink!.Route.Contains("nền cho phép RMS") && !Manual(null).Pink!.Route.Contains("nền cho phép"), "result route records a raised allowance only");

        // Failure guide: every stop reason maps to a title and steps; cancel / handled noisy floor → no popup.
        RubBuzzFailureHelp? Help(Exception inner) => RubBuzzFailureGuide.Explain(new InvalidOperationException("Không hoàn tất đo Rè: x", inner), () => "• Zalo (PID 1) — thu: Mic");
        void Maps(Exception inner, string title) { var h = Help(inner); Check(h != null && h.Title.Contains(title) && h.Steps.Count >= 2 && h.Body.Contains("Cách xử lý"), $"guide: {inner.GetType().Name} → {title}"); }
        Maps(new COMException("busy", unchecked((int)0x8889000A)), "ứng dụng khác");
        Check(Help(new COMException("busy", unchecked((int)0x8889000A)))!.Message.Contains("Zalo"), "guide: device-in-use lists the apps holding the endpoint");
        Maps(new COMException("no exclusive", unchecked((int)0x8889000E)), "Exclusive");
        Maps(new NotSupportedException("Thiết bị thu không hỗ trợ WASAPI Exclusive ở 44100 Hz."), "định dạng");
        Maps(new COMException("gone", unchecked((int)0x88890004)), "Mất kết nối");
        Maps(new IOException("Windows vừa ngắt hoặc khởi tạo lại ngõ thu"), "Mất kết nối");
        Maps(new UnauthorizedAccessException(), "quyền");
        Maps(new TimeoutException(), "đúng hạn");
        Maps(new InvalidOperationException("Rate thu đổi giữa các lượt; không dùng dữ liệu dở dang."), "Định dạng thu");
        Maps(new InvalidOperationException("Route/gain/model/rate đã đổi; không dùng dữ liệu dở dang."), "Route/âm lượng");
        Maps(new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát/thu."), "Mono");
        Maps(new ArgumentException("Pink Rè cần ngõ phát 44,1/48 kHz và mic thu ≥ 32 kHz (phân tích 4–12 kHz)."), "Sample rate");
        Maps(new InvalidOperationException("Đang đo/chuyển model; chờ hoàn tất trước khi đo rè."), "bận");
        Maps(new InvalidOperationException("Chưa chọn ngõ thu."), "Chưa chọn");
        Maps(new RubBuzzBackgroundException(silence), "im lặng số");
        Maps(new RubBuzzBackgroundException(SignalDiagnostics.CheckBackground(new float[10], Rate, 0, null, true)), "không trả dữ liệu");
        Maps(new Exception("unexpected"), "Không hoàn tất");
        Check(Help(new RubBuzzBackgroundException(check)) == null && RubBuzzFailureGuide.Explain(new OperationCanceledException()) == null,
            "guide: handled noisy floor and cancel raise no extra popup");

        // Dialog: parsing, causes, rendered layout, and the save path through a real modal ShowDialog.
        Check(RubBuzzBackgroundDialog.TryParseDbFs("−45,5", out double a) && a == -45.5 && RubBuzzBackgroundDialog.TryParseDbFs("45", out double b) && b == -45
            && !RubBuzzBackgroundDialog.TryParseDbFs("abc", out _), "dialog: accepts −45,5 / 45 / -45 and rejects text");
        var dc = SignalDiagnostics.CheckBackground(bg.Select(v => v + .02f).ToArray(), Rate, PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, null, true);
        Check(dc.IsDcDominated && RubBuzzBackgroundDialog.Causes(dc).Contains("Lệch DC") && RubBuzzBackgroundDialog.Causes(check).Contains("Thùng cách âm"), "dialog: causes follow the failing limit");
        var preview = new RubBuzzBackgroundDialog(check);
        var surface = (FrameworkElement)preview.Content; preview.Content = null;
        Render(new Border { Child = surface, Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)) }, Path.Combine(output, "background-dialog.png"), 680, 560);
        Check(File.Exists(Path.Combine(output, "background-dialog.png")), "dialog: rendered offline");
        var dialog = new RubBuzzBackgroundDialog(check) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 0 };
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            Control<Button>(dialog, "SaveBackgroundAllowance").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        dialog.ShowDialog();
        Check(dialog.Result == RubBuzzBackgroundDialog.Choice.SaveAndRetry && dialog.Allowance == suggestion, "dialog: Save returns the suggested allowance exactly");
    }
}
