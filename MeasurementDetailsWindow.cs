using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record MeasurementValue(string ThongSo, string GiaTri);

public sealed class MeasurementDetailsWindow : Window
{
    private readonly DataGrid _summary = Table();
    private readonly DataGrid _response = Table();
    private readonly DataGrid _harmonics = Table();
    private readonly DataGrid _decays = Table();
    private readonly DataGrid _phase = Table();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ContentControl _impulse = new() { Content = new TextBlock { Text = "Chưa có bản thu sweep. Đồ thị Impulse sẽ xuất hiện sau khi đo.", Margin = new Thickness(20), Foreground = Brushes.White } };
    private StandardAcousticResult? _result;

    public MeasurementDetailsWindow()
    {
        Title = "Impulse / Thông số — Line / Line chuẩn"; Width = 1200; Height = 820; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(10) }; Content = layout;
        DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);
        var tabs = new TabControl(); layout.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "Impulse / ETC / Schroeder", Content = _impulse });
        tabs.Items.Add(new TabItem { Header = "Tổng quan / Thu / Effect", Content = _summary });
        tabs.Items.Add(new TabItem { Header = "Đáp tuyến 1/24", Content = _response });
        tabs.Items.Add(new TabItem { Header = "THD / H2–H9", Content = _harmonics });
        tabs.Items.Add(new TabItem { Header = "EDT / T20 / T30", Content = _decays });
        tabs.Items.Add(new TabItem { Header = "Phase / Group delay", Content = _phase });
    }

    public void Update(string status, IReadOnlyList<MeasurementValue> summary, IReadOnlyDictionary<double, double> response, StandardAcousticResult? result)
    {
        _status.Text = status; _summary.ItemsSource = summary;
        if (!ReferenceEquals(_result, result))
            _impulse.Content = result == null ? new TextBlock { Text = "Đang đo / chưa có bản thu sweep.", Margin = new Thickness(20), Foreground = Brushes.White } : new ImpulseAnalysisView(result);
        _result = result;
        static string Value(double value, string format = "0.000") => double.IsFinite(value) ? value.ToString(format) : "Không khả dụng";
        _response.ItemsSource = response.Where(p => p.Key >= 50 && p.Key <= 18000).OrderBy(p => p.Key)
            .Select(p => new { Hz = Value(p.Key, "0.###"), Level = Value(p.Value, "0.0000") }).ToArray();
        _harmonics.ItemsSource = result?.SweepHarmonics.Select(p => new
        {
            Hz = Value(p.FundamentalFrequencyHz), Fundamental_dBFS = Value(p.FundamentalLevelDb), THD_percent = Value(p.ThdPercent),
            H2_dBc = Value(p.H2Dbc), H3_dBc = Value(p.H3Dbc), H4_dBc = Value(p.H4Dbc), H5_dBc = Value(p.H5Dbc),
            H6_dBc = Value(p.H6Dbc), H7_dBc = Value(p.H7Dbc), H8_dBc = Value(p.H8Dbc), H9_dBc = Value(p.H9Dbc)
        }).ToArray();
        _decays.ItemsSource = result?.DecayEstimates.Select(p => new { ThamSo = p.Name, T60_s = Value(p.Rt60Seconds), R2 = Value(p.RSquared),
            Tu_dB = p.StartDb, Den_dB = p.EndDb, HopLe = p.IsValid, TrangThai = p.Status }).ToArray();
        _phase.ItemsSource = result?.FrequencyResponse.Where(p => p.FrequencyHz >= 50 && p.FrequencyHz <= 18000)
            .Select(p => new { Hz = Value(p.FrequencyHz), Phase_deg = Value(p.PhaseDegrees), GroupDelay_ms = Value(p.GroupDelayMs),
                TransferGain_dB = Value(p.LevelDb) }).ToArray();
    }

    private static DataGrid Table()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
            EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Brushes.White, Foreground = Brushes.Black, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            if (e.PropertyName == "ThongSo") { e.Column.Header = "Thông số"; e.Column.Width = 270; }
            else if (e.PropertyName == "GiaTri") { e.Column.Header = "Giá trị"; e.Column.MinWidth = 450; }
            if (e.PropertyName is "ThongSo" or "GiaTri" && e.Column is DataGridTextColumn text)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 3, 4, 3)));
                text.ElementStyle = style;
                if (e.PropertyName == "GiaTri") e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
            }
        };
        return grid;
    }
}
