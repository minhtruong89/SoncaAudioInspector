using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoncaAudioInspector.Update;

namespace SoncaAudioInspector;

public sealed class VersionInfoView : UserControl
{
    public const string HistoryUrl = "https://inventory.acnos.store/update";
    private readonly ReleaseNotesView _features = new();
    private readonly TextBlock _feed = Text("", "VersionFeed");
    private readonly TextBlock _status = Text("", "VersionUpdateStatus");
    private readonly Button _check = Button("Kiểm tra cập nhật", "CheckVersionUpdate");
    private readonly ComboBox _channel = new() { Name = "UpdateChannel", MinWidth = 460, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _versions = new() { Name = "VersionCatalog", MinWidth = 640, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _loadVersions = Button("Tải danh sách phiên bản", "LoadVersionCatalog");
    private readonly Button _install = Button("Cài phiên bản đã chọn", "InstallChosenVersion");
    private bool _updatingChannel;
    private UpdateCheckState _state = new();
    public IReadOnlyList<ComboBoxItem> CatalogItems => _versions.Items.OfType<ComboBoxItem>().ToArray();
    public VersionInfoView()
    {
        _features.Summary.Name = "VersionFeatures";
        var stack = new StackPanel { Margin = new Thickness(24), MaxWidth = 1050, HorizontalAlignment = HorizontalAlignment.Left };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        string assembly = typeof(AppVersion).Assembly.Location;
        var title = Text($"{AppVersion.DisplayWithChannel} ({AppVersion.Numeric})", "RunningVersion");
        title.FontSize = 25; title.Foreground = Brushes.MediumSpringGreen; stack.Children.Add(title);
        stack.Children.Add(Text("Ngày build (giờ máy): " + File.GetLastWriteTime(assembly).ToString("dd/MM/yyyy HH:mm:ss"), "VersionBuildDate"));
        stack.Children.Add(Text("TÍNH NĂNG CỦA BẢN ĐANG CHẠY", "VersionFeaturesHeading")); stack.Children.Add(_features);
        stack.Children.Add(Text("KÊNH CẬP NHẬT", "VersionFeedHeading")); stack.Children.Add(_feed);
        foreach (var kind in new[] { UpdateChannelKind.Stable, UpdateChannelKind.Beta })
            _channel.Items.Add(new ComboBoxItem { Content = UpdateChannelPreference.Describe(kind), Tag = kind });
        stack.Children.Add(_channel);
        stack.Children.Add(Muted("Máy ở kênh Ổn định không bao giờ được mời cập nhật bản beta. Bản beta chỉ dành cho máy chọn kênh Beta; "
            + "nếu bản beta không ổn, dùng mục Cài phiên bản khác bên dưới để hạ về bản ổn định.", "UpdateChannelHelp"));
        stack.Children.Add(_status);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; stack.Children.Add(actions);
        actions.Children.Add(_check);
        var history = Button("Lịch sử phiên bản", "VersionHistory"); actions.Children.Add(history);
        stack.Children.Add(Text("CÀI PHIÊN BẢN KHÁC / HẠ CẤP", "VersionCatalogHeading"));
        stack.Children.Add(Muted("Chọn bất kỳ bản đã phát hành (kể cả bản cũ hơn) để cài lại. App kiểm chữ ký và SHA-256, chỉ tải file khác, "
            + "thay file có sao lưu/rollback và giữ nguyên dữ liệu trạm.", "VersionCatalogHelp"));
        var catalogActions = new WrapPanel(); stack.Children.Add(catalogActions);
        catalogActions.Children.Add(_loadVersions); catalogActions.Children.Add(_install);
        stack.Children.Add(_versions);
        _channel.SelectionChanged += (_, _) =>
        {
            if (_updatingChannel || _channel.SelectedItem is not ComboBoxItem { Tag: UpdateChannelKind kind } || kind == _state.Channel) return;
            try { _state.SetChannel(kind); _state.SetStatus("Đã chọn kênh " + (kind == UpdateChannelKind.Beta ? "beta" : "ổn định") + "; bấm Kiểm tra cập nhật."); }
            catch (Exception ex) { _state.SetStatus("Lỗi lưu kênh: " + ShortError(ex)); RefreshState(); }
        };
        _versions.SelectionChanged += (_, _) => RefreshState();
        _loadVersions.Click += async (_, _) =>
        {
            if (_state.CatalogRequested == null) return;
            _loadVersions.IsEnabled = false;
            try { ShowCatalog(await _state.CatalogRequested(CancellationToken.None)); }
            catch (Exception ex) { _state.SetStatus("Lỗi đọc danh sách phiên bản: " + ShortError(ex)); }
            finally { RefreshState(); }
        };
        _install.Click += async (_, _) =>
        {
            if (_state.InstallVersionRequested == null || _versions.SelectedItem is not ComboBoxItem { Tag: string version } || Window.GetWindow(this) is not { } owner) return;
            _install.IsEnabled = false;
            try { await _state.InstallVersionRequested(owner, version); }
            catch (Exception ex) { _state.SetStatus("Lỗi: " + ShortError(ex)); }
            finally { RefreshState(); }
        };
        _check.Click += async (_, _) =>
        {
            if (_state.CheckRequested == null || Window.GetWindow(this) is not { } owner) return;
            _check.IsEnabled = false;
            try { await _state.CheckRequested(owner); }
            catch (Exception ex) { _state.SetStatus("Lỗi: " + ShortError(ex)); }
            finally { _check.IsEnabled = _state.CheckRequested != null; }
        };
        history.Click += (_, _) =>
        {
            try { Process.Start(HistoryStartInfo()); }
            catch (Exception ex) { _state.SetStatus("Lỗi: " + ShortError(ex)); }
        };
        Loaded += (_, _) => { _state.PropertyChanged -= StateChanged; _state.PropertyChanged += StateChanged; RefreshState(); };
        Unloaded += (_, _) => _state.PropertyChanged -= StateChanged;
        LoadFromDirectory(AppContext.BaseDirectory); RefreshState();
    }
    public static ProcessStartInfo HistoryStartInfo() => new(HistoryUrl) { UseShellExecute = true };
    public void Bind(UpdateCheckState state)
    {
        _state.PropertyChanged -= StateChanged; _state = state;
        if (IsLoaded) _state.PropertyChanged += StateChanged;
        RefreshState();
    }
    public void LoadFromDirectory(string directory)
    {
        string path = Path.Combine(directory, "version-info.json");
        try
        {
            if (!File.Exists(path)) { _features.SetNotes("Bản phát triển — chưa có ghi chú phát hành", []); return; }
            var info = JsonSerializer.Deserialize<InstalledVersionInfo>(File.ReadAllBytes(path), UpdateManifest.JsonOptions)
                ?? throw new InvalidDataException("Nội dung rỗng.");
            if (UpdateManifest.ParseVersion(info.Version) != AppVersion.Current || info.ReleasedUtc == default
                || info.Features == null || info.Features.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Version hoặc danh sách tính năng không hợp lệ.");
            _features.SetNotes(info.Features.FirstOrDefault() ?? "Chưa có ghi chú tính năng cho bản này.", info.Features.Skip(1));
        }
        catch (Exception ex) { _features.SetNotes("Không đọc được version-info.json: " + ShortError(ex), []); }
    }
    private static string ShortError(Exception ex)
    {
        string message = ex is JsonException ? "JSON không hợp lệ." : ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 180 ? message[..180] + "…" : message;
    }
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshState(); else _ = Dispatcher.BeginInvoke(RefreshState);
    }
    /// <summary>Newest first; the running version and the stable/beta pointers are labelled.</summary>
    public void ShowCatalog(UpdateCatalog catalog)
    {
        _versions.Items.Clear();
        foreach (var entry in catalog.NewestFirst())
        {
            var version = UpdateManifest.ParseVersion(entry.Version);
            string tags = (entry.Version == catalog.StableVersion ? " · ỔN ĐỊNH MỚI NHẤT" : "") + (entry.Version == catalog.BetaVersion ? " · BETA MỚI NHẤT" : "")
                + (version == AppVersion.Current ? " · đang chạy" : version < AppVersion.Current ? " · hạ cấp" : "");
            _versions.Items.Add(new ComboBoxItem { Tag = entry.Version,
                Content = $"{entry.DisplayVersion}{tags} · {entry.ReleasedUtc.LocalDateTime:dd/MM/yyyy} · {UpdateManifest.SingleLine(entry.Summary)}" });
        }
        _versions.SelectedItem = _versions.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == catalog.StableVersion);
        _state.SetStatus($"Đã tải {catalog.Versions.Length} phiên bản (ổn định {catalog.StableVersion}" + (catalog.BetaVersion == null ? ")." : $", beta {catalog.BetaVersion})."));
        RefreshState();
    }
    private void RefreshState()
    {
        _feed.Text = _state.Feed; _status.Text = _state.Status; _check.IsEnabled = _state.CheckRequested != null;
        _updatingChannel = true;
        try { _channel.SelectedItem = _channel.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is UpdateChannelKind k && k == _state.Channel); }
        finally { _updatingChannel = false; }
        _loadVersions.IsEnabled = _state.CatalogRequested != null;
        _install.IsEnabled = _state.InstallVersionRequested != null && _versions.SelectedItem is ComboBoxItem { Tag: string v }
            && UpdateManifest.ParseVersion(v) != AppVersion.Current;
    }
    private static TextBlock Muted(string text, string name)
    {
        var block = Text(text, name); block.FontSize = 13; block.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)); return block;
    }
    private static TextBlock Text(string text, string name) => new() { Text = text, Name = name, TextWrapping = TextWrapping.Wrap,
        FontSize = 15, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 6, 0, 8) };
    private static Button Button(string text, string name) => new() { Content = text, Name = name, Padding = new Thickness(16, 9, 16, 9),
        Margin = new Thickness(0, 0, 12, 0), Background = new SolidColorBrush(Color.FromRgb(39, 39, 42)), Foreground = Brushes.WhiteSmoke };
}
