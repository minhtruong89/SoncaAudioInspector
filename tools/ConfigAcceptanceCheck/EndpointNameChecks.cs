using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SoncaAudioInspector;

/// <summary>
/// Device-name check against Windows endpoints, the editor's "Lấy ngõ đang chọn" buttons and gate, the manual trial line
/// measurement and the C360 capture-rate rule. Synthetic endpoints only: no audio device, BLE or server.
/// </summary>
internal static class EndpointNameChecks
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        var c360 = C360ModelDefinition.Create().testItems.InOut;
        WindowsEndpointName Out(string name) => new("out:" + name, name, false);
        WindowsEndpointName In(string name) => new("in:" + name, name, true);
        var bench = new[] { Out("Speakers (6- USB Audio Device)"), Out("Speakers (USB Audio Device)"), Out("BT (KBEATBOX )"),
            In("Headset Microphone (C360 usb audio)"), In("Microphone (KT USB Audio)") };
        check(ModelEndpointCheck.Check(c360, bench).Count == 0, "Complete C360 bench reports no endpoint issue");

        // 12/11/2026 bench: hub dropped, only BT + Realtek left. Every missing name must be listed at once.
        var dropped = new[] { Out("BT (KBEATBOX )"), Out("Speakers (Realtek(R) Audio)"), In("Microphone (Realtek(R) Audio)") };
        var issues = ModelEndpointCheck.Check(c360, dropped);
        check(issues.Count == 3 && issues.Any(i => !i.Capture && i.Port == "MIC IN") && issues.Any(i => !i.Capture && i.Port == "LINE IN")
            && issues.Any(i => i.Capture && i.Port == C360ModelDefinition.CaptureName), "Every missing C360 endpoint is reported, not only the first: " + ModelEndpointCheck.Describe(issues));
        check(!issues.Any(i => i.Port is "BT" or "USB A"), "BT/USB A playback is not name-checked (BLE address / source switch)");
        check(issues.All(i => i.Suggestions.Count > 0) && ModelEndpointCheck.Describe(issues).Contains("Windows đang có"), "Missing endpoints list what Windows has");

        var typo = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(c360))!;
        typo.Devices.Output[C360ModelDefinition.CaptureName] = "Headset Mic (C360 usb audio)";
        var typoIssue = ModelEndpointCheck.Check(typo, bench).Single();
        check(typoIssue.Capture && typoIssue.Suggestions.Contains("Headset Microphone (C360 usb audio)") && typoIssue.Problem.Contains("sai tên"),
            "Misspelt recording name is reported with the real endpoint as suggestion");
        var twoInstances = bench.Append(In("Headset Microphone (C360 usb audio)") with { Id = "second-port" }).ToArray();
        check(ModelEndpointCheck.Check(c360, twoInstances).Single().Problem.Contains("khớp 2"), "Two active C360 recording instances are ambiguous, as in the plan");
        check(ModelEndpointCheck.Check(c360, bench.Where(e => !e.Capture).ToArray()).Single().Problem.Contains("không có thiết bị thu nào"),
            "No capture endpoint at all names the USB/hub dropout");
        var disabled = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(c360))!;
        foreach (var t in disabled.Tests.Where(t => t.SpeakerStage is "MicEffects" or "MicFrequencyShift" or "MicDry")) t.Enable = false;
        check(!ModelEndpointCheck.Check(disabled, dropped).Any(i => i.Port == "MIC IN"), "Disabled tests are not name-checked");
        var generic = new InOutConfig { Devices = new() { Input = new() { ["OUT1"] = "Analog Connector" }, Output = new() { ["MIC2"] = "FastTrack" } },
            Tests = new() { new TestConfig { id = "IO001", name = "x", PlaybackOut = "OUT1", RecordingIn = "MIC2" } } };
        check(ModelEndpointCheck.Check(generic, new[] { Out("Analog Connector 1/2 (4- FastTrack Pro)"), In("Analog Connector (4- FastTrack Pro)"), In("Analog Connector (5- FastTrack Pro)") }).Count == 0,
            "Generic models keep the first-contains rule of the line routes");

        CheckEditor(output, check, bench);
        CheckTrialManualLine(check);

        var rate = typeof(AudioRouting).GetMethod("C360CaptureRateProblem", Hidden)!;
        check(rate.Invoke(null, new object?[] { 44100, 44100, "C360", true }) == null, "44.1 kHz C360 capture is accepted");
        string problem = (string)rate.Invoke(null, new object?[] { 48000, 48000, "Headset Microphone (C360 usb audio)", true })!;
        check(problem.StartsWith("CẢNH BÁO") && problem.Contains("48000") && problem.Contains("44100") && problem.Contains("Vẫn lưu LINE CHUẨN"),
            "48 kHz capture warns with the fix and still allows the LINE CHUẨN (operator decision)");
        check(!((string)rate.Invoke(null, new object?[] { 48000, 48000, "C360", false })!).Contains("LINE CHUẨN"), "48 kHz product measurement only warns");
        check(((string)rate.Invoke(null, new object?[] { 48000, 44100, "Headset Microphone (C360 usb audio)", false })!).Contains("Audio enhancements: Off"),
            "44.1 kHz Default Format with a 48 kHz stream points at Windows Audio enhancements");    }

    private static void CheckEditor(string output, Action<bool, string> check, WindowsEndpointName[] bench)
    {
        var model = C360ModelDefinition.Create();
        model.testItems.InOut.Devices.Input["LINE IN"] = "Speakers (USB Audio Devise)";
        var editor = new ModelConfigEditorWindow(model)
        {
            WindowsEndpoints = () => bench,
            RoutingEndpointNames = () => ("Speakers (USB Audio Device)", "Headset Microphone (C360 usb audio)")
        };
        int applied = 0; editor.SaveTemporaryModel = _ => applied++;
        editor.SelectTest("IO004"); editor.Show(); editor.UpdateLayout();
        try
        {
            var playback = Tree<TextBox>(editor).Single(t => t.Name == "PlaybackDeviceName");
            var playbackStatus = Tree<TextBlock>(editor).Single(t => t.Name == "PlaybackEndpointStatus");
            check(playback.Text == "Speakers (USB Audio Devise)" && playbackStatus.Text.StartsWith("⚠") && playbackStatus.Text.Contains("Speakers (USB Audio Device)"),
                "Editor shows the misspelt LINE IN name with the real endpoint: " + playbackStatus.Text);

            var save = editor.GetType().GetMethod("SaveTemporary", Hidden)!;
            var error = (TextBlock)editor.GetType().GetField("_error", Hidden)!.GetValue(editor)!;
            save.Invoke(editor, new object[] { false });
            check(applied == 0 && error.Text.Contains("CHƯA dùng") && error.Text.Contains("LINE IN"), "Wrong device name blocks the first temporary save and names the port");
            save.Invoke(editor, new object[] { false });
            check(applied == 1, "Second press with the same issues still uses the configuration");

            Click(Tree<Button>(editor).Single(b => b.Name == "UseRoutingPlayback"));
            check(playback.Text == "Speakers (USB Audio Device)" && Tree<TextBlock>(editor).Single(t => t.Name == "PlaybackEndpointStatus").Text.StartsWith("✓"),
                "Lấy ngõ PHÁT copies the routing selection and the check turns green");
            Click(Tree<Button>(editor).Single(b => b.Name == "UseRoutingRecording"));
            var config = (ModelConfig)editor.GetType().GetMethod("ReadEditorModel", Hidden)!.Invoke(editor, null)!;
            check(config.testItems.InOut.Devices.Input["LINE IN"] == "Speakers (USB Audio Device)"
                && config.testItems.InOut.Devices.Output[C360ModelDefinition.CaptureName] == "Headset Microphone (C360 usb audio)",
                "Routing names reach the saved model configuration");
            save.Invoke(editor, new object[] { false });
            check(applied == 2 && !error.Text.Contains("CHƯA dùng"), "Matching names save on the first press");
            Program.Render(editor, Path.Combine(output, "editor-endpoint-tools.png"));

            editor.RoutingEndpointNames = () => (null, null);
            Click(Tree<Button>(editor).Single(b => b.Name == "UseRoutingPlayback"));
            check(error.Text.Contains("Chưa chọn thiết bị phát") && playback.Text == "Speakers (USB Audio Device)", "Empty routing selection is reported, field kept");

            editor.SelectTest("IO007"); editor.UpdateLayout();
            check(Tree<TextBlock>(editor).Single(t => t.Name == "PlaybackEndpointStatus").Text.Contains("địa chỉ loa BLE"), "BT port explains BLE address matching");
        }
        finally { editor.Close(); }
    }

    private static void CheckTrialManualLine(Action<bool, string> check)
    {
        var io = C360ModelDefinition.Create().testItems.InOut;
        var inventory = io.Devices.Input.Select(p => new C360ModelEndpoint(p.Key, p.Value, false))
            .Concat(io.Devices.Output.Select(p => new C360ModelEndpoint(p.Key, p.Value, true))).ToArray();
        var plan = C360ModelPlan.Create(io, inventory, new("analog", "usb", "capture"));
        var line = plan.Configuration.Tests.Single(t => t.SpeakerStage == "LineIn");
        string empty = Path.Combine(Path.GetTempPath(), "sonca-no-trial-" + Guid.NewGuid().ToString("N"));
        using (ResultPersistencePolicy.BeginTrial(true))
        {
            var gated = new CountingBench();
            var auto = new C360ReferenceBench(gated, plan, "C360", empty, "offline", false, 3).CaptureAsync(C360ElectronicStage.LineIn, plan.SetupFor(line), default).GetAwaiter().GetResult();
            check(gated.Captures == 0 && auto.Summary.Contains("LINE CHUẨN THỬ"), "Trial Auto Test still needs a trial reference before any stimulus");
            var manual = new CountingBench();
            var shown = new C360ReferenceBench(manual, plan, "C360", empty, "offline", false, 3, requireTrialReference: false)
                .CaptureAsync(C360ElectronicStage.LineIn, plan.SetupFor(line), default).GetAwaiter().GetResult();
            check(manual.Captures == 1 && shown.Status == "INVALID" && shown.Summary.Contains("không kết luận"),
                "Trial manual ĐO LINE measures and shows the line, never a verdict without a reference");
        }
        check(!Directory.Exists(empty), "Manual trial line without a reference writes no reference");
    }

    private sealed class CountingBench : IC360ElectronicBench
    {
        public int Captures; public string DeviceIdentity => "offline";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new Exception("Unexpected device read");
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new Exception("Unexpected device write");
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        { Captures++; return Task.FromResult(new C360ElectronicResult(stage, "PASS", "fixture capture")); }
        public void StopAudio() { }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Tree<T>(child)) yield return nested;
        }
    }
}
