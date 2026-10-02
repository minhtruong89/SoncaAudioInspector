using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace RewNoiseBridge;

public partial class MainWindow : Window
{
    private readonly RewApiClient _rew = new();
    private readonly DispatcherTimer _timer;
    private readonly Queue<(DateTime Timestamp, double Spl)> _recent = new();
    private bool _polling;
    private bool _connected;
    private bool _calibrated;
    private bool _explicitInput;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += PollTimer_Tick;
        Loaded += async (_, _) => await ConnectAsync();
        Closed += (_, _) => _rew.Dispose();
    }

    private async Task ConnectAsync()
    {
        SetBusy(true);
        try
        {
            RewConnectionInfo info = await _rew.GetConnectionInfoAsync();
            _connected = true;
            _calibrated = info.DbFsAt94DbSpl is > -200 and < 20;
            _explicitInput = !info.InputDevice.Contains("Default", StringComparison.OrdinalIgnoreCase)
                && !info.Input.Contains("Default", StringComparison.OrdinalIgnoreCase);
            TxtApiStatus.Text = $"REW API {info.ApiVersion}: ĐÃ KẾT NỐI";
            TxtApiStatus.Foreground = Brushes.LightGreen;
            TxtAudioStatus.Text = $"Audio: {(info.AudioReady ? "READY" : "CHƯA SẴN SÀNG")} · {info.Driver} · {info.SampleRate:0} Hz · {info.InputDevice} / {info.Input}";
            TxtCalibration.Text = _calibrated
                ? $"SPL calibration: 94 dB SPL = {info.DbFsAt94DbSpl:F4} dBFS · {info.CalibrationSelection}{(_explicitInput ? "" : " · CẢNH BÁO: đang dùng Default Device")}" 
                : $"CHƯA XÁC NHẬN SPL CALIBRATION · {info.CalibrationSelection}";
            TxtCalibration.Foreground = _calibrated && _explicitInput ? Brushes.LightGreen : Brushes.Gold;
            TxtDiagnosis.Text = _calibrated && _explicitInput
                ? "API và SPL calibration đã sẵn sàng. Chọn weighting/filter rồi bấm MỞ + BẮT ĐẦU."
                : "Chưa đủ bằng chứng để tin dB SPL tuyệt đối: phải chọn đúng thiết bị/input cụ thể và có SPL calibration tương ứng.";
            _timer.Start();
        }
        catch (Exception ex)
        {
            _connected = false;
            _timer.Stop();
            TxtApiStatus.Text = "REW API: KHÔNG KẾT NỐI";
            TxtApiStatus.Foreground = Brushes.Salmon;
            TxtAudioStatus.Text = "Hãy chạy REW 5.40+ với API bật tại 127.0.0.1:4735.";
            TxtCalibration.Text = "Trong REW: Preferences → API → Start API when REW starts.";
            TxtCalibration.Foreground = Brushes.Gold;
            TxtDiagnosis.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_polling || !_connected) return;
        _polling = true;
        try
        {
            SplLevels levels = await _rew.GetLevelsAsync();
            if (!double.IsFinite(levels.Spl) || levels.Spl <= -170)
            {
                TxtSpl.Text = "--.-";
                TxtLeq.Text = "--.- dB";
                TxtPeak.Text = "--.- dB";
                TxtDiagnosis.Text = "SPL meter REW chưa chạy. Bấm MỞ + BẮT ĐẦU.";
                return;
            }

            TxtSpl.Text = levels.Spl.ToString("F1");
            TxtSplUnit.Text = $"dB SPL · {levels.SplWeighting}{(levels.Filter.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? "S" : "F")}";
            TxtLeq.Text = $"{levels.Leq:F1} dB";
            TxtPeak.Text = $"{levels.LzPeak:F1} dB";
            TxtElapsed.Text = $"REW elapsed: {levels.ElapsedTime:F1} s";

            DateTime now = DateTime.UtcNow;
            _recent.Enqueue((now, levels.Spl));
            while (_recent.Count > 0 && now - _recent.Peek().Timestamp > TimeSpan.FromSeconds(10))
                _recent.Dequeue();

            double[] values = _recent.Select(item => item.Spl).ToArray();
            double min = values.Min();
            double max = values.Max();
            double mean = values.Average();
            double sigma = Math.Sqrt(values.Select(value => (value - mean) * (value - mean)).Average());
            double range = max - min;
            TxtRange.Text = $"Range: {range:F1} dB ({min:F1}–{max:F1})";
            TxtStdDev.Text = $"σ: {sigma:F2} dB · n={values.Length}";

            if (!_calibrated || !_explicitInput)
            {
                TxtDiagnosis.Text = "Số đang đọc chưa có đủ bằng chứng calibration cho đúng thiết bị/input; chỉ nên xem tương đối.";
                TxtDiagnosis.Foreground = Brushes.Gold;
            }
            else if (range > 3.0 && values.Length >= 8)
            {
                TxtDiagnosis.Text = "Dao động > 3 dB/10 s. Kiểm tra tiếng phòng, gain/preamp, đúng input/channel, AGC/noise suppression và giữ nguyên vị trí mic. API không loại bỏ biến động phần cứng.";
                TxtDiagnosis.Foreground = Brushes.Salmon;
            }
            else
            {
                TxtDiagnosis.Text = "Đọc từ thuật toán SPL meter của REW. Dùng Leq để đánh giá nền ổn định; không nhầm với Headroom dBFS của app chính.";
                TxtDiagnosis.Foreground = Brushes.LightGreen;
            }
        }
        catch (Exception ex)
        {
            TxtDiagnosis.Text = "Mất dữ liệu REW API: " + ex.Message;
            TxtDiagnosis.Foreground = Brushes.Salmon;
        }
        finally
        {
            _polling = false;
        }
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            string weighting = SelectedText(ComboWeighting, "Z");
            string filter = SelectedText(ComboFilter, "Slow");
            await _rew.ConfigureMeterAsync(weighting, filter);
            await _rew.SendCommandAsync("Open", tolerateAlreadyOpen: true);
            await Task.Delay(500);
            await _rew.SendCommandAsync("Start");
            ResetStatistics();
            _timer.Start();
            TxtDiagnosis.Text = $"Đang đọc REW SPL meter: {weighting} weighting, {filter}.";
        }
        catch (Exception ex)
        {
            TxtDiagnosis.Text = "Không khởi động được SPL meter: " + ex.Message;
            TxtDiagnosis.Foreground = Brushes.Salmon;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _rew.SendCommandAsync("Stop");
            TxtDiagnosis.Text = "Đã dừng SPL meter trong REW.";
        }
        catch (Exception ex)
        {
            TxtDiagnosis.Text = "Không dừng được SPL meter: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BtnReconnect_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        ResetStatistics();
        try { await _rew.SendCommandAsync("Reset"); } catch { }
    }

    private void ResetStatistics()
    {
        _recent.Clear();
        TxtRange.Text = "Range: -- dB";
        TxtStdDev.Text = "σ: -- dB";
    }

    private void SetBusy(bool busy)
    {
        BtnReconnect.IsEnabled = !busy;
        BtnStart.IsEnabled = !busy;
        BtnStop.IsEnabled = !busy;
        BtnReset.IsEnabled = !busy;
    }

    private static string SelectedText(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
}
