using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

/// <summary>One result, one acquisition and one replay selector for both acoustic rattle tests.</summary>
public sealed class RubBuzzView : UserControl
{
    public const string Title = "RÈ (Rub & Buzz)";
    private const string IdleMessage = "Bấm Đo chuẩn: nền 3 s rồi mỗi tín hiệu 3 lượt, thu Exclusive, phát Shared. Đây là bài dùng để kết luận và lưu line chuẩn.";
    private readonly TextBlock _status = DiagnosticViewStyle.Text(Title + " — CHƯA ĐO", 21);
    private readonly TextBlock _message = DiagnosticViewStyle.Text(IdleMessage, 13);
    private readonly TextBlock _pinkMessage = DiagnosticViewStyle.Text("Chưa có kết quả Pink.", 13);
    private readonly TextBlock _chirpMessage = DiagnosticViewStyle.Text("Chưa có kết quả Chirp.", 13);
    private readonly TextBlock _standard = DiagnosticViewStyle.Text("", 12);
    private readonly TextBlock _detailText = DiagnosticViewStyle.Text("Chưa có dữ liệu.", 13);
    private readonly ComboBox _sound = new() { Name = "RubBuzzSound", MinWidth = 330, Margin = new Thickness(4), ItemsSource = RubBuzzManualCapture.Labels, SelectedIndex = (int)RubBuzzSoundChoice.LevelDiagnostic };
    private readonly Expander _advanced = new() { Name = "RubBuzzAdvanced", Margin = new Thickness(4, 10, 4, 4), Foreground = Brushes.WhiteSmoke,
        Header = "Chẩn đoán nâng cao — tìm nguyên nhân, không dùng để kết luận ĐẠT/KHÔNG ĐẠT" };
    /// <summary>Other rattle-related tools of the measurement window (H10–H35, loose particle, air leak, listening pink).</summary>
    public StackPanel AdvancedTools { get; } = new();
    private RubBuzzSoundChoice _standardChoice = RubBuzzSoundChoice.PinkAndChirp;
    private readonly ComboBox _listen = new() { Name = "RubBuzzListen", MinWidth = 270, Margin = new Thickness(4) };
    private readonly ComboBox _render = new() { Name = "RubBuzzRender", MinWidth = 220, Margin = new Thickness(4) };
    private readonly DataGrid _pinkTable = Table(), _chirpTable = Table();
    private readonly WpfPlot _pinkPlot = new() { Height = 220 }, _chirpPlot = new() { Height = 240 };
    private readonly StackPanel _pinkSection = new(), _chirpSection = new();
    private readonly Expander _details = new() { Name = "RubBuzzDetails", Header = "Chi tiết", Margin = new Thickness(4), Foreground = Brushes.WhiteSmoke };
    // Owner 2026-11-18: only the chirp frequency chart stays visible; the Pink/Chirp tables (and the Pink envelope) open on demand.
    private readonly Expander _tables = new() { Name = "RubBuzzTables", Header = "Bảng Pink / Chirp và đồ thị Pink", Margin = new Thickness(4), Foreground = Brushes.WhiteSmoke, IsExpanded = false };
    public bool TablesExpanded => _tables.IsExpanded;
    private readonly CheckBox _good = new() { Content = "Đây là loa TỐT đã nghe kiểm tra, đúng gá/mic/âm lượng như khi test line", Foreground = Brushes.WhiteSmoke, Margin = new Thickness(4) };
    private readonly Button _measure, _measurePink, _measureChirp, _diagnose, _stop, _play, _stopPlay, _save, _export;
    private readonly RubBuzzAudioPlayer _player = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1 / 30.0) };
    private ScottPlot.Plottables.VerticalLine? _pinkCursor, _chirpCursor;
    private PinkRubBuzzRun? _pink;
    private ChirpRubBuzzRun? _chirp;
    private RubBuzzManualResult? _diagnostics;
    private readonly StackPanel _diagnosisSection = new() { Name = "RubBuzzDiagnosisSection" };
    private readonly TextBlock _capturePathWarning = DiagnosticViewStyle.Text("", 13, "#FCD34D");
    private readonly TextBlock _diagnosisText = DiagnosticViewStyle.Text("", 13);
    private readonly TextBlock _diagnosisBrief = DiagnosticViewStyle.Text("", 15, "#FDE68A");
    private readonly Expander _diagnosisDetails = new() { Name = "RubBuzzDiagnosisDetails", Margin = new Thickness(4), Foreground = Brushes.WhiteSmoke,
        Header = "Chi tiết: 10 dấu hiệu, các khả năng, kiểm tra tiếp" };
    private readonly TextBlock _impulsiveText = DiagnosticViewStyle.Text("", 13), _broadbandText = DiagnosticViewStyle.Text("", 13);
    private readonly DataGrid _signsTable = DiagnosisTable(), _levelTable = Table(), _broadbandTable = Table();
    private readonly WpfPlot _impulsivePeakPlot = new() { Height = 210 }, _impulsiveCrestPlot = new() { Height = 210 };
    private readonly TextBox _hpMultiple = Option("20"), _wideLow = Option("20"), _wideHigh = Option("20000"),
        _wideCrest = Option("12"), _wideLevel = Option("-20"), _wideRepeats = Option("3");
    public Func<CapturePathEnhancementStatus?>? CapturePathProvider { get; set; }
    public BroadbandPinkSettings BroadbandSettings => new(LowHz: Number(_wideLow), HighHz: Number(_wideHigh),
        LevelRmsDbFs: Number(_wideLevel), CrestTargetDb: Number(_wideCrest), Repeats: (int)Number(_wideRepeats));
    public ChirpImpulsiveSettings ImpulsiveSettings => new(HighPassMultiple: Number(_hpMultiple));
    private readonly WrapPanel _impulsiveOptions = new() { Name = "RubBuzzImpulsiveOptions" }, _broadbandOptions = new() { Name = "RubBuzzBroadbandOptions" };
    // Dark like the ImpulseTheme controls of this window (the theme also styles TextBox; explicit here for offscreen renders).
    private static TextBox Option(string value) => new() { Text = value, Width = 70, Margin = new Thickness(4), Padding = new Thickness(4, 2, 4, 2),
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)), Foreground = new SolidColorBrush(Color.FromRgb(228, 228, 231)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(69, 69, 77)), CaretBrush = Brushes.WhiteSmoke, VerticalContentAlignment = VerticalAlignment.Center };
    private static double Number(TextBox text) => double.Parse(text.Text.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
    private PinkRubBuzzLimits _pinkLimits = new();
    private ChirpRubBuzzLimits _chirpLimits = new();
    private string _directory = "";
    private bool _busy, _diagnostic, _updating;
    private CancellationTokenSource? _cancellation;
    private List<RubBuzzListeningItem> _items = [];
    private object? _selectionKey;
    private string? _lastTestPlaybackId;
    private RubBuzzSoundChoice _resultChoice;
    public Func<RubBuzzSoundChoice, CancellationToken, Task<RubBuzzManualResult>>? Runner { get; set; }
    public Func<MeasurementRouteInfo?>? RouteProvider { get; set; }
    public Func<RubBuzzRenderDevice[]>? RenderDevicesProvider { get; set; }
    public bool DetailsExpanded => _details.IsExpanded;
    public IReadOnlyList<RubBuzzListeningItem> ListeningItems => _items;
    public int SelectedRepeat => (_listen.SelectedItem as RubBuzzListeningItem)?.Repeat ?? -1;
    public bool CanSaveStandard => _save.IsEnabled;
    private sealed record Row(string ThongSo, string LoaDo, string LineChuan, string ChoPhep);

    public RubBuzzView()
    {
        var root = new StackPanel { Margin = new Thickness(12) }; Content = root;
        root.Children.Add(_status); root.Children.Add(_message); root.Children.Add(_capturePathWarning);
        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; root.Children.Add(tools);
        _measure = Button(tools, "", "RunRubBuzz", () => Capture(_standardChoice));
        _measure.FontWeight = FontWeights.SemiBold; _measure.FontSize = 14; _measure.Padding = new Thickness(14, 8, 14, 8);
        _measure.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)); _measure.Foreground = Brushes.White;
        _measure.BorderBrush = new SolidColorBrush(Color.FromRgb(87, 152, 239)); StandardLabel();
        _measurePink = Button(tools, "▶ Chỉ Pink (3 lượt)", "RunRubBuzzPink", () => Capture(RubBuzzSoundChoice.Pink));
        _measureChirp = Button(tools, "▶ Chỉ Chirp (3 lượt)", "RunRubBuzzChirp", () => Capture(RubBuzzSoundChoice.Chirp));
        _stop = Button(tools, "■ Dừng", "StopRubBuzz", () => { Cancel(); return Task.CompletedTask; });
        // Diagnostic sounds, options and the other rattle tools sit in one collapsed section below the result.
        var advanced = new StackPanel(); _advanced.Content = advanced;
        var diagnosticTools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; advanced.Children.Add(diagnosticTools);
        diagnosticTools.Children.Add(DiagnosticViewStyle.Text("Âm thanh chẩn đoán:", 13)); diagnosticTools.Children.Add(_sound);
        _diagnose = Button(diagnosticTools, "▶ Đo chẩn đoán (3 lượt)", "RunRubBuzzDiagnostic", () => Capture((RubBuzzSoundChoice)_sound.SelectedIndex));
        advanced.Children.Add(_impulsiveOptions); advanced.Children.Add(_broadbandOptions);
        advanced.Children.Add(DiagnosticViewStyle.Text("Chẩn đoán theo mức (−30/−24/−18/−12 dBFS RMS) dùng để chọn mức phát của bài: cao hơn ngưỡng khởi phát của loa lỗi khoảng 6 dB, "
            + "loa tốt vẫn sạch, chưa nén. Kết quả chẩn đoán không lưu/không dùng line chuẩn.", 12, "#A1A1AA"));
        advanced.Children.Add(AdvancedTools);
        _advanced.Expanded += (_, _) => Dispatcher.BeginInvoke(() => _advanced.BringIntoView(), DispatcherPriority.Loaded);
        void Setting(Panel panel, string label, TextBox field, string tip)
        {
            var caption = DiagnosticViewStyle.Text(label, 12); caption.ToolTip = tip; field.ToolTip = tip;
            panel.Children.Add(caption); panel.Children.Add(field);
        }
        _impulsiveOptions.Children.Add(DiagnosticViewStyle.Text("Méo xung Chirp (chẩn đoán):", 12, "#93C5FD"));
        Setting(_impulsiveOptions, "Bội số high-pass N (5–40):", _hpMultiple,
            "Phân tích méo xung kiểu IEC 60268-21 / Klippel: lọc high-pass bám theo chirp, cắt tại N × tần số đang quét (tối thiểu 1 kHz), "
            + "rồi đo đỉnh/crest phần dư. N lớn = chỉ giữ hài bậc rất cao (rè/va đập); N nhỏ = giữ cả méo thường. Mặc định 20. Không đổi ĐẠT/KHÔNG ĐẠT.");
        _broadbandOptions.Children.Add(DiagnosticViewStyle.Text("Pink dải rộng (dạng IEC/AES):", 12, "#93C5FD"));
        Setting(_broadbandOptions, "Từ (Hz):", _wideLow, "Tần số thấp nhất của pink dải rộng (mặc định 20 Hz).");
        Setting(_broadbandOptions, "Đến (Hz):", _wideHigh, "Tần số cao nhất của pink dải rộng (mặc định 20 kHz, phải dưới Nyquist).");
        Setting(_broadbandOptions, "Crest (dB):", _wideCrest, "Giới hạn hệ số đỉnh của tín hiệu: 12 dB gần nhạc thật; 6 dB nặng hơn cho loa. Không khẳng định tuân thủ IEC/AES.");
        Setting(_broadbandOptions, "Mức RMS (dBFS):", _wideLevel, "Mức phát RMS của pink dải rộng (mặc định −20 dBFS).");
        Setting(_broadbandOptions, "Số lượt:", _wideRepeats, "Số lượt phát cùng dạng sóng để tách méo lặp lại khỏi nhiễu (mặc định 3).");
        var replay = new WrapPanel(); root.Children.Add(replay);
        replay.Children.Add(DiagnosticViewStyle.Text("Nghe lại:", 13)); replay.Children.Add(_listen);
        replay.Children.Add(DiagnosticViewStyle.Text("Nghe qua:", 13)); replay.Children.Add(_render);
        _play = Button(replay, "▶ Nghe", "PlayRubBuzz", Play);
        _stopPlay = Button(replay, "■ Dừng", "StopRubBuzzReplay", () => { StopPlayback(); return Task.CompletedTask; });
        root.Children.Add(_pinkSection); _pinkSection.Children.Add(DiagnosticViewStyle.Text("PINK · rè lặp lại", 17));
        _pinkSection.Children.Add(_pinkMessage);
        root.Children.Add(_chirpSection); _chirpSection.Children.Add(DiagnosticViewStyle.Text("CHIRP · tần số rè", 17));
        _chirpSection.Children.Add(_chirpMessage); _chirpSection.Children.Add(_chirpPlot);
        var tables = new StackPanel(); _tables.Content = tables; root.Children.Add(_tables);
        tables.Children.Add(DiagnosticViewStyle.Text("PINK", 14)); tables.Children.Add(_pinkTable); tables.Children.Add(_pinkPlot);
        tables.Children.Add(DiagnosticViewStyle.Text("CHIRP", 14)); tables.Children.Add(_chirpTable);
        root.Children.Add(_impulsiveText); root.Children.Add(_impulsivePeakPlot); root.Children.Add(_impulsiveCrestPlot);
        root.Children.Add(_broadbandText); root.Children.Add(_broadbandTable); root.Children.Add(_levelTable);
        root.Children.Add(_diagnosisSection);
        _diagnosisSection.Children.Add(DiagnosticViewStyle.Text("CHẨN ĐOÁN", 18));
        _diagnosisSection.Children.Add(_diagnosisBrief);
        // The ten signs and the long explanation stay available, collapsed under the short conclusion.
        var diagnosisDetails = new StackPanel(); _diagnosisDetails.Content = diagnosisDetails;
        diagnosisDetails.Children.Add(_signsTable); diagnosisDetails.Children.Add(_diagnosisText);
        _diagnosisSection.Children.Add(_diagnosisDetails);
        Button(_diagnosisSection, "Sao chép dấu hiệu & chẩn đoán", "CopyRubBuzzDiagnosis", () =>
        { Clipboard.SetText(DiagnosisCopyText); return Task.CompletedTask; });
        _details.Content = _detailText; root.Children.Add(_details); root.Children.Add(_standard);
        var save = new WrapPanel(); root.Children.Add(save); save.Children.Add(_good);
        _save = Button(save, "Lưu line chuẩn rè (loa tốt)", "SaveRubBuzzStandard", SaveStandard);
        ToolTipService.SetShowOnDisabled(_save, true);
        _export = Button(save, "Lưu WAV / mở thư mục", "ExportRubBuzz", Export);
        root.Children.Add(DiagnosticViewStyle.Text("Line chuẩn cho Auto Test C360/Acoustic 3 lưu bằng Tìm line chuẩn (context có fingerprint mixer). "
            + "Chẩn đoán thủ công dùng ngõ đang chọn, không đổi BLE/mixer. Ngưỡng đặt trong Ctrl+Shift+M.", 12));
        root.Children.Add(_advanced);
        DiagnosticViewStyle.Plot(_pinkPlot, "Cao tần 4–12 kHz (dBFS)");
        _pinkPlot.Plot.Axes.Bottom.Label.Text = "Thời gian (s) · lượt chọn tô đậm · đỏ: tiếng động lạ · xanh lá: nền";
        DiagnosticViewStyle.Plot(_chirpPlot, "Đỉnh rè (dB)");
        DiagnosticViewStyle.Plot(_impulsivePeakPlot, "Đỉnh méo xung so với cơ bản (dB)");
        DiagnosticViewStyle.Plot(_impulsiveCrestPlot, "Crest phần dư high-pass (dB)");
        _chirpPlot.Plot.Axes.Bottom.Label.Text = "Tần số chirp (Hz) · lượt chọn tô đậm · xám: chuẩn · đỏ: giới hạn";
        double[] ticks = [20, 50, 100, 200, 500, 1000, 2000, 20000];
        _chirpPlot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}")).ToArray());
        foreach (var plot in new[] { _impulsivePeakPlot, _impulsiveCrestPlot })
        {
            plot.Plot.Axes.Bottom.Label.Text = "Tần số chirp (Hz) · mỗi lượt và trung vị · kiểu IEC 60268-21 / Klippel";
            plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), $"{f:0}")).ToArray());
        }
        _listen.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            StopPlayback(); RenderPlots();
            if (_listen.SelectedItem is RubBuzzListeningItem item)
            { if (item.IsChirp) _chirpSection.BringIntoView(); else { _tables.IsExpanded = true; _pinkPlot.BringIntoView(); } }
            RefreshButtons();
        };
        _sound.SelectionChanged += (_, _) => RefreshButtons();
        _render.DropDownOpened += (_, _) => RefreshRenderDevices();
        _render.SelectionChanged += (_, _) => { StopPlayback(); RefreshButtons(); };
        _timer.Tick += (_, _) => TickPlayback();
        Loaded += (_, _) => RefreshRenderDevices();
        Unloaded += (_, _) => StopPlayback();
        Render(); RenderDiagnostics(); RefreshButtons();
    }
    private Button Button(Panel panel, string label, string name, Func<Task> action)
    {
        var button = new Button { Content = label, Name = name, Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (OperationCanceledException) { _message.Text = "Đã hủy; không dùng dữ liệu dở dang."; }
            catch (Exception ex) { _message.Text = ex.Message; }
        };
        panel.Children.Add(button); return button;
    }
    private static DataGrid Table()
    {
        var table = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 26, MinHeight = 78, FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)), Foreground = Brushes.WhiteSmoke,
            RowBackground = new SolidColorBrush(Color.FromRgb(24, 24, 27)), AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 37)) };
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 43, 49))));
        header.Setters.Add(new Setter(ForegroundProperty, Brushes.WhiteSmoke)); table.ColumnHeaderStyle = header;
        table.AutoGeneratingColumn += (_, e) => { e.Column.MinWidth = 160; e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            e.Column.Header = e.PropertyName switch { "ThongSo" => "Thông số", "LoaDo" => "Loa đang đo", "LineChuan" => "Line chuẩn", "ChoPhep" => "Cho phép",
                "TanSo" => "Tần số", "DauRa" => "Đầu ra", "Nen" => "Nhiễu", "MeoKhongKetHop" => "NL / đầu ra", "KetHop" => "Kết hợp γ²", "DoNhay" => "Độ nhạy",
                "MucPhat" => "Mức phát", "BassTaiMic" => "Bass tại mic", "HFVuotNen" => "HF vượt nền", "HFCuaLoa" => "HF của loa", "HFBass" => "HF / bass", "Xung" => "Xung", "HopLe" => "Hợp lệ", _ => e.PropertyName }; };
        return table;
    }
    private static DataGrid DiagnosisTable()
    {
        var table = Table(); table.Name = "RubBuzzSigns"; table.AutoGenerateColumns = false;
        table.RowHeight = double.NaN;
        var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("Caveat"))); table.RowStyle = rowStyle;
        foreach (var (header, path, width) in new[] { ("Dấu hiệu", "Name", 160), ("Số đo", "Measurement", 260),
            ("So sánh", "Comparison", 180), ("Ý nghĩa", "Meaning", 250), ("Hướng nguyên nhân", "CauseDirections", 200), ("Mức", "Strength", 110) })
        {
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4)));
            table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(path), Width = width, ElementStyle = style });
        }
        return table;
    }
    public string DiagnosisCopyText => _diagnostics?.Diagnosis?.CopyableText ?? "Chưa có dữ liệu chẩn đoán.";
    public void RefreshCapturePathEnhancements()
    {
        var state = CapturePathProvider?.Invoke() ?? _diagnostics?.CapturePathEnhancements;
        _capturePathWarning.Text = state?.Warning ?? ""; _capturePathWarning.ToolTip = state?.Tooltip;
        _capturePathWarning.Visibility = string.IsNullOrEmpty(state?.Warning) ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetDefaultChoice(TestConfig? config, object? selectionKey)
    {
        if (Equals(_selectionKey, selectionKey)) return;
        _selectionKey = selectionKey; _standardChoice = RubBuzzManualCapture.DefaultChoice(config); StandardLabel();
    }
    /// <summary>The standard run follows the model's RubBuzzMode, so its result can use and save the Auto Test line standard.</summary>
    private void StandardLabel() => _measure.Content = "▶ Đo chuẩn · " + _standardChoice switch
        { RubBuzzSoundChoice.Pink => "Pink", RubBuzzSoundChoice.Chirp => "Chirp", _ => "Pink + Chirp" } + " (3 lượt)";
    public RubBuzzSoundChoice StandardChoice => _standardChoice;
    /// <summary>Shows the operator guidance popup for a failed measurement (set by Audio Routing).</summary>
    public Action<Exception>? FailureNotifier { get; set; }
    public void SetBusy(bool busy) { _busy = busy; if (busy) StopPlayback(); RefreshButtons(); }
    public void Cancel() => _cancellation?.Cancel();
    public void Shutdown() { Cancel(); StopPlayback(); _player.Dispose(); }
    public void RefreshRenderDevices()
    {
        if (RenderDevicesProvider == null) return;
        try
        {
            string? testId = RouteProvider?.Invoke()?.PlaybackId;
            string? previous = testId == _lastTestPlaybackId ? (_render.SelectedItem as RubBuzzRenderDevice)?.Id : null;
            _lastTestPlaybackId = testId;
            var devices = RenderDevicesProvider();
            _render.ItemsSource = devices;
            _render.SelectedItem = devices.FirstOrDefault(d => d.Id == previous) ?? RubBuzzAudioPlayer.DefaultDevice(devices, testId);
        }
        catch (Exception ex) { _message.Text = "Không đọc được ngõ nghe: " + ex.Message; }
        RefreshButtons();
    }
    public void RefreshPlaybackRoute(string? testPlaybackId)
    {
        if (testPlaybackId != _lastTestPlaybackId) { StopPlayback(); RefreshRenderDevices(); }
    }
    private void RefreshButtons()
    {
        bool idle = !_busy && _cancellation == null;
        _measure.IsEnabled = _measurePink.IsEnabled = _measureChirp.IsEnabled = _diagnose.IsEnabled = idle; _sound.IsEnabled = idle; _stop.IsEnabled = _cancellation != null;
        _listen.IsEnabled = idle && _items.Count > 0; _render.IsEnabled = idle;
        _play.IsEnabled = idle && _listen.SelectedItem != null && _render.SelectedItem != null;
        _stopPlay.IsEnabled = _player.IsPlaying;
        bool valid = (_pink != null || _chirp != null) && (_pink == null || _pink.IsValid && _pink.Verdict != "INVALID")
            && (_chirp == null || _chirp.IsValid && _chirp.Verdict != "INVALID");
        // Only the result of the standard run can be saved; the diagnostic selector no longer affects that result.
        _save.IsEnabled = idle && valid && !_diagnostic && (_pink is { SavedAsStandard: false } || _chirp is { SavedAsStandard: false });
        _save.ToolTip = _diagnostic ? "Kết quả chẩn đoán không lưu hoặc dùng line chuẩn Auto Test; bấm Đo chuẩn." : "Xác nhận loa tốt trước khi lưu chuẩn của lần đo này.";
        _export.IsEnabled = idle && (_pink != null || _chirp != null) && ResultPersistencePolicy.CanPersist
            && _pink?.PersistenceAllowed != false && _chirp?.PersistenceAllowed != false;
        if (_diagnostics?.Broadband != null) _export.IsEnabled = idle && ResultPersistencePolicy.CanPersist && _diagnostics.Broadband.PersistenceAllowed;
        foreach (var field in new[] { _hpMultiple, _wideLow, _wideHigh, _wideCrest, _wideLevel, _wideRepeats }) field.IsEnabled = idle;
        // Show only the settings the selected sound uses: N for every chirp, the broadband fields for the wide pink test.
        var selected = (RubBuzzSoundChoice)Math.Max(0, _sound.SelectedIndex);
        _impulsiveOptions.Visibility = selected is RubBuzzSoundChoice.Pink or RubBuzzSoundChoice.BroadbandPink ? Visibility.Collapsed : Visibility.Visible;
        _broadbandOptions.Visibility = selected == RubBuzzSoundChoice.BroadbandPink ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task Capture(RubBuzzSoundChoice choice)
    {
        if (_busy || _cancellation != null) return;
        StopPlayback(); using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; RefreshButtons();
        try
        {
            bool standard = RubBuzzManualCapture.AllowsStandard(choice);
            _status.Text = Title + (standard ? " — ĐANG ĐO CHUẨN" : " — ĐANG ĐO CHẨN ĐOÁN"); _status.Foreground = Brushes.Gold;
            _message.Text = (standard ? "Đo chuẩn" : "Chẩn đoán · " + RubBuzzManualCapture.Labels[(int)choice]) + " · nền 3 s → mỗi tín hiệu 3 lượt. Có thể bấm Dừng.";
            var result = await (Runner ?? throw new InvalidOperationException("Mở từ Audio Routing."))(choice, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); _cancellation = null;
            Update(result.Pink, result.Chirp, _directory, diagnostic: !result.AllowsStandard, choice: result.Choice, diagnostics: result);
        }
        // A refused or failed run must not leave the header on "ĐANG ĐO"; the caller then shows the error in _message.
        catch (Exception ex)
        {
            _cancellation = null; Render(); RenderDiagnostics();
            if (ex is not OperationCanceledException) { _message.Text = ex.Message; FailureNotifier?.Invoke(ex); }
            throw;
        }
        finally { _cancellation = null; RefreshButtons(); }
    }
    public void Update(PinkRubBuzzRun? pink, ChirpRubBuzzRun? chirp, string directory,
        PinkRubBuzzLimits? pinkLimits = null, ChirpRubBuzzLimits? chirpLimits = null, bool diagnostic = false, RubBuzzSoundChoice choice = RubBuzzSoundChoice.PinkAndChirp,
        RubBuzzManualResult? diagnostics = null)
    {
        if (_cancellation != null) return;
        diagnostic |= !RubBuzzManualCapture.AllowsStandard(choice);
        bool changed = !ReferenceEquals(_pink, pink) || !ReferenceEquals(_chirp, chirp) || _diagnostic != diagnostic
            || diagnostics != null && !ReferenceEquals(_diagnostics, diagnostics);
        _directory = directory; _diagnostic = diagnostic; _resultChoice = choice;
        _pink = pink; _chirp = chirp; _pinkLimits = pinkLimits ?? pink?.Limits ?? new(); _chirpLimits = chirpLimits ?? chirp?.Limits ?? new();
        try
        {
            if (diagnostic)
            {
                if (pink != null) { pink.StandardMetrics = null; pink.StandardCreatedUtc = null; pink.DeltaRepeatPct = null; pink.DeltaHfDb = null; }
                if (chirp != null) { chirp.StandardMetrics = null; chirp.StandardCreatedUtc = null; chirp.DeltaBumpDb = null; chirp.DeltaBumpHz = null; }
            }
            if (pink != null && !pink.SavedAsStandard) PinkRubBuzzStandard.Compare(pink, diagnostic ? null : PinkRubBuzzStandard.Load(directory, pink.ContextKey), _pinkLimits);
            if (chirp != null && !chirp.SavedAsStandard) ChirpRubBuzzStandard.Compare(chirp, diagnostic ? null : ChirpRubBuzzStandard.Load(directory, chirp.ContextKey), _chirpLimits);
        }
        catch (Exception ex)
        {
            if (pink != null) { pink.Verdict = "REVIEW"; pink.Message = "File line chuẩn rè lỗi: " + ex.Message; }
            if (chirp != null) { chirp.Verdict = "REVIEW"; chirp.Message = "File line chuẩn rè lỗi: " + ex.Message; }
        }
        if (changed)
        {
            _diagnostics = diagnostics ?? new(pink, chirp, choice) { Diagnosis = RubBuzzDiagnosis.Build(pink, chirp) };
            StopPlayback(); _updating = true;
            try
            {
                _items = RubBuzzListeningAudio.Items(pink, chirp);
                if (_diagnostics?.Impulsive is { } a)
                {
                    var audio = RubBuzzListeningAudio.Normalize(a.Residuals);
                    for (int i = 0; i < audio.Samples.Length; i++)
                        _items.Add(new($"Chirp · phần dư high-pass bám tần số lượt {i + 1}", true, RubBuzzAudioKind.TrackingResidual, i, audio.Samples[i], chirp!.SampleRate));
                }
                if (_diagnostics?.Broadband is { } b)
                {
                    _items.Add(new("Pink rộng · Tín hiệu phát", false, RubBuzzAudioKind.Stimulus, -1, b.StimulusSamples, b.PlaybackSampleRate));
                    _items.Add(new("Pink rộng · Nền", false, RubBuzzAudioKind.Background, -1, b.Background, b.SampleRate));
                    for (int i = 0; i < b.Captures.Length; i++) _items.Add(new($"Pink rộng · Mic lượt {i + 1}", false, RubBuzzAudioKind.Microphone, i, b.Captures[i], b.SampleRate));
                    var nl = RubBuzzListeningAudio.Normalize([b.NonlinearResidual]);
                    _items.Add(new("Pink rộng · Phần dư mô hình tuyến tính (NL)", false, RubBuzzAudioKind.BroadbandResidual, -1, nl.Samples[0], b.SampleRate));
                }
                if (_diagnostics?.Levels is not null)
                    _items.AddRange(LevelListeningItems(_diagnostics.Levels));
                _listen.ItemsSource = _items; _listen.SelectedIndex = _items.Count > 0 ? 0 : -1;
            }
            finally { _updating = false; }
            _good.IsChecked = false;
        }
        Render(); RenderDiagnostics(); RefreshButtons();
    }
    private static IEnumerable<RubBuzzListeningItem> LevelListeningItems(RubBuzzLevelDiagnostics levels)
    {
        // Keep the last level in the ordinary Pink slots; add earlier levels with unique kinds/filenames.
        foreach (var run in levels.Runs.OrderBy(r => r.Settings.LevelRmsDbFs).SkipLast(1))
        {
            int tag = (int)(-run.Settings.LevelRmsDbFs) * 10;
            yield return new($"Pink {run.Settings.LevelRmsDbFs:0} dBFS · phát", false, RubBuzzAudioKind.LevelStimulus, tag,
                PinkRubBuzzStimulus.Generate(run.Settings, run.PlaybackSampleRate), run.PlaybackSampleRate);
            for (int i = 0; i < run.Captures.Length; i++)
                yield return new($"Pink {run.Settings.LevelRmsDbFs:0} dBFS · Mic lượt {i + 1}", false, RubBuzzAudioKind.LevelMicrophone, tag + i, run.Captures[i], run.SampleRate);
        }
    }
    private void RenderDiagnostics()
    {
        RefreshCapturePathEnhancements();
        var d = _diagnostics?.Diagnosis;
        _signsTable.ItemsSource = d?.Signs.Select(s => new { Name = s.Key + " · " + s.Name, s.Measurement, s.Comparison, s.Meaning, s.CauseDirections, s.Strength, s.Caveat }).ToArray();
        _diagnosisBrief.Text = d == null ? "Chưa có dữ liệu chẩn đoán." : string.Join("\n", d.Brief);
        _diagnosisText.Text = d == null ? "Chưa có dữ liệu; các dấu hiệu chưa đủ dữ liệu sẽ không dùng để suy luận." :
            string.Join("\n", d.Summary.Select(s => "• " + s)) + "\n\nKhả năng (chưa khẳng định nguyên nhân):\n"
            + string.Join("\n", d.Hypotheses.Select(h => $"• {h.Name} · {h.Confidence} · điểm {h.Score:+0;-0;0} · ủng hộ {string.Join(", ", h.SupportingSignKeys)} / chống {string.Join(", ", h.ContradictingSignKeys)}"))
            + "\n\nKiểm tra tiếp:\n" + string.Join("\n", d.NextChecks.Select(s => "• " + s));
        var a = _diagnostics?.Impulsive;
        _impulsiveText.Text = a == null ? "" : "CHIRP · MÉO XUNG (chẩn đoán) — " + a.Verdict + "\n" + a.Filter
            + $"\nBội HP {a.Settings.HighPassMultiple:0.#}; crest ≥ {a.Settings.CrestThresholdDb:0.#} dB và đỉnh ≥ nền +{a.Settings.PeakAboveBackgroundDb:0.#} dB; lặp ≥ 2 lượt."
            + (a.StandardHohdDeltaDb == null ? "" : "\nĐường cam: chênh HOHD so với line chuẩn (chẩn đoán); xám: từng lượt; xanh: trung vị.");
        _impulsivePeakPlot.Visibility = _impulsiveCrestPlot.Visibility = a == null ? Visibility.Collapsed : Visibility.Visible;
        _impulsivePeakPlot.Plot.Clear(); _impulsiveCrestPlot.Plot.Clear();
        if (a?.Median != null)
        {
            double[] x = a.FrequenciesHz.Select(Math.Log10).ToArray();
            void Curve(WpfPlot plot, double[] y, ScottPlot.Color color, float width)
            { var p = plot.Plot.Add.Scatter(x, y); p.MarkerSize = 0; p.Color = color; p.LineWidth = width; }
            foreach (var r in a.RepeatMetrics)
            { Curve(_impulsivePeakPlot, r.PeakRelativeFundamentalDb, ScottPlot.Colors.SlateGray, 1); Curve(_impulsiveCrestPlot, r.CrestDb, ScottPlot.Colors.SlateGray, 1); }
            Curve(_impulsivePeakPlot, a.Median.PeakRelativeFundamentalDb, ScottPlot.Colors.DeepSkyBlue, 3);
            Curve(_impulsiveCrestPlot, a.Median.CrestDb, ScottPlot.Colors.DeepSkyBlue, 3);
            if (a.StandardHohdDeltaDb is { } delta) Curve(_impulsivePeakPlot, delta, ScottPlot.Colors.Orange, 2);
            _impulsiveCrestPlot.Plot.Add.HorizontalLine(a.Settings.CrestThresholdDb, color: ScottPlot.Colors.Salmon);
        }
        _impulsivePeakPlot.Plot.Axes.AutoScale(); _impulsiveCrestPlot.Plot.Axes.AutoScale();
        _impulsivePeakPlot.Refresh(); _impulsiveCrestPlot.Refresh();
        var b = _diagnostics?.Broadband;
        _broadbandText.Text = b == null ? "" : BroadbandPinkStimulus.Label + "\n" + b.Reason + "\n" + b.Interpretation
            + $"\nCrest đạt {b.Stimulus?.AchievedCrestDb:0.00} dB; RMS {b.Stimulus?.RmsDbFs:0.00} dBFS; đỉnh {b.Stimulus?.PeakDbFs:0.00} dBFS; NL 4–12 kHz: kurtosis {b.HighFrequencyNlKurtosis:0.00}, crest {b.HighFrequencyNlCrestDb:0.00} dB."
            + "\n" + string.Join("; ", b.Alignment.Select(r => $"Lượt {r.Repeat}: clock {r.ClockOffsetPpm:+0.0;-0.0} ppm, r={r.Correlation:0.000}, dư trôi {r.DriftResidualSamples:0.00} mẫu"));
        if (b != null)
        {
            string Band(string title, BroadbandPinkBand? band) => band == null ? "" : $"{title}: NL/đầu ra {band.NonlinearRelativeDb:0.0} dB; γ² {band.Coherence:0.000}; SNR {band.SnrDb:0.0} dB ({band.Sensitivity}).";
            _broadbandText.Text += "\n" + Band("4–12 kHz", b.HighFrequency) + "\n" + Band("0,2–2 kHz", b.LowMid);
        }
        _broadbandTable.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        _broadbandTable.ItemsSource = b?.Bands.Select(r => new { TanSo = $"{r.FrequencyHz:0} Hz", DauRa = $"{r.OutputDbFs:0.0} dBFS", Nen = $"{r.NoiseDbFs:0.0} dBFS",
            NL = $"{r.NonlinearDbFs:0.0} dBFS", MeoKhongKetHop = $"{r.NonlinearRelativeDb:0.0} dB", KetHop = $"{r.Coherence:0.000}", SNR = $"{r.SnrDb:0.0} dB", DoNhay = r.Sensitivity }).ToArray();
        var levels = _diagnostics?.Levels;
        _levelTable.Visibility = levels == null ? Visibility.Collapsed : Visibility.Visible;
        string Metric(double? value, string unit) => value is double n ? $"{n:0.0} {unit}" : "Chưa tính";
        _levelTable.ItemsSource = levels?.Rows.Select(r => new { MucPhat = $"{r.StimulusRmsDbFs:0} dBFS RMS", BassTaiMic = Metric(r.BassDbFs, "dBFS"), HFVuotNen = Metric(r.HfExcessDb, "dB"),
            HFCuaLoa = Metric(r.SpeakerHfDbFs, "dBFS"), HFBass = Metric(r.HfBassDb, "dB"), RC = Metric(r.RepeatCoherencePct, "%"), Xung = Metric(r.EventsPct, "%"), HopLe = r.IsUsable ? "Đủ dữ liệu" : r.ValidityReason }).ToArray();
        if (_resultChoice is RubBuzzSoundChoice.BroadbandPink or RubBuzzSoundChoice.LevelDiagnostic && _diagnostics != null)
        {
            _status.Text = Title + " · CHẨN ĐOÁN — " + (_resultChoice == RubBuzzSoundChoice.BroadbandPink ? "PINK DẢI RỘNG" : "THEO MỨC");
            _status.Foreground = Brushes.LightSkyBlue; _standard.Text = "Chẩn đoán thủ công; không dùng/lưu line chuẩn, không tham gia verdict Auto Test.";
        }
    }
    private static string Verdict(string value) => value switch { "PASS" => "ĐẠT", "FAIL" => "KHÔNG ĐẠT", "INVALID" => "THU KHÔNG HỢP LỆ · ĐO LẠI", _ => "CẦN DUYỆT" };
    private void Render()
    {
        string overall = AcousticRubBuzzConfiguredTest.Combine(new[] { _pink?.Verdict, _chirp?.Verdict }.OfType<string>().ToArray());
        _status.Text = Title + " — " + (_pink == null && _chirp == null ? "CHƯA ĐO" : Verdict(overall))
            + (_pink?.IsValid == true ? $" · Pink {_pink.Metrics.RepeatCoherencePct:0.0} %" : "")
            + (_chirp?.IsValid == true ? $" · Chirp {_chirp.Metrics.PeakBumpDb:0.0} dB @ {_chirp.Metrics.PeakBumpHz:0} Hz" : "");
        _status.Foreground = overall == "FAIL" ? Brushes.Salmon : overall == "PASS" && (_pink != null || _chirp != null) ? Brushes.MediumSpringGreen : Brushes.Gold;
        _message.Text = _pink == null && _chirp == null ? IdleMessage
            : string.Join("\n", new[] { _pink?.Route, _chirp?.Route }.OfType<string>().Distinct()) + (_diagnostic ? "\nChẩn đoán · chỉ so ngưỡng tuyệt đối; không dùng/lưu line chuẩn." : "");
        _pinkMessage.Text = _pink == null ? "Chưa có kết quả Pink." : $"{Verdict(_pink.Verdict)} · {_pink.Message}\n"
            + $"25–150 Hz · {_pink.Settings.LevelRmsDbFs:0.#} dBFS RMS · {_pink.Settings.DurationSeconds:0.#} s × {_pink.Captures.Length} lượt · phát {_pink.PlaybackSampleRate} Hz / thu {_pink.SampleRate} Hz · {_pink.CaptureMode}";
        _chirpMessage.Text = _chirp == null ? "Chưa có kết quả Chirp." : $"{Verdict(_chirp.Verdict)} · {_chirp.Message}\n"
            + $"{_chirp.Settings.F1Hz:0}→{_chirp.Settings.F2Hz:0} Hz · {_chirp.Settings.PeakDbFs:0.#} dBFS đỉnh · {_chirp.Settings.DurationSeconds:0.##} s × {_chirp.Captures.Length} lượt · phát {_chirp.PlaybackSampleRate} Hz / thu {_chirp.SampleRate} Hz · {_chirp.CaptureMode}";
        var p = _pink?.Metrics; var ps = _pink?.StandardMetrics; var c = _chirp?.Metrics; var cs = _chirp?.StandardMetrics;
        _pinkTable.ItemsSource = p?.IsValid != true ? null : new[] {
            new Row("Tỷ lệ rè lặp lại (4–12 kHz)", $"{p.RepeatCoherencePct:0.0} %", ps == null ? "—" : $"{ps.RepeatCoherencePct:0.0} %", ps == null ? $"PASS ≤ {_pinkLimits.AbsolutePassPct:0.#} / FAIL ≥ {_pinkLimits.AbsoluteFailPct:0.#} %" : $"+{_pinkLimits.RepeatMarginPct:0.#} điểm"),
            new Row("Cao tần 4–12 kHz", $"{p.HfDbFs:0.0} dBFS", ps == null ? "—" : $"{ps.HfDbFs:0.0} dBFS", $"+{_pinkLimits.HfMarginDb:0.#} dB"),
            new Row("Bass tại mic", $"{p.DriveDbFs:0.0} dBFS", ps == null ? "—" : $"{ps.DriveDbFs:0.0} dBFS", $"±{_pinkLimits.DriveMatchDb:0.#} dB") };
        _chirpTable.ItemsSource = c?.IsValid != true ? null : new[] {
            new Row("Đỉnh rè (lặp lại ở mọi lượt)", $"{c.PeakBumpDb:0.0} dB @ {c.PeakBumpHz:0} Hz", cs == null ? "—" : $"{cs.PeakBumpDb:0.0} dB @ {cs.PeakBumpHz:0} Hz", cs == null ? $"PASS ≤ {_chirpLimits.AbsolutePassDb:0.#} / FAIL ≥ {_chirpLimits.AbsoluteFailDb:0.#} dB" : $"+{_chirpLimits.BumpMarginDb:0.#} dB"),
            new Row("Vượt chuẩn lớn nhất", _chirp!.DeltaBumpDb is double delta ? $"{delta:+0.0;-0.0} dB @ {_chirp.DeltaBumpHz:0} Hz" : "—", "—", $"≤ +{_chirpLimits.BumpMarginDb:0.#} dB") };
        _detailText.Text = (p == null ? "" : $"PINK · {p.Reason}\nĐộ nhạy {p.SensitivityDb:0.0} dB (≥ {_pinkLimits.MinSensitivityDb:0.#}); tiếng động lạ {100 - p.KeptPct:0.0} % (≤ {_pinkLimits.MaxForeignPct:0.#}); "
            + $"nền {p.BackgroundRmsDbFs:0.0} dBFS / HF {p.BackgroundHfDbFs:0.0} dBFS; lệch đầu/cuối {p.BackgroundDriftDb:0.0} dB; lệch lượt {p.DriveSpreadDb:0.0} dB.\n"
            + "Từng cặp lượt: " + string.Join(" / ", p.PairCoherencePct.Select(v => $"{v:0.0} %")) + "\n")
            + (c == null ? "" : $"CHIRP · {c.Reason}\nMức chirp {c.DriveDbFs:0.0} dB (tương đối); lệch lượt {c.DriveSpreadDb:0.0} dB; nền {c.BackgroundRmsDbFs:0.0} dBFS; lệch đầu/cuối {c.BackgroundDriftDb:0.0} dB; "
                + $"HF vượt nền {c.ExcessAtPeakDb:0.0} dB tại đỉnh / {c.MaxExcessDb:0.0} dB lớn nhất.");
        if (_pink?.Verdict == "INVALID" || _chirp?.Verdict == "INVALID") _details.IsExpanded = true;
        _standard.Text = _diagnostic ? "Chẩn đoán chậm/Sweep: không lưu line chuẩn." : string.Join(" · ",
            new[] { _pink == null ? null : ps == null ? "Pink: chưa có line chuẩn" : $"Pink: chuẩn {_pink.StandardCreatedUtc?.LocalDateTime:g}",
                _chirp == null ? null : cs == null ? "Chirp: chưa có line chuẩn" : $"Chirp: chuẩn {_chirp.StandardCreatedUtc?.LocalDateTime:g}" }.OfType<string>());
        RenderPlots();
    }
    private void RenderPlots()
    {
        _pinkPlot.Plot.Clear(); _chirpPlot.Plot.Clear();
        var selected = _listen.SelectedItem as RubBuzzListeningItem;
        void Line(WpfPlot plot, double[] x, double[] y, ScottPlot.Color color, float width = 1)
        { var line = plot.Plot.Add.Scatter(x, y); line.MarkerSize = 0; line.Color = color; line.LineWidth = width; }
        if (_pink is { } pink && pink.Metrics.Envelope.Length > 0)
        {
            var m = pink.Metrics;
            Line(_pinkPlot, m.Envelope.Select(e => e.Seconds).ToArray(), m.Envelope.Select(e => e.LevelDb).ToArray(), ScottPlot.Colors.DeepSkyBlue, 2);
            for (int i = 0; i < m.RepeatEnvelopes.Length; i++)
            {
                var e = m.RepeatEnvelopes[i]; bool active = selected is { IsChirp: false } && selected.Repeat == i;
                Line(_pinkPlot, e.Select(p => p.Seconds).ToArray(), e.Select(p => p.LevelDb).ToArray(), active ? ScottPlot.Colors.Gold : ScottPlot.Colors.SlateGray, active ? 3 : 1);
            }
            var foreign = m.Envelope.Where(e => e.Foreign).ToArray();
            if (foreign.Length > 0) { var marks = _pinkPlot.Plot.Add.Scatter(foreign.Select(e => e.Seconds).ToArray(), foreign.Select(e => e.LevelDb).ToArray()); marks.LineWidth = 0; marks.MarkerSize = 3; marks.Color = ScottPlot.Colors.Red; }
            _pinkPlot.Plot.Add.HorizontalLine(m.BackgroundHfDbFs, color: ScottPlot.Colors.MediumSeaGreen);
        }
        if (_chirp is { } chirp && chirp.Metrics.FrequenciesHz.Length > 0)
        {
            var m = chirp.Metrics; double[] x = m.FrequenciesHz.Select(Math.Log10).ToArray();
            Line(_chirpPlot, x, m.BumpDb, ScottPlot.Colors.DeepSkyBlue, 2);
            for (int i = 0; i < m.RepeatBumpDb.Length; i++)
            {
                bool active = selected is { IsChirp: true } && selected.Repeat == i;
                Line(_chirpPlot, x, m.RepeatBumpDb[i], active ? ScottPlot.Colors.Gold : ScottPlot.Colors.SlateGray, active ? 3 : 1);
            }
            if (!_diagnostic && chirp.StandardMetrics is { } std && std.BumpDb.Length == x.Length)
            { Line(_chirpPlot, x, std.BumpDb, ScottPlot.Colors.Gray); Line(_chirpPlot, x, std.BumpDb.Select(v => v + _chirpLimits.BumpMarginDb).ToArray(), ScottPlot.Colors.Salmon); }
            else _chirpPlot.Plot.Add.HorizontalLine(_chirpLimits.AbsoluteFailDb, color: ScottPlot.Colors.Salmon);
        }
        _pinkPlot.Plot.Axes.AutoScale(); _chirpPlot.Plot.Axes.AutoScale();
        _pinkCursor = _pinkPlot.Plot.Add.VerticalLine(0, color: ScottPlot.Colors.Gold); _pinkCursor.IsVisible = false;
        _chirpCursor = _chirpPlot.Plot.Add.VerticalLine(0, color: ScottPlot.Colors.Gold); _chirpCursor.IsVisible = false;
        _pinkPlot.Refresh(); _chirpPlot.Refresh();
    }
    private Task Play()
    {
        if (_busy || _listen.SelectedItem is not RubBuzzListeningItem item || _render.SelectedItem is not RubBuzzRenderDevice device) return Task.CompletedTask;
        if (RubBuzzAudioPlayer.NeedsConfirmation(item, device.Id, RouteProvider?.Invoke()?.PlaybackId)
            && MessageBox.Show(Window.GetWindow(this), "Nghe bản mic/phần rè qua chính loa đang test có thể làm loa rè lại và có thể to. Tiếp tục?", "Nghe qua ngõ test", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return Task.CompletedTask;
        _player.Play(item, device.Id); _timer.Start(); RefreshButtons(); return Task.CompletedTask;
    }
    public void StopPlayback()
    {
        _timer.Stop(); _player.Stop();
        if (_pinkCursor != null) _pinkCursor.IsVisible = false;
        if (_chirpCursor != null) _chirpCursor.IsVisible = false;
        _pinkPlot.Refresh(); _chirpPlot.Refresh();
    }
    private void TickPlayback()
    {
        if (!_player.IsPlaying) { if (_player.Error != null) _message.Text = _player.Error; StopPlayback(); RefreshButtons(); return; }
        if (_listen.SelectedItem is not RubBuzzListeningItem item) return;
        double position = _player.PositionSeconds;
        if (item.IsChirp && _chirp != null && _chirpCursor != null)
        {
            double lag = item.Kind is RubBuzzAudioKind.Stimulus or RubBuzzAudioKind.TrackingResidual ? 0 : _chirp.Metrics.CaptureStartSeconds.ElementAtOrDefault(item.Repeat);
            double? f = item.Kind == RubBuzzAudioKind.Background ? null : RubBuzzListeningAudio.ChirpFrequency(_chirp.Settings, position, lag);
            _chirpCursor.IsVisible = f.HasValue; if (f.HasValue) _chirpCursor.X = Math.Log10(f.Value); _chirpPlot.Refresh();
        }
        else if (_pink != null && _pinkCursor != null)
        {
            _pinkCursor.IsVisible = item.Kind is not (RubBuzzAudioKind.Background or RubBuzzAudioKind.LevelMicrophone or RubBuzzAudioKind.LevelStimulus);
            _pinkCursor.X = position + (item.Kind == RubBuzzAudioKind.Stimulus ? _pink.Metrics.CaptureStartSeconds.ElementAtOrDefault(0) : _pink.Metrics.CaptureLagSeconds.ElementAtOrDefault(item.Repeat));
            _pinkPlot.Refresh();
        }
    }
    private Task SaveStandard()
    {
        if (!_save.IsEnabled || _diagnostic) throw new InvalidOperationException("Cần lần đo hợp lệ ở chế độ Pink/Chirp chuẩn.");
        if (_good.IsChecked != true) throw new InvalidOperationException("Xác nhận đây là loa tốt đã nghe kiểm tra trước khi lưu line chuẩn rè.");
        if (string.IsNullOrEmpty(_directory)) throw new InvalidOperationException("Thiếu thư mục dữ liệu trạm.");
        if (_pink != null) PinkRubBuzzStandard.SaveAsStandard(_pink, _pinkLimits, _directory, "Chẩn đoán thủ công · " + _pink.Route);
        if (_chirp != null) ChirpRubBuzzStandard.SaveAsStandard(_chirp, _chirpLimits, _directory, "Chẩn đoán thủ công · " + _chirp.Route);
        _good.IsChecked = false; Render(); RefreshButtons(); return Task.CompletedTask;
    }
    public string ExportAudio(string directory)
    {
        using var persistence = ResultPersistencePolicy.BeginResult(_pink?.PersistenceAllowed != false && _chirp?.PersistenceAllowed != false
            && _diagnostics?.Broadband?.PersistenceAllowed != false && _diagnostics?.Levels?.Runs.All(r => r.PersistenceAllowed) != false);
        string folder = Path.Combine(directory, "captures", "rubbuzz-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        ResultPersistencePolicy.CreateDirectory(folder);
        ResultPersistencePolicy.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new {
            pink = _pink, chirp = _chirp, diagnostic = _diagnostic, choice = _resultChoice,
            diagnosis = _diagnostics?.Diagnosis, impulsive = _diagnostics?.Impulsive, broadband = _diagnostics?.Broadband,
            levelDiagnostics = _diagnostics?.Levels, CapturePathEnhancements = _diagnostics?.CapturePathEnhancements
        }, new JsonSerializerOptions { WriteIndented = true }));
        ResultPersistencePolicy.WriteAllText(Path.Combine(folder, "diagnosis.txt"), DiagnosisCopyText);
        foreach (var item in _items)
        {
            string name = (item.IsChirp ? "chirp-" : "pink-") + item.Kind.ToString().ToLowerInvariant() + (item.Repeat >= 0 ? $"-{item.Repeat + 1}" : "") + ".wav";
            PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, name), item.Samples, item.SampleRate);
        }
        return folder;
    }
    private Task Export()
    {
        if (string.IsNullOrEmpty(_directory)) throw new InvalidOperationException("Thiếu thư mục dữ liệu trạm.");
        Process.Start(new ProcessStartInfo("explorer.exe", '"' + ExportAudio(_directory) + '"') { UseShellExecute = true }); return Task.CompletedTask;
    }
}
