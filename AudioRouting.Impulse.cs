using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private MeasurementDetailsWindow? _measurementDetailsWindow;
    private IReadOnlyList<MeasurementValue>? _lineAcquisitionValues;
    private Window? _measurementShortcutHost;

    private void HandleMeasurementShortcut(object sender, KeyEventArgs e)
    {
        if (TryHandleMeasurementShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, () => ShowMeasurementDetails())) e.Handled = true;
    }

    private static bool TryHandleMeasurementShortcut(Key key, ModifierKeys modifiers, bool repeat, Action show)
    {
        if (key != Key.I || modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || repeat) return false;
        show();
        return true;
    }

    private void ShowMeasurementDetails(bool pending = false)
    {
        if (_measurementDetailsWindow == null)
        {
            _measurementDetailsWindow = new MeasurementDetailsWindow { Owner = Window.GetWindow(this) };
            _measurementDetailsWindow.Closed += (_, _) => _measurementDetailsWindow = null;
            _measurementDetailsWindow.Show();
        }
        _measurementDetailsWindow.Activate();
        UpdateMeasurementDetails(pending);
    }

    private void UpdateMeasurementDetails(bool pending = false)
    {
        var sweep = pending ? null : _reviewedAutoTestCase != null ? _reviewedAutoTestCase.ImpulseResult : _testRunner.LastSweepResult;
        if (TxtMeasurementSummary != null)
            TxtMeasurementSummary.Text = pending ? "Đang đo…" : sweep == null ? "Chưa có sweep"
                : $"RMS {sweep.SignalLevelDbFs:0.0} dBFS · SNR {sweep.EstimatedSnrDb:0.0} dB · delay {sweep.DirectArrivalMs:0.00} ms · "
                    + (sweep.ImpulseEffects is { IsValid: true } e ? $"echo {(e.EchoDetected ? $"{e.EchoDelayMs:0} ms" : "chưa thấy")} · reverb {(e.ReverbDetected ? "có đuôi" : "chưa thấy")}" : "effect chưa đủ dữ liệu");
        if (_measurementDetailsWindow == null) return;
        var measured = pending ? new Dictionary<double, double>() : _reviewedAutoTestCase != null
            ? _reviewedAutoTestCase.ResponseFrequencies.Zip(_reviewedAutoTestCase.ResponseLevels).ToDictionary(p => p.First, p => p.Second)
            : _freqs.Zip(_dbValues).ToDictionary(p => p.First, p => p.Second);
        var rows = BuildMeasurementValues(sweep, pending);
        string status = pending ? "Đang thu/phân tích — thông số chưa đo sẽ hiện Chưa đo."
            : $"{(_isFindingStandardReference ? "Line chuẩn" : "Line")} · {_referenceRouteKey} · {sweep?.Validity ?? "chưa có sweep"} · đáp tuyến 1/24 octave, 50 Hz–18 kHz";
        _measurementDetailsWindow.Update(status, rows, measured, sweep);
    }

    private IReadOnlyList<MeasurementValue> BuildAcquisitionValues()
    {
        var rows = new List<MeasurementValue>();
        void Add(string name, string value) => rows.Add(new(name, value));
        Add("Route / bài đo", _referenceRouteKey);
        Add("Ngõ phát", SelectedPlaybackDevice?.FriendlyName ?? "Chưa chọn");
        Add("Ngõ thu", (ComboRecording.SelectedItem as DeviceItem)?.Device.FriendlyName ?? "Chưa chọn");
        Add("Kênh phát / thu", $"{(_audioEngine.PlaybackChannel.HasValue ? (_audioEngine.PlaybackChannel.Value + 1).ToString() : "Cả hai")} / {(_audioEngine.RecordingChannel.HasValue ? (_audioEngine.RecordingChannel.Value + 1).ToString() : "Cả hai")}");
        Add("Sample rate phát", $"{_audioEngine.PlaybackSampleRate} Hz");
        Add("Chuẩn sample rate đối chiếu", "44100 Hz cho các thiết bị");
        string rateWarning = BuildSampleRateWarning(_audioEngine.PlaybackSampleRate,
            ReadEndpointSampleRate(SelectedPlaybackDevice), ReadEndpointSampleRate(SelectedRecordingDevice), null);
        if (rateWarning.Length > 0) Add("Cảnh báo sample rate trước đo", rateWarning);
        Add("Length / thời gian sweep", $"{Math.Round(_testRunner.LogSweepDurationSeconds * _audioEngine.PlaybackSampleRate):0} mẫu / {_testRunner.LogSweepDurationSeconds:0.000000} s");
        Add("Thời gian thu đuôi yêu cầu", $"{_testRunner.ImpulseEffectsSettings.CaptureTailSeconds:0.000} s");
        Add("Mức phát / Recording Gain số", $"{PlaybackLevelDbfs:0.00} dBFS peak / {_audioEngine.RecordingGain:0.00}x");
        Add("Chế độ / đơn vị / so sánh", $"{(_audioEngine.UseExclusivePlayback ? "Exclusive" : "WASAPI Shared")} / {(_testRunner.NormalizeFrequencyResponseToOneKilohertz ? "dBr chuẩn hóa 1k" : "dBFS không chuẩn hóa")} / 1/24, 50–18000 Hz");
        Add("Calibration đáp tuyến / Distortion", $"{_testRunner.FeqMicrophoneCalibration?.Name ?? "Không"} / {(_testRunner.ApplyDistortionCalibration ? "Áp dụng" : "Không áp dụng")}");
        Add("Tham chiếu THD", _testRunner.UseHarmonicFrequencyAsDistortionReference ? "Tại tần số hài" : "Tại fundamental");
        return rows;
    }

    private IReadOnlyList<MeasurementValue> BuildMeasurementValues(StandardAcousticResult? sweep, bool pending)
    {
        if (!pending && _reviewedAutoTestCase?.MeasurementValues is { Count: > 0 } saved) return saved;
        var rows = new List<MeasurementValue>(_lineAcquisitionValues ?? BuildAcquisitionValues());
        void Add(string name, string value) => rows.Add(new(name, value));
        static string N(double? value, string unit = "", string format = "0.000") => value is double v && double.IsFinite(v) ? v.ToString(format) + unit : "Chưa đo / không khả dụng";
        Add("Sample rate thu thực", sweep != null ? sweep.SampleRate + " Hz" : "Chưa mở thu");
        if (!pending && _testRunner.LastPreflightWarning.Length > 0) Add("Cảnh báo trước đo", _testRunner.LastPreflightWarning);
        if (sweep != null && sweep.SampleRate != StandardSampleRate)
            Add("Cảnh báo sample rate bản thu", $"Lượt thu {sweep.SampleRate} Hz khác chuẩn 44100 Hz. Chưa đồng bộ chuẩn 44.1 kHz giữa các thiết bị.");
        Add("RMS / Noise floor", N(sweep?.SignalLevelDbFs, " dBFS") + " / " + N(sweep?.NoiseFloorDbFs, " dBFS"));
        Add("RMS tín hiệu sweep phát", N(sweep?.ExcitationRmsDbFs, " dBFS"));
        Add("SNR", N(sweep?.EstimatedSnrDb, " dB"));
        Add("Peak thu / peak trước gain", N(sweep?.PeakSample) + " / " + N(sweep?.EstimatedPreGainPeak));
        Add("Clipping", sweep == null ? "Chưa đo" : sweep.IsClipped ? "CÓ — không dùng kết quả" : "Không");
        double? oneKhz = pending ? null : _testRunner.LastOneKilohertzLevelDbFs;
        Add("Mức 1 kHz / chuẩn / độ lệch", N(oneKhz, " dBFS") + " / " + N(_standardOneKilohertzLevelDbFs, " dBFS") + " / " + N(oneKhz - _standardOneKilohertzLevelDbFs, " dB"));
        Add("Max lệch đáp tuyến", N(pending ? null : _testRunner.LastFrequencyMaxDeviationDb, " dB"));
        Add("Direct arrival / cực tính", N(sweep?.DirectArrivalMs, " ms") + " / " + (sweep?.Polarity ?? "Chưa đo"));
        Add("Direct sample / hợp lệ", N(sweep?.DirectArrivalSample, " mẫu", "0") + " / " + (sweep?.HasPlausibleDirectArrival.ToString() ?? "Chưa đo"));
        Add("Tương quan sweep / độ nổi IR", N(sweep?.SweepCorrelation) + " / " + N(sweep?.ImpulsePeakToBackgroundDb, " dB"));
        Add("Clock / sai số fit / đã bù", N(sweep?.EstimatedClockDriftPpm, " ppm") + " / " + N(sweep?.ClockFitRmsSamples, " mẫu") + " / " + (sweep?.ClockDriftCorrectionApplied.ToString() ?? "Chưa đo"));
        Add("IR trái / phải / FDW", N(sweep?.ImpulseWindowLeftMs, " ms") + " / " + N(sweep?.ImpulseWindowRightMs, " ms") + " / " + N(sweep?.FrequencyDependentWindowCycles, " chu kỳ"));
        Add("Tần số thấp nhất tin cậy theo IR", N(sweep?.GatedMinimumFrequencyHz, " Hz"));
        Add("C50 / C80 / D50", N(sweep?.ClarityC50Db, " dB") + " / " + N(sweep?.ClarityC80Db, " dB") + " / " + N(sweep?.DefinitionD50Percent, "%"));
        foreach (var decay in sweep?.DecayEstimates ?? Array.Empty<DecayEstimate>()) Add(decay.Name, N(decay.Rt60Seconds, " s") + $" · R² {decay.RSquared:0.000} · {decay.Status}");
        foreach (var band in sweep?.OctaveBandRt60 ?? Array.Empty<BandRt60Result>()) Add($"Decay band {band.FrequencyHz:0} Hz", $"EDT {N(band.EdtSeconds, " s")} / T20 {N(band.T20Seconds, " s")} / T30 {N(band.T30Seconds, " s")} · hợp lệ {band.IsValid}");
        if (sweep?.ImpulseEffects is { } effects)
        {
            Add("Chẩn đoán echo/reverb", effects.Describe());
            Add("IR noise / đuôi thu thực / bị cắt", N(effects.ImpulseNoiseDb, " dBr") + " / " + N(effects.CapturedTailSeconds, " s") + " / " + effects.TailTruncated);
            foreach (var tap in effects.EchoTaps) Add("Echo tap", N(tap.DelayMs, " ms") + " / " + N(tap.LevelDb, " dBr theo direct"));
            if (_standardImpulseEffects != null) foreach (string note in _standardImpulseEffects.Compare(effects, _testRunner.ImpulseEffectsSettings)) Add("So effect với line chuẩn", note);
        }
        foreach (var point in pending ? new Dictionary<double, ToneQualityMetrics>() : _testRunner.LastToneQualities)
            Add($"Distortion {point.Key:0} Hz", $"f0 {N(point.Value.FundamentalFrequencyHz, " Hz")} / THD {N(point.Value.ThdPercent, "%")} / THD+N {N(point.Value.ThdNPercent, "%")} / SINAD {N(point.Value.SinadDb, " dB")} / SNR {N(point.Value.SnrDb, " dB")} / DC {N(point.Value.DcOffset)} · hợp lệ {point.Value.IsValid}");
        Add("THD+N / SINAD / IMD từ log-sweep", "Không suy ra từ sweep; chỉ hiển thị khi có phép đo tương ứng.");
        if (sweep?.RewDistortion is { } stats)
            Add("THD sweep trung bình / cực đại", N(stats.AverageThdPercent, "%") + " / " + N(stats.MaxThdPercent, "%") + " tại " + N(stats.MaxThdFrequencyHz, " Hz") + " · hài trội " + stats.DominantHarmonic);
        if (!string.IsNullOrWhiteSpace(sweep?.Warning)) Add("Cảnh báo bản thu", sweep.Warning);
        foreach (var diagnostic in sweep?.Diagnostics ?? Array.Empty<MeasurementDiagnostic>()) Add(diagnostic.Severity + " · " + diagnostic.Code, diagnostic.Message + " " + diagnostic.Action);
        return rows;
    }

    private IReadOnlyDictionary<double, double>? GetDisplayedStandardCurve()
    {
        if (!_showUnsmoothedResponse) return _reviewedAutoTestCase?.StandardCurve ?? _standardCurve;
        var raw = _reviewedAutoTestCase?.UnsmoothedStandardCurve ?? _rawStandardCurve;
        return raw.Count > 0 ? raw : null;
    }

    private void SetupResponseContextMenu()
    {
        var menu = new ContextMenu();
        var smoothed = new MenuItem { Header = "1/24 octave — so sánh 50 Hz–18 kHz", IsCheckable = true, IsChecked = true };
        var raw = new MenuItem { Header = "No smoothing — dữ liệu trước làm mượt", IsCheckable = true };
        smoothed.Click += (_, _) => SetResponseSmoothing(false);
        raw.Click += (_, _) => SetResponseSmoothing(true);
        menu.Items.Add(smoothed); menu.Items.Add(raw);
        menu.Opened += (_, _) =>
        {
            smoothed.IsChecked = !_showUnsmoothedResponse; raw.IsChecked = _showUnsmoothedResponse;
            raw.IsEnabled = (_reviewedAutoTestCase?.UnsmoothedResponse ?? _rawResponse).Count > 0
                || (_reviewedAutoTestCase?.UnsmoothedStandardCurve ?? _rawStandardCurve).Count > 0;
        };
        PlotFreqResponse.ContextMenu = menu;
        PlotFreqResponse.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        PlotFreqResponse.PreviewMouseRightButtonUp += (_, e) =>
        {
            menu.PlacementTarget = PlotFreqResponse; menu.IsOpen = true; e.Handled = true;
        };
    }

    private void SetResponseSmoothing(bool unsmoothed)
    {
        _showUnsmoothedResponse = unsmoothed;
        UpdateFreqResponseChart();
    }

    private void ShowImpulseAnalysis()
    {
        var result = _reviewedAutoTestCase != null ? _reviewedAutoTestCase.ImpulseResult : _testRunner.LastSweepResult;
        if (result == null) return;
        new ImpulseAnalysisWindow(result) { Owner = Window.GetWindow(this) }.Show();
    }

    private void LoadRawStandardCurve(string csvPath, string basis, bool normalize)
    {
        _rawStandardCurve = new Dictionary<double, double>();
        _standardImpulseEffects = null;
        string path = Path.ChangeExtension(csvPath, ".json");
        if (!File.Exists(path)) return;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (!root.TryGetProperty("analysisId", out var analysis) || analysis.GetString() != ResponseSmoothing.AnalysisId
                || root.GetProperty("levelBasis").GetString() != basis
                || root.GetProperty("csvSha256").GetString() != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csvPath)))
                || !root.TryGetProperty("rawPoints", out var points)) return;
            var curve = new Dictionary<double, double>();
            foreach (var point in points.EnumerateArray())
            {
                double f = point.GetProperty("frequencyHz").GetDouble(), value = point.GetProperty("levelDb").GetDouble();
                if (f > 0 && double.IsFinite(f) && double.IsFinite(value)) curve[f] = value;
            }
            if (normalize && _standardOneKilohertzLevelDbFs is double reference)
                curve = curve.ToDictionary(p => p.Key, p => p.Value - reference);
            _rawStandardCurve = curve;
            if (root.TryGetProperty("impulseEffectsReference", out var effects) && effects.ValueKind == JsonValueKind.Object)
                _standardImpulseEffects = effects.Deserialize<ImpulseEffectReference>();
        }
        catch (Exception ex) { AppendLog("Line gốc", "Không đọc được dữ liệu trước làm mượt: " + ex.Message); }
    }
}

public sealed class ImpulseAnalysisWindow : Window
{
    public ImpulseAnalysisWindow(StandardAcousticResult result)
    {
        Title = "Impulse / Echo / Reverb — cùng lượt log-sweep";
        Width = 1100; Height = 720; MinWidth = 750; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(12) }; Content = layout;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 12),
            Text = (result.ImpulseEffects?.Describe() ?? "Chưa có dữ liệu chẩn đoán hiệu ứng.")
                + "\nIR/ETC dùng toàn đuôi thu, trước cửa sổ FDW. dBr theo đỉnh IR; T60 đuôi là hồi quy đuôi hiệu ứng, không phải chứng nhận RT60 phòng." };
        DockPanel.SetDock(text, Dock.Top); layout.Children.Add(text);
        var tabs = new TabControl(); layout.Children.Add(tabs);
        var impulse = new WpfPlot();
        var irLine = impulse.Plot.Add.Scatter(result.ImpulseResponse.Select(p => p.TimeSeconds * 1000).ToArray(),
            result.ImpulseResponse.Select(p => 20 * Math.Log10(Math.Max(1e-9, Math.Abs(p.Value)))).ToArray());
        irLine.LegendText = "Impulse"; irLine.MarkerSize = 0;
        var etcLine = impulse.Plot.Add.Scatter(result.EnergyTimeCurve.Select(p => p.TimeSeconds * 1000).ToArray(),
            result.EnergyTimeCurve.Select(p => p.Value).ToArray());
        etcLine.LegendText = "ETC"; etcLine.MarkerSize = 0;
        if (result.ImpulseEffects != null)
            foreach (var tap in result.ImpulseEffects.EchoTaps)
                impulse.Plot.Add.VerticalLine(tap.DelayMs).LegendText = $"Echo {tap.DelayMs:0} ms";
        impulse.Plot.Axes.Bottom.Label.Text = "Thời gian từ direct (ms)";
        impulse.Plot.Axes.Left.Label.Text = "dBr theo đỉnh IR";
        impulse.Plot.Axes.SetLimits(-10, Math.Max(100, result.ImpulseResponse.LastOrDefault()?.TimeSeconds * 1000 ?? 1000), -110, 5);
        impulse.Plot.ShowLegend();
        tabs.Items.Add(new TabItem { Header = "Impulse / ETC", Content = impulse });
        var decay = new WpfPlot();
        decay.Plot.Add.Scatter(result.EnergyDecayCurve.Select(p => p.TimeSeconds * 1000).ToArray(), result.EnergyDecayCurve.Select(p => p.LevelDb).ToArray());
        decay.Plot.Axes.Bottom.Label.Text = "Thời gian từ direct (ms)"; decay.Plot.Axes.Left.Label.Text = "Schroeder (dB)";
        decay.Plot.Axes.SetLimits(0, Math.Max(100, result.ImpulseResponse.LastOrDefault()?.TimeSeconds * 1000 ?? 1000), -80, 2);
        tabs.Items.Add(new TabItem { Header = "Schroeder decay", Content = decay });
    }
}
