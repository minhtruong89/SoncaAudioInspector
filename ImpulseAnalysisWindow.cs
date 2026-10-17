using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using PlotColor = ScottPlot.Color;

namespace SoncaAudioInspector;

public sealed class ImpulseAnalysisWindow : Window
{
    public ImpulseAnalysisWindow(StandardAcousticResult result)
    {
        Title = "Impulse / ETC / Schroeder — cùng lượt sweep";
        Width = 1200; Height = 800; MinWidth = 850; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ImpulseAnalysisView(result);
    }
}

public sealed class ImpulseAnalysisView : UserControl
{
    private readonly StandardAcousticResult _result;
    private readonly WpfPlot _plot = new(), _navigator = new();
    private readonly CheckBox _showIr = Toggle("Impulse", "#FF302B"), _showEtc = Toggle("Envelope (ETC)", "#EE00E9"),
        _showDecay = Toggle("Schroeder Integral", "#F4F4F5"), _showMetrics = Toggle("Thông số", "#D4D4D8"),
        _showRegression = Toggle("Hồi quy", "#FBBF24");
    private readonly ComboBox _regressionChoice = new() { Width = 66, Margin = new Thickness(3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _readout = new() { Foreground = Brushes.LightSkyBlue, Margin = new Thickness(12, 5, 6, 5) };
    private readonly Border _metrics;
    private readonly Scatter _ir, _etc, _decay;
    private readonly VerticalLine _cursor;
    private readonly HorizontalSpan _selection;
    private readonly Dictionary<string, Scatter> _regressions = new();
    private readonly IReadOnlyList<TimeDomainPoint> _irPoints, _etcPoints;
    private readonly double _start, _end;
    private bool _draggingNavigator;

    public ImpulseAnalysisView(StandardAcousticResult result)
    {
        _result = result;
        Background = new SolidColorBrush(Color.FromRgb(22, 22, 24));
        _irPoints = result.ImpulsePlot?.Impulse ?? result.ImpulseResponse;
        _etcPoints = result.ImpulsePlot?.Envelope ?? result.EnergyTimeCurve;
        _start = Math.Min(0, _irPoints.FirstOrDefault()?.TimeSeconds ?? 0);
        _end = Math.Max(.1, _irPoints.LastOrDefault()?.TimeSeconds ?? .1);
        var layout = new Grid { Margin = new Thickness(6) }; Content = layout;
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(82), GridLength.Auto, GridLength.Auto })
            layout.RowDefinitions.Add(new RowDefinition { Height = height });
        var toolbar = new WrapPanel { Margin = new Thickness(4) };
        toolbar.Children.Add(new TextBlock { Text = "No Filter", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 5, 16, 5) });
        toolbar.Children.Add(_showMetrics); toolbar.Children.Add(_showRegression); toolbar.Children.Add(_regressionChoice);
        var reset = new Button { Content = "Toàn bộ IR", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 4, 0) };
        reset.Click += (_, _) => ResetView(); toolbar.Children.Add(reset);
        toolbar.Children.Add(new TextBlock { Text = "Cuộn để zoom · kéo để dịch · chọn vùng ở thanh dưới", Foreground = Brushes.Silver, Margin = new Thickness(10, 5, 0, 5) });
        layout.Children.Add(toolbar);
        var graph = new Grid(); Grid.SetRow(graph, 1); layout.Children.Add(graph); graph.Children.Add(_plot);
        ApplyPlotStyle(_plot); ApplyPlotStyle(_navigator);
        _ir = Trace(_plot, _irPoints, p => Db(p.Value), "#FF302B", "Impulse", true);
        _etc = Trace(_plot, _etcPoints, p => p.Value, "#EE00E9", "Envelope (ETC)");
        _decay = Trace(_plot, result.EnergyDecayCurve.Select(p => new TimeDomainPoint(p.TimeSeconds, p.LevelDb)).ToArray(),
            p => p.Value, "#F4F4F5", "Schroeder Integral");
        _decay.LineWidth = 2;
        var origin = _plot.Plot.Add.VerticalLine(0); origin.Color = PlotColor.FromHex("#888888"); origin.LineWidth = 1;
        _cursor = _plot.Plot.Add.VerticalLine(0); _cursor.Color = PlotColor.FromHex("#60A5FA"); _cursor.IsVisible = false;
        _plot.Plot.Axes.Left.Label.Text = "dBr theo đỉnh IR";
        _plot.Plot.Axes.Bottom.Label.Text = "Thời gian từ direct (s)";
        _plot.ToolTip = "IR/ETC theo đỉnh IR; Schroeder theo tổng năng lượng từ t=0. Chế độ mức FFT không bị thay đổi.";
        _metrics = BuildMetrics(); graph.Children.Add(_metrics);
        _plot.SizeChanged += (_, _) => PositionMetrics();
        Grid.SetRow(_navigator, 2); layout.Children.Add(_navigator);
        Trace(_navigator, _irPoints, p => Db(p.Value), "#FF302B", "", true);
        Trace(_navigator, _etcPoints, p => p.Value, "#EE00E9", "");
        _selection = _navigator.Plot.Add.HorizontalSpan(_start, _end);
        _selection.FillColor = PlotColor.FromHex("#60A5FA").WithAlpha(.12);
        _selection.LineColor = PlotColor.FromHex("#60A5FA"); _selection.LineWidth = 1;
        _navigator.Plot.Axes.SetLimits(_start, _end, -160, 5);
        _navigator.Plot.Axes.Left.IsVisible = false; _navigator.Plot.Axes.Bottom.IsVisible = false;
        _navigator.ToolTip = "Kéo tại đây để di chuyển vùng đang xem trên đồ thị chính.";
        var legend = new WrapPanel { Margin = new Thickness(55, 2, 0, 0) };
        legend.Children.Add(_showIr); legend.Children.Add(_showEtc); legend.Children.Add(_showDecay); legend.Children.Add(_readout);
        Grid.SetRow(legend, 3); layout.Children.Add(legend);
        var note = new TextBlock { Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 4),
            Text = result.ImpulseEffects?.Describe() ?? result.Validity + " · " + result.Warning };
        Grid.SetRow(note, 4); layout.Children.Add(note);
        BuildRegressions();
        _showIr.Click += (_, _) => { _ir.IsVisible = _showIr.IsChecked == true; _plot.Refresh(); };
        _showEtc.Click += (_, _) => { _etc.IsVisible = _showEtc.IsChecked == true; _plot.Refresh(); };
        _showDecay.Click += (_, _) => { _decay.IsVisible = _showDecay.IsChecked == true; _plot.Refresh(); };
        _showMetrics.Click += (_, _) => _metrics.Visibility = _showMetrics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _showRegression.Click += (_, _) => UpdateRegression();
        _regressionChoice.SelectionChanged += (_, _) => UpdateRegression();
        _plot.Plot.RenderManager.AxisLimitsChanged += (_, _) => SyncNavigator();
        _plot.MouseMove += (_, e) => UpdateCursor(e.GetPosition(_plot));
        _plot.MouseLeave += (_, _) => { _cursor.IsVisible = false; _plot.Refresh(); };
        _navigator.PreviewMouseLeftButtonDown += (_, e) => { _draggingNavigator = true; _navigator.CaptureMouse(); MoveNavigator(e.GetPosition(_navigator)); e.Handled = true; };
        _navigator.PreviewMouseMove += (_, e) => { if (_draggingNavigator) { MoveNavigator(e.GetPosition(_navigator)); e.Handled = true; } };
        _navigator.PreviewMouseLeftButtonUp += (_, e) => { _draggingNavigator = false; _navigator.ReleaseMouseCapture(); e.Handled = true; };
        ResetView();
    }

    private static CheckBox Toggle(string label, string color) => new()
    { Content = label, IsChecked = true, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(8, 5, 8, 5) };

    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-9, Math.Abs(value)));
    private static string Number(double value, string format = "0.000") => double.IsFinite(value) ? value.ToString(format) : "N/A";

    private static Scatter Trace(WpfPlot plot, IReadOnlyList<TimeDomainPoint> points, Func<TimeDomainPoint, double> value,
        string color, string name, bool fill = false)
    {
        var trace = plot.Plot.Add.Scatter(points.Select(p => p.TimeSeconds).ToArray(), points.Select(p => Math.Max(-180, value(p))).ToArray());
        trace.Color = PlotColor.FromHex(color); trace.MarkerSize = 0; trace.LineWidth = 1; trace.LegendText = name;
        trace.FillY = fill; trace.FillYValue = -180; trace.FillYColor = trace.Color.WithAlpha(.7);
        return trace;
    }

    private static void ApplyPlotStyle(WpfPlot plot)
    {
        plot.Plot.FigureBackground.Color = PlotColor.FromHex("#161618");
        plot.Plot.DataBackground.Color = PlotColor.FromHex("#101011");
        plot.Plot.Axes.Color(PlotColor.FromHex("#D4D4D8"));
        plot.Plot.Grid.MajorLineColor = PlotColor.FromHex("#39393C");
        plot.Plot.Legend.IsVisible = false;
    }

    private Border BuildMetrics()
    {
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = "No Filter", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 5, 2, 8) });
        void Row(string name, string value, bool trusted = true)
        {
            var line = new Grid { Margin = new Thickness(5, 4, 5, 4) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock { Text = name, Foreground = Brushes.LightGray, FontSize = 12 });
            var text = new TextBlock { Text = value, Foreground = trusted ? Brushes.LightSkyBlue : Brushes.Orange, FontSize = 12, FontStyle = trusted ? FontStyles.Normal : FontStyles.Italic };
            Grid.SetColumn(text, 1); line.Children.Add(text); rows.Children.Add(line);
        }
        foreach (var estimate in _result.DecayEstimates)
        {
            double r = double.IsFinite(estimate.RSquared) ? -Math.Sqrt(Math.Clamp(estimate.RSquared, 0, 1)) : double.NaN;
            Row($"{estimate.Name} {estimate.StartDb:0}..{estimate.EndDb:0} dB", $"{Number(estimate.Rt60Seconds)} s; r {Number(r)}", estimate.IsValid && r <= -.98);
        }
        var t20 = _result.DecayEstimates.FirstOrDefault(p => p.Name == "T20");
        var t30 = _result.DecayEstimates.FirstOrDefault(p => p.Name == "T30");
        double curvature = t20?.Rt60Seconds > 0 && t30 != null ? 100 * (t30.Rt60Seconds / t20.Rt60Seconds - 1) : double.NaN;
        Row("Curvature", Number(curvature, "0.0") + " %", t20?.IsValid == true && t30?.IsValid == true);
        Row("Clarity C20", Number(_result.ImpulsePlot?.ClarityC20Db ?? double.NaN, "0.00") + " dB");
        Row("Clarity C50", Number(_result.ClarityC50Db, "0.00") + " dB");
        Row("Clarity C80", Number(_result.ClarityC80Db, "0.00") + " dB");
        Row("Definition D50", Number(_result.DefinitionD50Percent, "0.0") + " %");
        Row("Centre time TS", Number(_result.ImpulsePlot?.CentreTimeSeconds ?? double.NaN) + " s");
        return new Border { Child = rows, Background = new SolidColorBrush(Color.FromArgb(224, 30, 30, 34)), BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Width = 260, Margin = new Thickness(55, 35, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Màu cam: hồi quy chưa tin cậy/không đủ dữ liệu. RT60 chẩn đoán; không dùng làm kết luận mất effect." };
    }

    private void BuildRegressions()
    {
        foreach (var estimate in _result.DecayEstimates)
        {
            _regressionChoice.Items.Add(estimate.Name);
            var selected = _result.EnergyDecayCurve.Where(p => p.LevelDb <= estimate.StartDb && p.LevelDb >= estimate.EndDb).ToArray();
            if (selected.Length < 2 || !double.IsFinite(estimate.SlopeDbPerSecond)) continue;
            double intercept = selected.Average(p => p.LevelDb) - estimate.SlopeDbPerSecond * selected.Average(p => p.TimeSeconds);
            var fit = _plot.Plot.Add.Scatter(new[] { selected[0].TimeSeconds, selected[^1].TimeSeconds },
                new[] { intercept + estimate.SlopeDbPerSecond * selected[0].TimeSeconds, intercept + estimate.SlopeDbPerSecond * selected[^1].TimeSeconds });
            fit.Color = PlotColor.FromHex("#FBBF24"); fit.LineWidth = 2; fit.MarkerSize = 0; fit.IsVisible = false;
            _regressions[estimate.Name] = fit;
        }
        _regressionChoice.SelectedItem = _regressionChoice.Items.Contains("T20") ? "T20" : _regressionChoice.Items.Cast<object>().FirstOrDefault();
        UpdateRegression();
    }

    private void UpdateRegression()
    {
        foreach (var pair in _regressions) pair.Value.IsVisible = _showRegression.IsChecked == true && pair.Key == _regressionChoice.SelectedItem as string;
        _plot.Refresh();
    }

    private void ResetView()
    {
        _plot.Plot.Axes.SetLimits(_start, _end, -160, 8); _plot.Refresh(); SyncNavigator();
    }

    private void SyncNavigator()
    {
        var limits = _plot.Plot.Axes.GetLimits(); _selection.X1 = limits.Left; _selection.X2 = limits.Right; _navigator.Refresh();
        PositionMetrics();
    }

    private void PositionMetrics()
    {
        var limits = _plot.Plot.Axes.GetLimits();
        double width = limits.Right - limits.Left;
        if (width <= 0) return;
        double zeroPixel = 55 + Math.Max(0, _plot.ActualWidth - 70) * -limits.Left / width;
        bool fitsLeft = zeroPixel > 330;
        _metrics.HorizontalAlignment = fitsLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _metrics.Margin = fitsLeft ? new Thickness(55, 35, 0, 0) : new Thickness(0, 35, 20, 0);
    }

    private void MoveNavigator(Point position)
    {
        var limits = _plot.Plot.Axes.GetLimits();
        double width = Math.Min(_end - _start, limits.Right - limits.Left);
        double center = _navigator.Plot.GetCoordinates((float)(position.X * _navigator.DisplayScale), (float)(position.Y * _navigator.DisplayScale)).X;
        double left = Math.Clamp(center - width / 2, _start, _end - width);
        _plot.Plot.Axes.SetLimitsX(left, left + width); _plot.Refresh(); SyncNavigator();
    }

    private void UpdateCursor(Point position)
    {
        double time = _plot.Plot.GetCoordinates((float)(position.X * _plot.DisplayScale), (float)(position.Y * _plot.DisplayScale)).X;
        if (time < _start || time > _end) return;
        static double Nearest(IReadOnlyList<TimeDomainPoint> points, double time)
        {
            if (points.Count == 0) return double.NaN;
            int low = 0, high = points.Count - 1;
            while (low < high) { int middle = (low + high) / 2; if (points[middle].TimeSeconds < time) low = middle + 1; else high = middle; }
            if (low > 0 && Math.Abs(points[low - 1].TimeSeconds - time) < Math.Abs(points[low].TimeSeconds - time)) low--;
            return points[low].Value;
        }
        _cursor.X = time; _cursor.IsVisible = true;
        _readout.Text = $"{time * 1000:0.00} ms · IR {Number(Db(Nearest(_irPoints, time)), "0.0")} · ETC {Number(Nearest(_etcPoints, time), "0.0")} dBr";
        _plot.Refresh();
    }
}
