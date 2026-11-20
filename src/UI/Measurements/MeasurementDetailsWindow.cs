using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record MeasurementValue(string ThongSo, string GiaTri);

public sealed class MeasurementDetailsWindow : Window
{
    private readonly DataGrid _summary = Table();
    private readonly DataGrid _decays = Table();
    private readonly DataGrid _phase = Table();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly ContentControl _impulse = new() { Content = new TextBlock { Text = "Chưa có bản thu sweep. Đồ thị Impulse sẽ xuất hiện sau khi đo.", Margin = new Thickness(20), Foreground = Brushes.White } };
    private StandardAcousticResult? _result;
    private readonly HighOrderHarmonicView _highOrders = new();
    private readonly MechanicalDiagnosticView _looseParticle;
    private readonly MechanicalDiagnosticView _airLeak;
    private readonly PolarityDiagnosticView _polarity = new();
    private readonly SignalDiagnosticView _imd, _compression;
    private System.Threading.CancellationTokenSource? _diagnosticCancellation;
    public Func<MechanicalTestRequest, System.Threading.CancellationToken, Task<MechanicalTestResult>>? DiagnosticRunner { get; set; }
    public Func<SignalDiagnosticRequest, System.Threading.CancellationToken, Task<SignalDiagnosticResult>>? SignalDiagnosticRunner { get; set; }
    public Func<ImpedanceView>? ImpedanceFactory { get; set; }
    public ImpedanceView? Impedance { get; private set; }
    private readonly TabControl _tabs = new();
    private readonly TabItem _impedanceTab = new() { Header = "Impedance · Ω" };
    private readonly PinkNoiseView _pinkNoise;
    public RubBuzzView RubBuzz { get; } = new();
    public Func<RubBuzzSoundChoice, System.Threading.CancellationToken, Task<RubBuzzManualResult>>? RubBuzzRunner { get; set; }
    public Func<MeasurementRouteInfo?>? RouteProvider { get; set; }
    private readonly TextBlock _route = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.LightSkyBlue };
    private readonly System.Windows.Threading.DispatcherTimer _routeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public void RefreshRouteInfo()
    {
        try { var route = RouteProvider?.Invoke(); _route.Text = MeasurementRouteInfo.Format(route); RubBuzz.RefreshPlaybackRoute(route?.PlaybackId); RubBuzz.RefreshCapturePathEnhancements(); }
        catch { _route.Text = MeasurementRouteInfo.Format(null); }
    }
    public Func<PinkNoiseSettings, System.Threading.CancellationToken, Task>? PinkNoiseRunner { get; set; }
    public VersionInfoView VersionInfo { get; } = new();
    public void SelectImpedance() => _tabs.SelectedItem = _impedanceTab;
    public const string RubBuzzTabHeader = "Rè (Rub & Buzz)";
    public bool IsDiagnosticRunning => _diagnosticCancellation != null || Impedance?.IsBusy == true;
    private bool _isMeasurementPending;
    public bool IsMeasurementPending
    {
        get => _isMeasurementPending;
        set { _isMeasurementPending = value; if (value) RubBuzz.StopPlayback(); }
    }

    public MeasurementDetailsWindow()
    {
        Title = "Impulse / Thông số — Line / Line chuẩn"; Width = 1200; Height = 820; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;
        ImpulseAnalysisWindow.UseDarkTitleBar(this);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ImpulseTheme.xaml", UriKind.Relative) });
        Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)); Foreground = Brushes.White;
        var layout = new DockPanel { Margin = new Thickness(10) }; Content = layout;
        DockPanel.SetDock(_status, Dock.Top); layout.Children.Add(_status);
        DockPanel.SetDock(_route, Dock.Top); layout.Children.Add(_route);
        var tabs = _tabs; layout.Children.Add(tabs);
        _looseParticle = new(MechanicalTestKind.LooseParticle, RunDiagnosticAsync, CancelDiagnostic);
        _airLeak = new(MechanicalTestKind.AirLeak, RunDiagnosticAsync, CancelDiagnostic);
        _imd = new(SignalDiagnosticKind.Imd, RunSignalDiagnosticAsync, CancelDiagnostic);
        _compression = new(SignalDiagnosticKind.Compression, RunSignalDiagnosticAsync, CancelDiagnostic);
        tabs.Items.Add(new TabItem { Header = "Impulse / ETC / Schroeder", Content = _impulse });
        tabs.Items.Add(new TabItem { Header = "Tổng quan / Thu / Effect", Content = _summary });
        tabs.Items.Add(new TabItem { Header = "Cực tính", Content = _polarity });
        tabs.Items.Add(new TabItem { Header = "IMD · Hai tone", Content = _imd });
        tabs.Items.Add(new TabItem { Header = "Compression / Limiter", Content = _compression });
        tabs.Items.Add(new TabItem { Header = "EDT / T20 / T30", Content = _decays });
        tabs.Items.Add(new TabItem { Header = "Phase / Group delay", Content = _phase });
        tabs.Items.Add(_impedanceTab);
        _pinkNoise = new PinkNoiseView(RunPinkNoiseAsync, CancelDiagnostic);
        RubBuzz.Runner = (choice, token) => RunGuardedDiagnosticAsync(async guardToken =>
        {
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token, guardToken);
            return await (RubBuzzRunner ?? throw new InvalidOperationException("Mở từ Audio Routing."))(choice, linked.Token);
        });
        // One rattle tab: the standard Pink/Chirp run on top; every other rattle tool under "Chẩn đoán nâng cao".
        var rattleTools = new TabControl { Name = "RubBuzzToolTabs", Height = 780, Margin = new Thickness(0, 8, 0, 0) };
        rattleTools.Items.Add(new TabItem { Header = "H10–H35 · từ sweep", Content = _highOrders });
        rattleTools.Items.Add(new TabItem { Header = "Hạt rác · LP", Content = _looseParticle });
        rattleTools.Items.Add(new TabItem { Header = "Rò khí · ALD", Content = _airLeak });
        rattleTools.Items.Add(new TabItem { Header = "Pink nghe thử · v1.1", Content = new ScrollViewer { Content = _pinkNoise, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        RubBuzz.AdvancedTools.Children.Add(rattleTools);
        tabs.Items.Add(new TabItem { Header = RubBuzzTabHeader, Content = new ScrollViewer { Content = RubBuzz, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Phiên bản · " + AppVersion.Display, Content = VersionInfo });
        tabs.SelectionChanged += (_, _) =>
        {
            RefreshRouteInfo();
            if (tabs.SelectedItem == _impedanceTab && Impedance == null && ImpedanceFactory != null)
                _impedanceTab.Content = new ScrollViewer { Content = Impedance = ImpedanceFactory(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        };
        Closing += async (_, e) => { if (Impedance?.IsBusy != true) return; e.Cancel = true; await Impedance.StopForShutdownAsync(); Close(); };
        _routeTimer.Tick += (_, _) => RefreshRouteInfo();
        Loaded += (_, _) => { RefreshRouteInfo(); _routeTimer.Start(); };
        Closed += (_, _) => { _routeTimer.Stop(); CancelDiagnostic(); RubBuzz.Shutdown(); };
    }

    private async Task RunPinkNoiseAsync(PinkNoiseSettings settings)
        => await RunGuardedDiagnosticAsync(async token => { await (PinkNoiseRunner ?? throw new InvalidOperationException("Mở từ Audio Routing."))(settings, token); return true; });

    internal void CancelDiagnostic() { _diagnosticCancellation?.Cancel(); Impedance?.Cancel(); RubBuzz.Cancel(); }

    private async Task<MechanicalTestResult> RunDiagnosticAsync(MechanicalTestRequest request)
        => await RunGuardedDiagnosticAsync(token => (DiagnosticRunner ?? throw new InvalidOperationException("Mở từ Audio Routing và chọn ngõ phát/thu."))(request, token));

    private async Task<SignalDiagnosticResult> RunSignalDiagnosticAsync(SignalDiagnosticRequest request)
        => await RunGuardedDiagnosticAsync(token => (SignalDiagnosticRunner ?? throw new InvalidOperationException("Mở từ Audio Routing và chọn ngõ phát/thu."))(request, token));

    private async Task<T> RunGuardedDiagnosticAsync<T>(Func<System.Threading.CancellationToken, Task<T>> run)
    {
        if (IsDiagnosticRunning) throw new InvalidOperationException("Đang đo bài khác; bấm Dừng hoặc chờ hoàn tất.");
        using var cancellation = new System.Threading.CancellationTokenSource();
        _diagnosticCancellation = cancellation;
        SetDiagnosticsBusy(true);
        try { return await run(cancellation.Token); }
        finally
        {
            _diagnosticCancellation = null;
            SetDiagnosticsBusy(false);
        }
    }

    private void SetDiagnosticsBusy(bool busy)
    {
        RubBuzz.SetBusy(busy);
        _pinkNoise.SetBusy(busy); _looseParticle.SetBusy(busy); _airLeak.SetBusy(busy); _imd.SetBusy(busy); _compression.SetBusy(busy);
    }

    public void UpdatePolarity(PolarityDiagnosticResult? result) => _polarity.Update(result);

    public void Update(string status, IReadOnlyList<MeasurementValue> summary, IReadOnlyDictionary<double, double> response, StandardAcousticResult? result)
    {
        _status.Text = status; _summary.ItemsSource = summary;
        if (!ReferenceEquals(_result, result) || result == null)
            _impulse.Content = result == null ? new TextBlock { Text = IsMeasurementPending ? "Đang thu / phân tích sweep…" : "Chưa có bản thu sweep.", Margin = new Thickness(20), Foreground = Brushes.White } : new ImpulseAnalysisView(result);
        _result = result;
        _polarity.Update(PolarityDiagnosticResult.Compare(result, "", null));
        _highOrders.Update(result?.HighOrderHarmonics);
        static string Value(double value, string format = "0.000") => double.IsFinite(value) ? value.ToString(format) : "Không khả dụng";
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
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)), Foreground = Brushes.WhiteSmoke,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
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
