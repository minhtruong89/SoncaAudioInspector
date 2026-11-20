using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ScottPlot.WPF;

namespace SoncaAudioInspector;

/// <summary>What the Ctrl+Shift+T window needs from Audio Routing.</summary>
public sealed class SubwooferControlsHost
{
    /// <summary>Locks the routing for the whole guided test; returns the active lease when one exists; throws when busy.</summary>
    public required Func<IDisposable> BeginSession { get; init; }
    public required Func<SubwooferSweepSettings, IProgress<string>?, CancellationToken, Task<SubwooferResponse>> MeasureAsync { get; init; }
    public required Func<MeasurementRouteInfo?> RouteProvider { get; init; }
    /// <summary>Station data folder; a finished session is saved under captures/ (WAV + result.json).</summary>
    public Func<string>? DataDirectory { get; init; }
}

/// <summary>Ctrl+Shift+T: tells the operator which knob/button to set, measures, and judges each control by comparison.</summary>
public sealed class SubwooferControlsWindow : Window
{
    private static readonly Brush Surface = Brush("#18181B"), Ink = Brush("#F4F4F5"), Muted = Brush("#A1A1AA"),
        Accent = Brush("#FFD54F"), Good = Brush("#34D399"), Bad = Brush("#F87171"), Edge = Brush("#34343B");
    private readonly SubwooferControlsHost _host;
    private readonly SubwooferControlSession _session = new();
    private IDisposable? _lease;
    private CancellationTokenSource? _cancellation;
    private bool? _ledAnswer;
    private bool _refreshing, _closed;
    private readonly TextBlock _status = DiagnosticViewStyle.Text("", 23, "#F4F4F5");
    private readonly TextBlock _route = DiagnosticViewStyle.Text("", 12, "#87CEFA");
    private readonly TextBlock _stepTitle = DiagnosticViewStyle.Text("", 26, "#FFD54F");
    private readonly TextBlock _instruction = DiagnosticViewStyle.Text("", 20, "#F4F4F5");
    private readonly TextBlock _ledQuestion = DiagnosticViewStyle.Text("", 17, "#FFD54F");
    private readonly TextBlock _message = DiagnosticViewStyle.Text("Chỉnh loa theo hình rồi bấm Bắt đầu.", 15, "#F4F4F5");
    private readonly TextBlock _count = DiagnosticViewStyle.Text("", 13, "#A1A1AA");
    private readonly ListBox _steps = new() { Name = "SubwooferSteps", Background = Brushes.Transparent,
        Foreground = Ink, BorderThickness = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly SubwooferPanelView _panel = new();
    private readonly Button _start, _measure, _retry, _stop, _reset, _ledYes, _ledNo, _ledNone;
    private readonly WrapPanel _ledRow = new();
    private readonly ProgressBar _progress = new() { Height = 5, Margin = new Thickness(6, 10, 6, 6),
        Foreground = Accent, Background = Edge, BorderThickness = new Thickness(0) };
    private readonly DataGrid _checks = DiagnosticViewStyle.Table();
    private readonly WpfPlot _plot = new() { Height = 300, Margin = new Thickness(0, 8, 0, 0) };
    private readonly WpfPlot _phasePlot = new() { Name = "SubwooferPhasePlot", Height = 280, Margin = new Thickness(0, 8, 0, 0) };
    private Button _export = null!;
    private string? _savedFolder;
    private static readonly ScottPlot.Color[] StepColors = [ScottPlot.Color.FromHex("#60A5FA"), ScottPlot.Color.FromHex("#FFD54F"),
        ScottPlot.Color.FromHex("#34D399"), ScottPlot.Color.FromHex("#C084FC"), ScottPlot.Color.FromHex("#FB923C")];
    private sealed record CheckRow(string HangMuc, string KetQua, string DoDuoc, string MongDoi, string GhiChu,
        Brush Mau, Brush Nen);

    public SubwooferControlSession Session => _session;

    public SubwooferControlsWindow(SubwooferControlsHost host)
    {
        _host = host;
        Title = "Test núm/nút Subwoofer — Ctrl+Shift+T"; Width = 1280; Height = 860; MinWidth = 960; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#121214"); Foreground = Ink; FontFamily = new FontFamily("Segoe UI");
        ImpulseAnalysisWindow.UseDarkTitleBar(this);
        _status.Name = "SubwooferStatus"; _status.FontWeight = FontWeights.Bold;
        _instruction.Name = "SubwooferInstruction"; _instruction.LineHeight = 29;
        _message.Name = "SubwooferMessage"; _checks.Name = "SubwooferChecks";
        _stepTitle.FontWeight = FontWeights.Bold;
        Resources[typeof(Button)] = ButtonStyle();
        ConfigureSteps(); ConfigureChecks();
        var root = new StackPanel { Margin = new Thickness(12) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        root.Children.Add(_status); root.Children.Add(_route);
        var top = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) });
        root.Children.Add(top);
        var left = new StackPanel(); top.Children.Add(left);
        var picture = new StackPanel();
        picture.Children.Add(DiagnosticViewStyle.Text("MẶT SAU LOA · VỊ TRÍ CẦN ĐẶT", 14, "#A1A1AA"));
        picture.Children.Add(new Viewbox { Child = _panel, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 0) });
        left.Children.Add(Card(picture));
        var steps = new StackPanel();
        steps.Children.Add(_count); steps.Children.Add(_steps);
        left.Children.Add(Card(steps));
        var right = new StackPanel();
        var currentCard = Card(right, true);
        Grid.SetColumn(currentCard, 1); top.Children.Add(currentCard);
        right.Children.Add(DiagnosticViewStyle.Text("BƯỚC HIỆN TẠI", 14, "#A1A1AA"));
        right.Children.Add(_stepTitle); right.Children.Add(_instruction);
        right.Children.Add(_ledQuestion);
        right.Children.Add(_ledRow);
        _ledYes = Button(_ledRow, "Đèn 180° SÁNG", "SubwooferLedYes", () => { _session.NoLedAnswered = false; _ledAnswer = true; return Task.CompletedTask; });
        _ledNo = Button(_ledRow, "Đèn 180° TẮT", "SubwooferLedNo", () => { _session.NoLedAnswered = false; _ledAnswer = false; return Task.CompletedTask; });
        _ledNone = Button(_ledRow, "Loa không có đèn 180°", "SubwooferLedNone", () => { _session.NoLedAnswered = true; _ledAnswer = null; Refresh(); return Task.CompletedTask; });
        _measure = Button(right, "✔ Đã chỉnh xong · Đo (Enter)", "SubwooferMeasure", Measure);
        _measure.FontSize = 21; _measure.MinHeight = 58; _measure.Background = Accent; _measure.Foreground = Brush("#121214");
        _measure.Margin = new Thickness(4, 16, 4, 6);
        right.Children.Add(_progress);
        right.Children.Add(_message);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) }; right.Children.Add(actions);
        _start = Button(actions, "▶ Bắt đầu", "SubwooferStart", Start);
        _stop = Button(actions, "■ Dừng", "SubwooferStop", () => { _cancellation?.Cancel(); return Task.CompletedTask; });
        _stop.Foreground = Bad; _stop.BorderBrush = Bad;
        _reset = Button(actions, "Làm lại từ đầu", "SubwooferReset", () =>
        {
            _session.Reset(); _ledAnswer = null; _steps.SelectedIndex = 0;
            _message.Text = "Đã làm lại. Chỉnh loa về vị trí chuẩn rồi bấm Đo."; return Task.CompletedTask;
        });
        _export = Button(actions, "Lưu dữ liệu / mở thư mục", "SubwooferExport", Export);
        _retry = Button(right, "↺ Đo lại bước đang chọn", "SubwooferRetry", Retry);
        _retry.ToolTip = "Chọn một bước trong danh sách bên trái rồi bấm để chuẩn bị đo lại.";
        right.Children.Add(DiagnosticViewStyle.Text("Mỗi bước quét 10–1.000 Hz × 3 lượt. So sánh các vị trí trên cùng ngõ phát/thu. "
            + "Giữ nguyên mic, âm lượng và vị trí loa đến hết bài.", 13, "#A1A1AA"));
        var results = new StackPanel();
        results.Children.Add(DiagnosticViewStyle.Text("KẾT QUẢ · 4 HẠNG MỤC", 17, "#F4F4F5")); results.Children.Add(_checks);
        root.Children.Add(Card(results));
        var graph = new StackPanel();
        graph.Children.Add(DiagnosticViewStyle.Text("ĐÁP TUYẾN CÁC BƯỚC ĐÃ ĐO", 17, "#F4F4F5"));
        graph.Children.Add(DiagnosticViewStyle.Text("Vùng tô nhạt 30–150 Hz: dải so sánh độ trễ và pha.", 13, "#A1A1AA"));
        graph.Children.Add(_plot); root.Children.Add(Card(graph));
        var phase = new StackPanel();
        phase.Children.Add(DiagnosticViewStyle.Text("PHA SO VỚI BƯỚC 1 (ĐÃ BÙ TRỄ)", 17, "#F4F4F5"));
        phase.Children.Add(DiagnosticViewStyle.Text("Pha của mỗi bước so với bước 1 sau khi trừ độ trễ đo được. Nút PHASE tốt: bước 4 nằm ngang ở ±180°. "
            + "Delay tốt: bước 3 nằm ngang ở 0°. Bước 5 phải trùng 0°. Bước 2 cong là bình thường (bộ lọc crossover tự xoay pha).", 13, "#A1A1AA"));
        phase.Children.Add(_phasePlot); root.Children.Add(Card(phase));
        DiagnosticViewStyle.Plot(_phasePlot, "Pha (°)");
        _phasePlot.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        DiagnosticViewStyle.Plot(_plot, "Mức tại mic (dB)");
        _plot.Plot.Axes.Bottom.Label.Text = "Tần số (Hz)";
        double[] ticks = [20, 30, 40, 50, 80, 100, 150, 200, 300, 400];
        _plot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), $"{f:0}")).ToArray());
        _phasePlot.Plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks.Select(f => new ScottPlot.Tick(Math.Log10(f), $"{f:0}")).ToArray());
        _phasePlot.Plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(new double[] { -180, -90, 0, 90, 180 }.Select(v => new ScottPlot.Tick(v, $"{v:0}°")).ToArray());
        _steps.SelectionChanged += (_, _) => { if (!_refreshing) Refresh(); };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && _measure.IsEnabled) { e.Handled = true; await Run(Measure); } };
        Closing += (_, _) => _cancellation?.Cancel();
        Closed += (_, _) => { _closed = true; ReleaseLease(); };
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    private static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static Brush VerdictBrush(string verdict) => verdict switch
    { "PASS" => Good, "FAIL" => Bad, "INVALID" or "REVIEW" => Accent, _ => Muted };

    private static Border Card(UIElement child, bool active = false) => new()
    {
        Child = child, Background = Surface, BorderBrush = active ? Brush("#63562C") : Edge,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12),
        Margin = new Thickness(4, 4, 4, 8), VerticalAlignment = VerticalAlignment.Top
    };

    // Local templates keep hover, keyboard focus and disabled states legible in the dark theme.
    private static Style ButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Frame";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Frame")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Frame"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Frame")); template.Triggers.Add(focus);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.75)); template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.4)); template.Triggers.Add(disabled);
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(BackgroundProperty, Brush("#27272A")));
        style.Setters.Add(new Setter(ForegroundProperty, Ink));
        style.Setters.Add(new Setter(BorderBrushProperty, Edge));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        return style;
    }

    private void ConfigureSteps()
    {
        var frame = new FrameworkElementFactory(typeof(Border)); frame.Name = "Frame";
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        frame.SetValue(Border.PaddingProperty, new Thickness(9, 7, 9, 7));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); frame.AppendChild(content);
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = frame };
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#302C1E"), "Frame"));
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Frame")); template.Triggers.Add(selected);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Muted, "Frame")); template.Triggers.Add(hover);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(MarginProperty, new Thickness(0, 2, 0, 2)));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _steps.ItemContainerStyle = style;
        ScrollViewer.SetHorizontalScrollBarVisibility(_steps, ScrollBarVisibility.Disabled);
    }

    private void ConfigureChecks()
    {
        _checks.AutoGenerateColumns = false; _checks.BorderThickness = new Thickness(0);
        _checks.Margin = new Thickness(6, 8, 6, 6); _checks.FontSize = 13;
        _checks.RowBackground = Surface; _checks.AlternatingRowBackground = Brush("#202023");
        _checks.HorizontalGridLinesBrush = Edge; _checks.CanUserResizeRows = false;
        _checks.CanUserSortColumns = false; _checks.CanUserReorderColumns = false;
        _checks.MinRowHeight = 62; _checks.ColumnHeaderHeight = 36;
        var header = new Style(typeof(DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, Brush("#27272A")));
        header.Setters.Add(new Setter(ForegroundProperty, Muted));
        header.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 6, 10, 6)));
        header.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        _checks.ColumnHeaderStyle = header;
        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        cell.Setters.Add(new Setter(ForegroundProperty, Ink));
        cell.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        cell.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        _checks.CellStyle = cell;
        void TextColumn(string title, string property, double weight)
        {
            var text = new Style(typeof(TextBlock));
            text.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            text.Setters.Add(new Setter(MarginProperty, new Thickness(10, 8, 10, 8)));
            text.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            _checks.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property),
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star), ElementStyle = text });
        }
        TextColumn("Hạng mục", "HangMuc", 1.05);
        var chip = new FrameworkElementFactory(typeof(Border));
        chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        chip.SetValue(MarginProperty, new Thickness(6, 8, 6, 8));
        chip.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
        chip.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        chip.SetBinding(Border.BackgroundProperty, new Binding("Nen"));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("KetQua"));
        label.SetBinding(TextBlock.ForegroundProperty, new Binding("Mau"));
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        label.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center); chip.AppendChild(label);
        _checks.Columns.Add(new DataGridTemplateColumn { Header = "Kết quả", Width = new DataGridLength(166),
            CellTemplate = new DataTemplate { VisualTree = chip } });
        TextColumn("Đo được", "DoDuoc", 1.55); TextColumn("Mong đợi", "MongDoi", 1.2); TextColumn("Ghi chú", "GhiChu", 2.1);
    }

    private Button Button(Panel panel, string label, string name, Func<Task> action)
    {
        var button = new Button { Content = label, Name = name, Margin = new Thickness(4),
            Padding = new Thickness(12, 9, 12, 9), FontSize = 15, FontWeight = FontWeights.SemiBold, MinHeight = 40 };
        button.Click += async (_, _) => await Run(action);
        panel.Children.Add(button); return button;
    }

    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { _message.Text = "Đã dừng; bước này chưa được ghi."; }
        catch (Exception ex) { _message.Text = ex.Message; }
        finally { if (!_closed) Refresh(); }
    }

    private Task Start()
    {
        _lease ??= _host.BeginSession();
        _session.Reset(); _ledAnswer = null;
        _steps.SelectedIndex = 0;
        _message.Text = "Chỉnh loa theo hình rồi bấm Đã chỉnh xong · Đo.";
        return Task.CompletedTask;
    }

    private async Task Measure()
    {
        if (_cancellation != null || _closed) return;
        var step = _session.Current ?? throw new InvalidOperationException("Đã đo xong; bấm Làm lại từ đầu hoặc Đo lại bước đang chọn.");
        if (step.LedQuestion != null && _ledAnswer == null && !_session.NoLedAnswered)
            throw new InvalidOperationException("Trả lời câu hỏi đèn 180° trước khi đo.");
        _lease ??= _host.BeginSession();
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _message.Text = "Đang chuẩn bị phát và thu… Giữ nguyên các núm và mic."; Refresh();
        try
        {
            var progress = new Progress<string>(text => { if (!_closed && _cancellation == cancellation) _message.Text = text; });
            var response = await _host.MeasureAsync(_session.Settings, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            bool accepted = _session.Record(response, _ledAnswer);
            _ledAnswer = null;
            _message.Text = accepted ? $"Đã ghi {StepTitle(step)}. {response.Reason}" : $"Chưa ghi bước này: {_session.LastRejected?.Reason}";
            if (accepted && _session.Current != null) _steps.SelectedIndex = _session.CurrentIndex;
            if (accepted && _session.IsComplete && _host.DataDirectory != null)
            {
                try { _savedFolder = SubwooferControlExport.Save(_session, _host.DataDirectory()); _message.Text += $" Đã lưu dữ liệu: {_savedFolder}"; }
                catch (Exception ex) when (ex is not OperationCanceledException) { _message.Text += " (Chưa lưu được dữ liệu: " + ex.Message + ")"; }
            }
        }
        finally { _cancellation = null; if (_session.IsComplete || _closed) ReleaseLease(); }
    }

    private void ReleaseLease() { var lease = _lease; _lease = null; lease?.Dispose(); }

    private Task Export()
    {
        if (_host.DataDirectory == null) throw new InvalidOperationException("Mở từ Audio Routing để có thư mục dữ liệu trạm.");
        _savedFolder = SubwooferControlExport.Save(_session, _host.DataDirectory());
        _message.Text = "Đã lưu dữ liệu: " + _savedFolder;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", '"' + _savedFolder + '"') { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private Task Retry()
    {
        int index = _steps.SelectedIndex >= 0 ? _steps.SelectedIndex : Math.Max(0, _session.CurrentIndex - 1);
        _session.GoTo(index); _ledAnswer = null;
        _message.Text = $"Đo lại {StepTitle(_session.Current!)}: chỉnh loa theo hình rồi bấm Đo.";
        return Task.CompletedTask;
    }

    private static string Verdict(string value) => value switch
    {
        "PASS" => "ĐẠT", "FAIL" => "KHÔNG ĐẠT", "INVALID" => "THU KHÔNG HỢP LỆ", "REVIEW" => "CẦN DUYỆT", _ => "CHỜ ĐO"
    };

    private static string StepTitle(SubwooferStep step) => step.Id switch
    {
        SubwooferControlProcedure.Baseline => "1 · Vị trí chuẩn",
        SubwooferControlProcedure.CrossoverMin => "2 · Tần số cắt 40 Hz",
        SubwooferControlProcedure.DelayMax => "3 · Độ trễ 20 ms",
        SubwooferControlProcedure.Phase180 => "4 · Đảo pha 180°", _ => "5 · Trả về và xác nhận"
    };

    private ListBoxItem StepItem(SubwooferStep step, int index)
    {
        var response = _session.Response(step.Id);
        bool current = index == _session.CurrentIndex, rejected = current && _session.LastRejected != null;
        Brush color = rejected ? Bad : current ? Accent : response != null ? Good : Muted;
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = DiagnosticViewStyle.Text(rejected ? "✗" : current ? "▶" : response != null ? "✓" : "○", 16);
        icon.Margin = new Thickness(0); icon.Foreground = color; row.Children.Add(icon);
        var title = DiagnosticViewStyle.Text(StepTitle(step), 15); title.Margin = new Thickness(0, 0, 6, 0);
        title.Foreground = color; title.FontWeight = current ? FontWeights.Bold : FontWeights.Normal;
        Grid.SetColumn(title, 1); row.Children.Add(title);
        string detail = rejected ? "Thu bị từ chối" : response != null ? $"SNR {DiagnosticViewStyle.Number(response.SnrDb, "0")} dB" : current ? "Đang làm" : "Chờ";
        var note = DiagnosticViewStyle.Text(detail, 12); note.Margin = new Thickness(0, 2, 0, 0); note.Foreground = color;
        Grid.SetColumn(note, 2); row.Children.Add(note);
        var item = new ListBoxItem { Content = row, ToolTip = rejected ? _session.LastRejected!.Reason : step.Instruction };
        System.Windows.Automation.AutomationProperties.SetName(item, StepTitle(step) + ". " + detail);
        return item;
    }

    private static void LedChoice(Button button, string label, bool selected)
    {
        button.Content = selected ? "✓ " + label : label;
        button.Background = selected ? Brush("#3B3420") : Brush("#27272A");
        button.BorderBrush = selected ? Accent : Edge; button.Foreground = selected ? Accent : Ink;
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "Đã chọn" : "Chưa chọn");
    }

    private void Refresh()
    {
        if (_refreshing) return; // ItemsSource/SelectedIndex below raise SelectionChanged
        _refreshing = true;
        try { RefreshCore(); } finally { _refreshing = false; }
    }

    private void RefreshCore()
    {
        bool busy = _cancellation != null;
        var step = _session.Current;
        try { _route.Text = MeasurementRouteInfo.Format(_host.RouteProvider()); } catch { }
        string verdict = _session.Verdict;
        _status.Text = "SUBWOOFER · TEST NÚM / NÚT — " + (busy ? "ĐANG ĐO" : Verdict(verdict));
        _status.Foreground = busy ? Accent : VerdictBrush(verdict);
        _count.Text = $"TRÌNH TỰ 5 BƯỚC · {_session.Measured.Count()}/5 ĐÃ ĐO";
        int selected = _steps.SelectedIndex;
        _steps.ItemsSource = _session.Steps.Select(StepItem).ToArray();
        _steps.SelectedIndex = selected >= 0 ? selected : Math.Min(_session.CurrentIndex, _session.Steps.Count - 1);
        _steps.IsEnabled = !busy;
        _stepTitle.Text = step == null ? "ĐÃ ĐO XONG 5 BƯỚC" : StepTitle(step);
        _instruction.Text = step == null ? "Xem kết quả bên dưới. Muốn đo lại một bước: chọn bước bên trái rồi bấm Đo lại." : step.Instruction;
        bool askLed = step?.LedQuestion != null;
        _ledQuestion.Text = askLed ? step!.LedQuestion + " Chọn một câu trả lời:" : "";
        _ledQuestion.Visibility = askLed ? Visibility.Visible : Visibility.Collapsed;
        _ledRow.Visibility = askLed ? Visibility.Visible : Visibility.Collapsed;
        LedChoice(_ledYes, "Đèn 180° SÁNG", _ledAnswer == true && !_session.NoLedAnswered);
        LedChoice(_ledNo, "Đèn 180° TẮT", _ledAnswer == false && !_session.NoLedAnswered);
        LedChoice(_ledNone, "Loa không có đèn 180°", _session.NoLedAnswered);
        _panel.Show(step?.Panel ?? SubwooferControlProcedure.Steps[0].Panel,
            step == null ? [] : SubwooferControlProcedure.Actions(_session.CurrentIndex), _ledAnswer);
        _export.IsEnabled = !busy && _session.Measured.Any() && _host.DataDirectory != null;
        _start.IsEnabled = !busy;
        _measure.IsEnabled = !busy && step != null && (!askLed || _ledAnswer != null || _session.NoLedAnswered);
        _measure.ToolTip = askLed && _ledAnswer == null && !_session.NoLedAnswered ? "Trả lời câu hỏi đèn 180° để bật nút Đo." : "Đã chỉnh loa đúng hình: bấm hoặc nhấn Enter để đo.";
        _progress.IsIndeterminate = busy; _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = !busy && _session.Response(SubwooferControlProcedure.Baseline) != null;
        _stop.IsEnabled = busy; _reset.IsEnabled = !busy;
        _ledYes.IsEnabled = _ledNo.IsEnabled = _ledNone.IsEnabled = !busy;
        _checks.ItemsSource = _session.Checks.Select(c => new CheckRow(c.Control switch
        { "Crossover" => "Tần số cắt (Crossover)", "Time aligning delay" => "Căn chỉnh thời gian trễ", "Phase 180°" => "Đảo pha 180°", _ => c.Control },
            Verdict(c.Verdict), c.Measured, c.Expected, c.Message, VerdictBrush(c.Verdict),
            c.Verdict switch { "PASS" => Brush("#12352B"), "FAIL" => Brush("#3D2024"), "INVALID" or "REVIEW" => Brush("#3B3420"), _ => Brush("#27272A") })).ToArray();
        RenderPlot();
    }

    private void RenderPlot()
    {
        _plot.Plot.Clear();
        var band = _plot.Plot.Add.HorizontalSpan(Math.Log10(30), Math.Log10(150));
        band.FillColor = ScottPlot.Color.FromHex("#FFD54F").WithAlpha(0.06); band.LineWidth = 0;
        int i = 0;
        foreach (var (step, response) in _session.Measured)
        {
            var line = _plot.Plot.Add.Scatter(response.FrequenciesHz.Select(Math.Log10).ToArray(), response.LevelDb);
            line.MarkerSize = 0; line.LineWidth = 2;
            line.Color = StepColors[StepIndex(step)]; line.LegendText = StepTitle(step); i++;
        }
        StyleLegend(_plot, i > 0);
        _plot.Plot.Axes.AutoScale();
        _plot.Plot.Axes.SetLimitsX(Math.Log10(20), Math.Log10(400)); _plot.Refresh();
        RenderPhasePlot();
    }

    private int StepIndex(SubwooferStep step) => _session.Steps.ToList().FindIndex(s => s.Id == step.Id);

    private static void StyleLegend(WpfPlot plot, bool visible)
    {
        plot.Plot.Legend.IsVisible = visible;
        plot.Plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#18181B");
        plot.Plot.Legend.FontColor = ScottPlot.Color.FromHex("#F4F4F5");
    }

    /// <summary>Phase of each step relative to step 1 with the measured delay removed (SubwooferRelation.PhaseCurveDeg).</summary>
    private void RenderPhasePlot()
    {
        _phasePlot.Plot.Clear();
        foreach (double reference in new[] { -180.0, 0, 180 })
        {
            var guide = _phasePlot.Plot.Add.HorizontalLine(reference, 1, ScottPlot.Color.FromHex("#52525B"));
            guide.LinePattern = ScottPlot.LinePattern.Dashed;
        }
        int count = 0;
        foreach (var (step, _) in _session.Measured)
        {
            if (_session.RelationToBaseline(step.Id) is not { IsValid: true } relation || relation.PhaseCurveHz.Length == 0) continue;
            var line = _phasePlot.Plot.Add.Scatter(relation.PhaseCurveHz.Select(Math.Log10).ToArray(), relation.PhaseCurveDeg);
            line.MarkerSize = 0; line.LineWidth = 2; line.Color = StepColors[StepIndex(step)];
            line.LegendText = $"{StepTitle(step)} · {relation.PhaseDeg:+0;-0}° · trễ {relation.DelayMs:+0.0;-0.0} ms"; count++;
        }
        StyleLegend(_phasePlot, count > 0);
        _phasePlot.Plot.Axes.SetLimits(Math.Log10(20), Math.Log10(400), -220, 220); _phasePlot.Refresh();
    }
}

/// <summary>Mimic of the rear panel: shows the target position of each control and highlights the one to change.</summary>
public sealed class SubwooferPanelView : Canvas
{
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(212, 212, 216));
    private static readonly Brush Highlight = new SolidColorBrush(Color.FromRgb(255, 213, 79));
    private static readonly Brush Inactive = new SolidColorBrush(Color.FromRgb(113, 113, 122));
    public SubwooferPanelView() { Width = 640; Height = 340; Background = Brushes.Transparent; IsHitTestVisible = false; }

    /// <summary>Every control in actions is highlighted and listed in the callout (a step can need several changes).</summary>
    public void Show(SubwooferPanelState target, IReadOnlyList<SubwooferAction> actions, bool? ledAnswer)
    {
        Children.Clear();
        bool Active(SubwooferControl control) => actions.Any(a => a.Control == control);
        double xo = Math.Log(Math.Clamp(target.CrossoverHz, 40, 180) / 40) / Math.Log(180 / 40.0);
        Knob(94, 80, xo, "40Hz", "180Hz", "CROSSOVER\nFREQUENCY", Active(SubwooferControl.Crossover));
        Knob(262, 80, Math.Clamp(target.DelayMs / 20, 0, 1), "0ms", "20ms", "TIME ALIGNING\nDELAY", Active(SubwooferControl.Delay));
        bool phase = Active(SubwooferControl.Phase);
        var box = new Rectangle { Width = 148, Height = 148, RadiusX = 8, RadiusY = 8,
            Stroke = phase ? Highlight : Line, StrokeThickness = phase ? 4 : 1.5 };
        if (phase) Glow(box);
        Place(box, 354, 38);
        var label = new Border { Width = 146, Height = 31, Background = Brushes.Black,
            CornerRadius = new CornerRadius(0, 0, 7, 7), Child = new TextBlock { Text = "PHASE", FontSize = 18,
                Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Place(label, 355, 154);
        var push = new Ellipse { Width = 55, Height = 55, Stroke = phase ? Highlight : Line,
            Fill = new SolidColorBrush(Color.FromRgb(32, 32, 35)), StrokeThickness = phase ? 4 : 1.5 };
        if (phase) Glow(push);
        Place(push, 369, 73);
        var led = new Ellipse { Width = 26, Height = 26, Stroke = target.Phase180 ? Brushes.Tomato : Line, StrokeThickness = 2.5,
            Fill = target.Phase180 ? new SolidColorBrush(Color.FromRgb(248, 70, 70)) : Brushes.Black };
        if (target.Phase180) led.Effect = new DropShadowEffect { Color = Colors.Red, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.7 };
        Place(led, 459, 68); Place(Text("180°", 17), 454, 42);
        var otg = new Rectangle { Width = 78, Height = 29, RadiusX = 11, RadiusY = 11, Stroke = Inactive, StrokeThickness = 1.5 };
        Place(otg, 541, 106); Place(Text("OTG", 17, Inactive), 562, 147);
        var hex = new Polygon { Stroke = Inactive, StrokeDashArray = [2, 2], StrokeThickness = 2,
            Points = new PointCollection(Enumerable.Range(0, 6).Select(k => new Point(510 + 34 * Math.Cos(Math.PI / 3 * k), 260 + 34 * Math.Sin(Math.PI / 3 * k)))) };
        Children.Add(hex);
        Place(new Ellipse { Width = 29, Height = 29, Stroke = Inactive, StrokeThickness = 2.5 }, 495.5, 245.5);
        string action = actions.Count == 0 ? "ĐÃ ĐO XONG" : string.Join("\n", actions.Select((a, i) => actions.Count > 1 ? $"{(char)('①' + i)} {a.Text}" : a.Text));
        var callout = new Border { Width = 452, Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(Color.FromRgb(51, 45, 27)), BorderBrush = Highlight,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = Text(action, actions.Count > 2 ? 17 : 20, Highlight) };
        Place(callout, 10, 232);
        // ledAnswer is the LED state the operator reported; the drawing shows the target state.
        // The drawing always shows the physical position the operator must set.
        System.Windows.Automation.AutomationProperties.SetName(this, "Mặt sau loa. " + action
            + (ledAnswer.HasValue ? ". Đã trả lời " + (ledAnswer.Value ? "Có" : "Không") : ""));
    }

    private void Knob(double cx, double cy, double position, string min, string max, string caption, bool active)
    {
        const double r = 66;
        var brush = active ? Highlight : Line;
        var arc = new Path { Stroke = brush, StrokeThickness = active ? 5 : 2, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, Data = new PathGeometry([new PathFigure(Point(cx, cy, r, 225), [
            new ArcSegment(Point(cx, cy, r, -45), new Size(r, r), 0, true, SweepDirection.Clockwise, true)], false)]) };
        if (active) Glow(arc);
        Children.Add(arc);
        Place(new Ellipse { Width = 60, Height = 60, Stroke = brush, StrokeThickness = active ? 3 : 1.5,
            Fill = new SolidColorBrush(Color.FromRgb(32, 32, 35)) }, cx - 30, cy - 30);
        double angle = 225 - 270 * position;
        Children.Add(new System.Windows.Shapes.Line { X1 = cx, Y1 = cy, X2 = Point(cx, cy, r - 9, angle).X, Y2 = Point(cx, cy, r - 9, angle).Y,
            Stroke = active ? Highlight : Brushes.WhiteSmoke, StrokeThickness = 5, StrokeEndLineCap = PenLineCap.Round });
        Place(Text(min, 17), cx - r - 4, cy + 57); Place(Text(max, 17), cx + r - 40, cy + 57);
        var name = Text(caption, 18); name.Width = 158; Place(name, cx - 79, cy + 90);
    }

    private static void Glow(UIElement element) => element.Effect = new DropShadowEffect
        { Color = Color.FromRgb(255, 213, 79), BlurRadius = 17, ShadowDepth = 0, Opacity = 0.65 };

    private static Point Point(double cx, double cy, double r, double degrees)
        => new(cx + r * Math.Cos(degrees * Math.PI / 180), cy - r * Math.Sin(degrees * Math.PI / 180));
    private static TextBlock Text(string text, double size, Brush? brush = null)
        => new() { Text = text, FontSize = size, Foreground = brush ?? Line, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private void Place(UIElement element, double x, double y) { SetLeft(element, x); SetTop(element, y); Children.Add(element); }
}
