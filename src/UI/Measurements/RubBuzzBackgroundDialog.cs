using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>
/// Noisy Rè background: shows the measured floor and the likely causes, and lets this station raise its allowance
/// (a poorly isolated box) before measuring again. Digital silence never reaches this dialog.
/// </summary>
public sealed class RubBuzzBackgroundDialog : Window
{
    public enum Choice { Cancel, Retry, SaveAndRetry }
    public Choice Result { get; private set; } = Choice.Cancel;
    public BackgroundAllowance Allowance { get; private set; }
    private readonly BackgroundCheck _check;
    private readonly TextBox _rms = Input(), _peak = Input();
    private readonly TextBlock _error = Line("", 13, Brushes.Salmon);

    public RubBuzzBackgroundDialog(BackgroundCheck check)
    {
        _check = check; Allowance = check.Allowance;
        Title = "Nền cao — chưa phát âm thanh"; Width = 680; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.WhiteSmoke;
        var current = check.Allowance; var suggested = current.SuggestFor(check);
        var root = new StackPanel { Margin = new Thickness(18) }; Content = root;
        root.Children.Add(Line("NỀN CAO — CHƯA PHÁT ÂM THANH", 18, Brushes.Gold, bold: true));
        root.Children.Add(Line($"Đo được (bỏ 0,3 s đầu): RMS {check.RmsDbFs:0.0} dBFS · đỉnh {check.PeakDbFs:0.0} dBFS · DC {check.DcDbFs:0.0} dBFS", 14));
        root.Children.Add(Line($"Đang cho phép: RMS ≤ {current.RmsDbFs:0.#} dBFS · đỉnh < {current.PeakDbFs:0.#} dBFS{(current.IsDefault ? " (mặc định)" : " (đã tăng cho trạm này)")}", 14));
        root.Children.Add(Line("Nguyên nhân thường gặp và cách xử lý:", 14, Brushes.LightSkyBlue, bold: true));
        root.Children.Add(Line(Causes(check), 13));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int row, string label, TextBox box, string range)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var l = Line(label, 14); Grid.SetRow(l, row); grid.Children.Add(l);
            Grid.SetRow(box, row); Grid.SetColumn(box, 1); grid.Children.Add(box);
            var r = Line(range, 12, Brushes.Gray); Grid.SetRow(r, row); Grid.SetColumn(r, 2); grid.Children.Add(r);
        }
        Row(0, "Nền cho phép RMS (dBFS):", _rms, $"{BackgroundAllowance.DefaultRmsDbFs:0} (mặc định) … {BackgroundAllowance.MaxRmsDbFs:0}");
        Row(1, "Đỉnh cho phép (dBFS):", _peak, $"{20 * Math.Log10(BackgroundAllowance.DefaultPeakLimit):0} (mặc định) … {20 * Math.Log10(BackgroundAllowance.MaxPeakLimit):0}");
        _rms.Text = suggested.RmsDbFs.ToString("0.#", CultureInfo.InvariantCulture);
        _peak.Text = suggested.PeakDbFs.ToString("0.#", CultureInfo.InvariantCulture);
        root.Children.Add(grid);
        if (!check.IsDcDominated && (check.RmsDbFs > suggested.RmsDbFs || check.PeakDbFs >= suggested.PeakDbFs))
            root.Children.Add(Line($"Nền vượt mức tối đa có thể cho phép (RMS {BackgroundAllowance.MaxRmsDbFs:0} / đỉnh {20 * Math.Log10(BackgroundAllowance.MaxPeakLimit):0} dBFS): cần xử lý thùng cách âm hoặc mic trước, tăng ngưỡng không đủ.", 13, Brushes.Salmon));
        root.Children.Add(Line("Tăng nền cho phép làm giảm độ nhạy phát hiện rè nhỏ. App vẫn yêu cầu bass tại mic cao hơn nền ≥ 20 dB và độ nhạy theo config; "
            + "ngưỡng đã tăng được ghi kèm route của mọi kết quả. Ngưỡng lưu cho máy này, dùng cho Rè thủ công, Auto Test và C360.", 12, Brushes.Gray));
        root.Children.Add(_error);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button("Lưu ngưỡng & đo lại", true, () => { if (TryRead(out var value)) { Allowance = value; Close(Choice.SaveAndRetry); } }).Name = "SaveBackgroundAllowance";
        Button("Đo lại (giữ ngưỡng)", false, () => Close(Choice.Retry)).Name = "RetryBackground";
        Button("Về mặc định", false, () =>
        {
            _rms.Text = BackgroundAllowance.DefaultRmsDbFs.ToString("0.#", CultureInfo.InvariantCulture);
            _peak.Text = BackgroundAllowance.Default.PeakDbFs.ToString("0.#", CultureInfo.InvariantCulture);
        }).Name = "DefaultBackgroundAllowance";
        Button("Hủy", false, () => Close(Choice.Cancel)).Name = "CancelBackground";
        root.Children.Add(buttons);

        Button Button(string label, bool primary, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6), FontSize = 13,
                Background = primary ? new SolidColorBrush(Color.FromRgb(37, 99, 235)) : new SolidColorBrush(Color.FromRgb(43, 43, 49)),
                Foreground = Brushes.White, IsDefault = primary, IsCancel = label == "Hủy" };
            button.Click += (_, _) => action();
            buttons.Children.Add(button); return button;
        }
    }

    private void Close(Choice choice) { Result = choice; DialogResult = choice != Choice.Cancel; }

    public static string Causes(BackgroundCheck check)
    {
        var lines = new List<string>();
        if (check.IsDcDominated)
            lines.Add("• Lệch DC chiếm phần lớn nền → mic/ADC/cáp có lỗi: cắm lại mic, thử cổng USB khác hoặc mic khác. Tăng ngưỡng không khắc phục lỗi này.");
        if (check.RmsFailed && !check.IsDcDominated)
        {
            lines.Add("• Ồn quanh thùng (quạt, điều hòa, người nói, máy khác đang chạy) → chờ yên rồi bấm \"Đo lại\".");
            lines.Add("• Thùng cách âm hở hoặc chưa tốt → đóng kín nắp; nếu nền vẫn cao, tăng nền cho phép bên dưới.");
            lines.Add("• Gain mic quá cao (thu Exclusive bỏ qua thanh mức mic của Windows trên một số mic) → giảm gain/Levels của mic.");
        }
        if (check.PeakFailed && !check.RmsFailed)
            lines.Add("• Có xung/tiếng va chạm trong 3 s thu nền → giữ yên, không chạm thùng/bàn rồi \"Đo lại\"; nếu xung lặp lại do môi trường, tăng đỉnh cho phép.");
        if (lines.Count == 0) lines.Add("• Nền không đạt ngưỡng → chờ yên rồi \"Đo lại\".");
        return string.Join("\n", lines);
    }

    /// <summary>Accepts "-45", "−45", "-45,5" or "45" (dBFS is never positive). A value equal to the shown current/default keeps that exact limit.</summary>
    public static bool TryParseDbFs(string text, out double value)
    {
        string normalized = text.Trim().Replace('−', '-').Replace(',', '.').Replace("dBFS", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
        if (value > 0) value = -value;
        return true;
    }

    private bool TryRead(out BackgroundAllowance value)
    {
        value = Allowance;
        if (!TryParseDbFs(_rms.Text, out double rms) || !TryParseDbFs(_peak.Text, out double peakDb))
        { _error.Text = "Nhập số dBFS, ví dụ -45."; return false; }
        double PeakLimit(double db) => Math.Abs(db - _check.Allowance.PeakDbFs) < .05 ? _check.Allowance.PeakLimit
            : Math.Abs(db - BackgroundAllowance.Default.PeakDbFs) < .05 ? BackgroundAllowance.DefaultPeakLimit : BackgroundAllowance.PeakFromDbFs(db);
        var candidate = new BackgroundAllowance(rms, PeakLimit(peakDb));
        try { candidate.Validate(); }
        catch (ArgumentException ex) { _error.Text = ex.Message; return false; }
        value = candidate; return true;
    }

    private static TextBox Input() => new()
    {
        Margin = new Thickness(8, 3, 8, 3), Padding = new Thickness(4, 2, 4, 2), FontSize = 14,
        Background = new SolidColorBrush(Color.FromRgb(43, 43, 49)), Foreground = Brushes.White, CaretBrush = Brushes.White
    };

    private static TextBlock Line(string text, double size, Brush? brush = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush ?? Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };
}
