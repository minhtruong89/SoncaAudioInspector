using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed class ConfigApprovalWindow : Window
{
    public ListBox PendingList { get; } = new() { Name = "PendingRevisions", Height = 110,
        Background = new SolidColorBrush(Color.FromRgb(15, 15, 17)), Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(63, 63, 70)) };
    public TextBox DiffText { get; } = ReadOnly("ApprovalDiff");
    public TextBox Reason { get; } = new() { Name = "RejectReason", MaxLength = 4000, Padding = new Thickness(6) };
    public TextBlock Status { get; } = new() { Name = "ApprovalStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gold };
    public Button Accept { get; } = new() { Name = "ApproveRevision", Content = "Chấp nhận", Padding = new Thickness(15, 8, 15, 8) };
    private readonly TextBox _json = ReadOnly("ApprovalJson");
    private readonly TextBlock _metadata = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 8) };
    private ConfigApprovalReview? _review;
    private bool _busy;
    public bool ApprovedAny { get; private set; }
    public Func<Task>? AfterApproved { get; set; }
    public ConfigApprovalWindow(IEnumerable<AudioConfigRevision> revisions)
    {
        Title = "Cấu hình chờ duyệt · ADMIN"; Width = 1040; Height = 820; MinWidth = 760; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ConfigTheme.xaml", UriKind.Relative) });
        ImpulseAnalysisWindow.UseDarkTitleBar(this);
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(36, 62, 84))));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); rowStyle.Triggers.Add(selected); PendingList.ItemContainerStyle = rowStyle;
        var grid = new Grid { Margin = new Thickness(20) }; Content = grid;
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Add(grid, new TextBlock { Text = "Có cấu hình chờ duyệt", FontSize = 22, Margin = new Thickness(0, 0, 0, 10) }, 0);
        Add(grid, PendingList, 1); Add(grid, _metadata, 2); Add(grid, DiffText, 3);
        var expander = new Expander { Header = "JSON đầy đủ (đề xuất)", Content = _json, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) };
        _json.MaxHeight = 150; Add(grid, expander, 4);
        var reasonPanel = new StackPanel(); reasonPanel.Children.Add(new TextBlock { Text = "Lý do từ chối (bắt buộc)", Margin = new Thickness(0, 6, 0, 4) });
        reasonPanel.Children.Add(Reason); Add(grid, reasonPanel, 5); Add(grid, Status, 6);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var reject = new Button { Name = "RejectRevision", Content = "Từ chối", Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(15, 8, 15, 8) };
        var later = new Button { Content = "Để sau", IsCancel = true, Padding = new Thickness(15, 8, 15, 8) };
        Accept.Click += async (_, _) => await PerformAsync(true); reject.Click += async (_, _) => await PerformAsync(false);
        later.Click += (_, _) => Close(); buttons.Children.Add(Accept); buttons.Children.Add(reject); buttons.Children.Add(later); Add(grid, buttons, 7);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        PendingList.ItemsSource = revisions.ToList(); PendingList.SelectionChanged += (_, _) => SelectRevision();
        if (PendingList.Items.Count > 0) PendingList.SelectedIndex = 0;
    }
    private static void Add(Grid grid, UIElement child, int row) { Grid.SetRow(child, row); grid.Children.Add(child); }
    private static TextBox ReadOnly(string name) => new() { Name = name, IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = Brushes.Black, Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10) };
    private void SelectRevision()
    {
        _review = PendingList.SelectedItem is AudioConfigRevision r ? new ConfigApprovalReview(r) : null;
        if (_review == null) return;
        var revision = _review.Revision;
        _metadata.Text = $"{revision.SpeakerModel} · người gửi: {revision.CreatedById} · gửi lúc: {revision.SubmittedAt ?? revision.CreatedAt:O}\nGhi chú: {revision.Note}";
        _json.Text = revision.ConfigJson; DiffText.Text = _review.Diff; Reason.Clear(); Status.Text = ""; Accept.Content = "Chấp nhận";
    }
    public async Task PerformAsync(bool approve)
    {
        if (_busy || _review == null) return;
        _busy = true; PendingList.IsEnabled = Accept.IsEnabled = false;
        try
        {
            if (approve)
            {
                if (!await _review.ApproveAsync())
                {
                    DiffText.Text = _review.Diff; Status.Text = "Cấu hình ACTIVE đã đổi. Hãy xem lại diff mới và bấm xác nhận để rebase rồi duyệt.";
                    Accept.Content = "Xác nhận diff mới & chấp nhận"; return;
                }
                ApprovedAny = true;
                // Remove first: a refresh failure must never invite approving the same revision twice.
                RemoveSelected(); Status.Text = "Đã duyệt. Đang tải ACTIVE đã ký…";
                if (AfterApproved != null) await AfterApproved();
                Status.Text = "Đã duyệt; đã tải lại ACTIVE.";
            }
            else { await _review.RejectAsync(Reason.Text); RemoveSelected(); Status.Text = "Đã từ chối."; }
        }
        catch (ServerEngine.ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        { Status.Text = "Server chưa sẵn sàng/thiếu khóa ký (503). Chưa duyệt cấu hình. " + ex.Message; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; PendingList.IsEnabled = Accept.IsEnabled = true; }
    }
    private void RemoveSelected()
    {
        var remaining = PendingList.Items.Cast<AudioConfigRevision>().Where(r => r.Id != _review!.Revision.Id).ToList();
        PendingList.ItemsSource = remaining; if (remaining.Count > 0) PendingList.SelectedIndex = 0;
    }
}
