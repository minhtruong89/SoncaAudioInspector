using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SoncaAudioInspector;

public sealed class PinkNoiseView : UserControl
{
    private readonly CheckBox _random = new() { Content = "Pink random · REW Preferences (bỏ chọn để dùng periodic)", IsChecked = true, Foreground = System.Windows.Media.Brushes.WhiteSmoke };
    private readonly ComboBox _band = new() { SelectedIndex = 3, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _level = new() { SelectedIndex = 1, Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _play = new() { Content = "Phát pink noise", Padding = new Thickness(16, 8, 16, 8) };
    private readonly TextBlock _status = new() { Text = "Đã dừng. Không tự phát khi mở tab.", TextWrapping = TextWrapping.Wrap };
    public PinkNoiseView(Func<PinkNoiseSettings, Task> run, Action stop)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = panel;
        void Text(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Text("PINK NOISE — Update v1.1");
        Text("Nghe kiểm tra mức và Rub & Buzz. Phát qua ngõ/kênh đang chọn ở Audio Routing; không cần mic. Mục này chạy riêng, không đổi sweep, calibration hay kết quả đo.");
        panel.Children.Add(_random);
        Text("Dải tần (Hz)");
        foreach (string label in new[] { "20–20000 Hz · Toàn dải", "40–200 Hz · Bass / nghe rè", "500–2000 Hz · Kiểm tra mức loa", "30–80 Hz · Subwoofer / REW Preferences" }) _band.Items.Add(label);
        panel.Children.Add(_band);
        Text("Mức RMS (0 dBFS = biên độ số 1; full-scale sine = -3.01 dBFS)");
        foreach (string label in new[] { "-30 dBFS RMS", "-24 dBFS RMS", "-18 dBFS RMS" }) _level.Items.Add(label);
        panel.Children.Add(_level);
        Text("Pink random: bộ lọc Kellet + Butterworth bậc 2, cùng nguyên lý REW Sub Cal 30–80 Hz; không lặp chu kỳ 128K. RMS live chuẩn hóa theo đoạn tham chiếu 30 s; mức ngắn hạn dao động tự nhiên. Periodic: nguồn 44,1 kHz, chu kỳ 128K mẫu (thiết bị Shared có thể resample), công suất giảm 3 dB/octave. Để đối chiếu REW Preferences → Soundcard, bật ‘Use pink periodic noise for level checks throughout REW’ và khớp dải tần/mức RMS. Đây không phải bản sao waveform REW.");
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 16) };
        buttons.Children.Add(_play);
        var halt = new Button { Content = "Dừng", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 8, 0) };
        halt.Click += (_, _) => stop(); buttons.Children.Add(halt);
        var save = new Button { Content = "Xuất WAV nghe thử (≈36 s)", Padding = new Thickness(16, 8, 16, 8) };
        save.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "WAV PCM24|*.wav", FileName = "pink-noise-v1.1.wav" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { PinkNoiseSignal.SaveWave(dialog.FileName, Settings()); _status.Text = "Đã lưu WAV mono 44,1 kHz / PCM24: " + dialog.FileName; }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        buttons.Children.Add(save); panel.Children.Add(buttons); panel.Children.Add(_status);
        Text("Bắt đầu ở âm lượng loa nhỏ rồi tăng từ từ. Pink noise hỗ trợ nghe tìm rè; kết luận lỗi cơ khí cần đo/đối chứng loa thực.");
        _play.Click += async (_, _) =>
        {
            try { _status.Text = "Đang phát pink noise…"; await run(Settings()); _status.Text = "Đã dừng."; }
            catch (OperationCanceledException) { _status.Text = "Đã dừng."; }
            catch (Exception ex) { _status.Text = "Không phát được: " + ex.Message; }
        };
    }
    public PinkNoiseSettings Settings()
    {
        var (low, high) = _band.SelectedIndex switch { 1 => (40d, 200d), 2 => (500d, 2000d), 3 => (30d, 80d), _ => (20d, 20000d) };
        return new(low, high, _level.SelectedIndex switch { 0 => -30, 2 => -18, _ => -24 }, _random.IsChecked == true);
    }
    public void SetBusy(bool busy) { _play.IsEnabled = _band.IsEnabled = _level.IsEnabled = _random.IsEnabled = !busy; }
}
