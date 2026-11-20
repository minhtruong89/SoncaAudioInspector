using System.IO;
using System.Windows;

namespace SoncaAudioInspector;

/// <summary>Operator prompts of the C360 Auto Test: sound card port sync / cable check on no signal (v1.5.6), automatic LINE CHUẨN, Bluetooth removal.</summary>
public partial class AudioRouting
{
    /// <summary>Test hooks. Offline checks with a fake bench leave these null, so the prompts never appear there.</summary>
    internal Func<C360ElectronicStage, C360ElectronicResult, int, CancellationToken, Task<bool>>? NoSignalRetryPrompt { get; set; }
    internal Func<IReadOnlyList<TestConfig>, Task<bool>>? AutoReferencePrompt { get; set; }
    internal Func<Task<bool>>? RemoveDevicePrompt { get; set; }
    private string? _endpointLossReason;
    private string _soundCardReport = "";

    private Task<bool> ConfirmAsync(string message, string title, string yes, string no)
        => Dispatcher.InvokeAsync(() => ModernMessageBox.ShowConfirmation(Window.GetWindow(this) ?? Application.Current.MainWindow, message, title, yes, no)).Task;

    private Task<bool> ConfirmAsync(string message, string title, string yes, string no, string icon, System.Windows.Media.Color color)
        => Dispatcher.InvokeAsync(() =>
        {
            var dialog = ModernMessageBox.CreateConfirmation(message, title, yes, no);
            dialog.TxtIcon.Text = icon; dialog.TxtIcon.Foreground = new System.Windows.Media.SolidColorBrush(color);
            var owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
            if (owner?.IsVisible == true) dialog.Owner = owner;
            dialog.ShowDialog(); return dialog.Accepted;
        }).Task;

    /// <summary>Sound card port sync of the running Auto Test (owner 2026-11-19); null outside a real C360 run.</summary>
    private C360PortSync? _c360PortSync;
    /// <summary>Test hook: enables the port sync with a fake bench (offline checks).</summary>
    internal bool C360PortSyncForTests { get; set; }

    private C360PortSync? CreateC360PortSync(C360ModelPlan plan)
    {
        if (C360BenchFactory != null && !C360PortSyncForTests) return null;
        var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in plan.Configuration.Tests.Where(t => t.IsEnabled && C360ModelDefinition.UsesAnalogPlayback(C360ModelDefinition.Stage(t))))
            if (plan.PlaybackIds.TryGetValue(C360ModelDefinition.Stage(test), out string? id) && id.Length > 0) ports.TryAdd(test.PlaybackOut, id);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (port, id) in ports) names[id] = plan.Configuration.Devices.Input.GetValueOrDefault(port, port);
        if (C360BenchFactory == null)
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                foreach (string id in ports.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                    using (var device = enumerator.GetDevice(id)) names[id] = device.FriendlyName;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { }
        // Two or more distinct outputs are needed to sync anything.
        return ports.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ? new C360PortSync(ports, names) : null;
    }

    private Func<C360ElectronicStage, C360ElectronicResult, int, CancellationToken, Task<bool>>? C360NoSignalPrompt(C360ModelPlan plan)
    {
        if (NoSignalRetryPrompt != null) return NoSignalRetryPrompt;
        if (C360BenchFactory != null) return null;
        return async (stage, result, retry, token) =>
        {
            token.ThrowIfCancellationRequested();
            var test = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
            if (C360SignalCheck.IsWeakFeedbackLoop(stage, result))
            {
                string weak = $"{test.id} · {test.name}: {C360SignalCheck.Describe(result)} — mic đo nghe loa quá nhỏ nên chưa đo được FBX.\n\n"
                    + "Đặt mic đo gần loa hơn, hướng thẳng vào loa (không che mic), rồi bấm THỬ LẠI.\n"
                    + "Nếu vẫn không hú: kiểm tra mức MIC trên loa và ngõ " + test.PlaybackOut + ".\n\n"
                    + "Bấm BỎ QUA để giữ kết quả chưa đo được và đo tiếp."
                    + (retry > 1 ? $"\n(Lần thử lại thứ {retry})" : "");
                AppendLog("Gain trước hú", $"{test.id}: {C360SignalCheck.Describe(result)} — hỏi đặt mic gần loa hơn (lần {retry}).");
                bool closer = await ConfirmAsync(weak, "Chưa hú — đặt mic đo gần loa hơn", "Thử lại", "Bỏ qua");
                AppendLog("Gain trước hú", closer ? $"{test.id}: người đo bấm Thử lại." : $"{test.id}: người đo bấm Bỏ qua.");
                return closer;
            }
            var inputs = plan.Configuration.Devices.Input;
            var sync = _c360PortSync;
            // The app already tried every configured sound card output for this port (C360PortSync); what is left is the cable / speaker.
            string tried = sync == null ? inputs.GetValueOrDefault(test.PlaybackOut, test.PlaybackOut)
                : string.Join(", ", sync.Configured.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(sync.Name));
            string message = $"{test.id} · {test.name}: loa không nhận tín hiệu ở ngõ {test.PlaybackOut} ({C360SignalCheck.Describe(result)}).\n"
                + $"App đã tự thử phát qua: {tried}.\n\n"
                + $"Kiểm tra cáp {test.PlaybackOut} từ sound card tới loa (cắm chặt, đúng jack), mức trên sound card và nguồn loa, rồi bấm THỬ LẠI.\n"
                + (_soundCardReport.Length > 0 ? "Sound card lúc bắt đầu: " + _soundCardReport + "\n" : "")
                + "\nBấm BỎ QUA để giữ kết quả không có tín hiệu và đo tiếp bài sau."
                + (retry > 1 ? $"\n(Lần thử lại thứ {retry})" : "");
            AppendLog("Không có tín hiệu", $"{test.id}: {C360SignalCheck.Describe(result)} — đã thử {tried}; hỏi kiểm tra cáp {test.PlaybackOut} (lần {retry}).");
            bool again = await ConfirmAsync(message, $"Không có tín hiệu — kiểm tra cáp {test.PlaybackOut}", "Thử lại", "Bỏ qua", "🔌", System.Windows.Media.Color.FromRgb(245, 158, 11));
            AppendLog("Không có tín hiệu", again ? $"{test.id}: người đo bấm Thử lại." : $"{test.id}: người đo bấm Bỏ qua.");
            return again;
        };
    }

    private IC360ElectronicBench WithNoSignalRetry(IC360ElectronicBench bench, C360ModelPlan plan)
    {
        var ask = C360NoSignalPrompt(plan);
        if (ask == null && _c360PortSync == null) return bench;
        return new C360NoSignalRetryBench(bench, ask ?? ((_, _, _, _) => Task.FromResult(false)), sync: _c360PortSync);
    }

    /// <summary>Asks once per Auto Test; the answer covers every stage without a LINE CHUẨN in this run.</summary>
    private sealed class AutoReferenceDecision(AudioRouting owner)
    {
        private bool? _answer;
        public bool Accepted => _answer == true;
        public async Task<bool> AskAsync(IReadOnlyList<TestConfig> missing)
        {
            if (_answer is bool known) return known;
            Func<IReadOnlyList<TestConfig>, Task<bool>>? ask = owner.AutoReferencePrompt;
            if (ask == null && owner.C360BenchFactory != null) { _answer = false; return false; }
            ask ??= tests =>
            {
                string list = string.Join("\n", tests.Select(t => $"   • {t.id} · {t.name}"));
                return owner.ConfirmAsync($"Chưa có LINE CHUẨN khớp cấu hình hiện tại cho:\n{list}\n\n"
                    + "Bạn có muốn tự động lấy LINE CHUẨN rồi đo luôn không?\n\n"
                    + "CÓ: dùng loa đang đặt trên gá làm LINE CHUẨN (mỗi bài thiếu chuẩn đo thêm 2–3 lượt để lưu), rồi đo và kết luận ngay. "
                    + "Chỉ chọn CÓ khi đây là loa tốt đã biết; các loa sau sẽ được so với loa này.\n"
                    + "KHÔNG: đo như cũ; bài thiếu chuẩn sẽ không kết luận.",
                    "Chưa có LINE CHUẨN", "Có — lấy chuẩn rồi đo", "Không");
            };
            bool yes = await ask(missing);
            _answer = yes;
            owner.AppendLog("LINE CHUẨN", yes ? "Người đo chọn tự lấy LINE CHUẨN từ loa đang đo rồi đo luôn." : "Người đo chọn không tự lấy LINE CHUẨN.");
            return yes;
        }
    }

    private async Task<bool> AskRemoveDeviceAsync(string serial)
    {
        if (RemoveDevicePrompt != null) return await RemoveDevicePrompt();
        if (C360BenchFactory != null) return false;
        bool remove = await ConfirmAsync($"Đã đo xong {serial}.\n\nBạn có muốn xóa thiết bị Bluetooth của loa vừa đo khỏi Windows không?\n"
            + "Nên xóa trước khi đo loa tiếp theo để Windows không tự nối lại loa cũ. Kết nối BLE điều khiển vẫn được giữ.",
            "Xóa thiết bị Bluetooth?", "Xóa thiết bị", "Giữ lại");
        AppendLog("Bluetooth", remove ? "Người đo chọn xóa thiết bị Bluetooth sau khi đo xong." : "Người đo chọn giữ thiết bị Bluetooth.");
        return remove;
    }

    /// <summary>Durable Auto Test log: the UI log box alone left no trace of why a station run stopped (IO007, 2026-11-18).</summary>
    private void AppendSessionLog(string source, string message)
    {
        if (_autoTestSessionFolder is not string folder) return;
        try
        {
            ResultPersistencePolicy.CreateDirectory(folder);
            ResultPersistencePolicy.AppendAllText(Path.Combine(folder, "auto-test-log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }

    public void LogEndpointEvent(string message) => AppendLog("Thiết bị Windows", message);

    /// <summary>Silent pre-check (user request 2026-11-18): before any stimulus, report which sound card pairs Windows has for the
    /// analog ports, e.g. "LINE IN = Analog 1/2 ✓ · MIC IN = Analog 3/4 ✓". Which output really reaches which speaker jack is
    /// learnt from the signal during the run (C360PortSync), never asked as a sound card button.</summary>
    public static string DescribeAnalogPorts(InOutConfig config, IReadOnlyList<C360ModelEndpoint> endpoints)
    {
        var ports = config.Tests.Where(t => t.IsEnabled && C360ModelDefinition.UsesAnalogPlayback(C360ModelDefinition.Stage(t)))
            .Select(t => t.PlaybackOut).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parts = new List<string>();
        foreach (string port in ports)
        {
            string name = config.Devices.Input.GetValueOrDefault(port, port);
            var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d)\s*/\s*(\d)");
            string pair = match.Success ? $"Analog {match.Groups[1].Value}/{match.Groups[2].Value}" : name;
            var renders = endpoints.Where(e => !e.Capture).ToArray();
            bool present = renders.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                || renders.Count(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) == 1
                || renders.Count(e => C360ModelPlan.EndpointKey(e.Name) == C360ModelPlan.EndpointKey(name)) == 1;
            parts.Add($"{port} = {pair} {(present ? "✓ có trong Windows" : "✗ Windows chưa thấy")}");
        }
        return parts.Count == 0 ? "" : "Sound card: " + string.Join(" · ", parts);
    }

    private string CheckAnalogPortsQuietly(InOutConfig config)
    {
        try
        {
            IReadOnlyList<C360ModelEndpoint> endpoints;
            if (C360EndpointProvider is { } provide) endpoints = provide();
            else
            {
                var list = new List<C360ModelEndpoint>();
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    using (device) list.Add(new(device.ID, device.FriendlyName, false));
                endpoints = list;
            }
            string report = DescribeAnalogPorts(config, endpoints);
            if (report.Length > 0) AppendLog("Sound card", report + ". Không có tín hiệu ở một ngõ thì app tự thử các ngõ sound card khác của cấu hình.");
            return report;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        { AppendLog("Sound card", "Chưa kiểm được ngõ sound card: " + ex.Message); return ""; }
    }
}
