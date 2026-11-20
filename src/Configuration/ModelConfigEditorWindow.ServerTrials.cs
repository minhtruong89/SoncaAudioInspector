using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed partial class ModelConfigEditorWindow
{
    private ConfigTrialEditorSession? _serverTrial;
    private ComboBox _serverRevisions = null!;
    private TextBox _revisionNote = null!;
    private Button _applyServerTrial = null!;
    private readonly List<Button> _serverActionButtons = new();
    private Button FooterButton(string text, Action action)
    {
        var button = ActionButton(text, action);
        button.Width = 180; button.Height = 36; button.Padding = new Thickness(8, 0, 8, 0);
        _serverActionButtons.Add(button);
        return button;
    }
    private void RefreshServerTrialActions()
    {
        foreach (var button in _serverActionButtons) button.IsEnabled = !_saving;
        var revision = _serverRevisions.SelectedItem as AudioConfigRevision;
        _applyServerTrial.IsEnabled = !_saving && revision?.Status is "DRAFT" or "PENDING" or "REJECTED";
    }
    private ConfigTrialEditorSession ServerTrial => _serverTrial ??= new(candidate =>
    {
        if (SaveTemporaryModel == null) throw new InvalidOperationException("Chưa nối ngữ cảnh phiên app.");
        SaveTemporaryModel(candidate);
    });
    private void AddServerTrialControls(StackPanel footer, Panel buttons)
    {
        var save = FooterButton("Lưu lên server", () => { _ = ServerActionAsync(SaveServerTrialAsync); }); save.Name = "SaveServerTrial";
        save.ToolTip = "Lưu cấu hình lên server ở trạng thái chờ duyệt PENDING và dùng thử trong phiên app; kết quả đo lưu vào dữ liệu thử.";
        var load = FooterButton("Lưu danh sách thử", () => { _ = ServerActionAsync(LoadServerTrialsAsync); }); load.Name = "LoadServerTrials";
        load.ToolTip = "Lấy danh sách cấu hình thử đã lưu trên server để chọn dùng lại.";
        buttons.Children.Add(save); buttons.Children.Add(load);
        _revisionNote = new TextBox { Name = "ServerRevisionNote", MaxLength = 500, ToolTip = "Ghi chú ngắn (tùy chọn)",
            Margin = new Thickness(0, 3, 0, 5), Padding = new Thickness(5), Background = Brushes.Black, Foreground = Brushes.White };
        footer.Children.Add(new TextBlock { Text = "Cấu hình thử trên server · ghi chú (tùy chọn)", Foreground = Brushes.LightGray });
        footer.Children.Add(_revisionNote);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var apply = _applyServerTrial = FooterButton("Dùng bản thử đã chọn", () => Guard(ApplySelectedServerTrial)); apply.Name = "ApplyServerTrial";
        apply.ToolTip = "Thay nội dung đang sửa bằng bản đã chọn trên server và dùng cho lần đo tiếp theo trong phiên app.";
        DockPanel.SetDock(apply, Dock.Right); row.Children.Add(apply);
        _serverRevisions = new ComboBox { Name = "ServerTrialRevisions", MinWidth = 0, MaxDropDownHeight = 250, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        _serverRevisions.SelectionChanged += (_, _) => { ServerTrial.Select(_serverRevisions.SelectedItem as AudioConfigRevision); RefreshServerTrialActions(); };
        row.Children.Add(_serverRevisions); footer.Children.Add(row);
        footer.Children.Add(new TextBlock { Text = "Dùng cấu hình thử → lưu LINE CHUẨN thử → Auto Test → gửi DỮ LIỆU THỬ. Đổi cấu hình cần lấy lại LINE CHUẨN thử.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        RefreshServerTrialActions();
    }
    private async Task ServerActionAsync(Func<Task> action)
    {
        if (_saving) return;
        _saving = true; _tabs.IsEnabled = _name.IsEnabled = false;
        RefreshServerTrialActions();
        try { await action(); _error.Foreground = Brushes.MediumSpringGreen; }
        catch (EndpointNameGateException) { _error.Foreground = Brushes.Gold; }
        catch (Exception ex) { ShowEditorError(new InvalidOperationException("Server/offline: " + ex.Message + " · bản đang sửa vẫn được giữ.")); }
        finally { _saving = false; _tabs.IsEnabled = _name.IsEnabled = true; RefreshServerTrialActions(); }
    }
    private async Task SaveServerTrialAsync()
    {
        var candidate = ReadEditorModel();
        if (!ConfirmEndpointNames(candidate)) throw new EndpointNameGateException(_error.Text);
        var revision = await ServerTrial.SaveAsync(candidate, _revisionNote.Text.Trim());
        _serverRevisions.ItemsSource = new[] { revision }; _serverRevisions.SelectedItem = revision;
        _error.Text = "Đã lưu lên server · " + revision.Status + " · " + SessionModelConfigurations.Current.TrialBanner(revision.SpeakerModel);
    }
    private async Task LoadServerTrialsAsync()
    {
        var revisions = await ServerEngine.ListAudioConfigRevisionsAsync(_name.Text.Trim());
        _serverRevisions.ItemsSource = revisions;
        _error.Text = $"{revisions.Length} bản · {revisions.Count(r => r.CreatedById == ServerEngine.StaffID)} bản của tôi. "
            + "Chọn DRAFT/PENDING/REJECTED rồi bấm Dùng bản thử đã chọn.";
    }
    private void ApplySelectedServerTrial()
    {
        if (_saving) return;
        var revision = _serverRevisions.SelectedItem as AudioConfigRevision ?? throw new InvalidOperationException("Chọn một bản thử.");
        if (revision.SpeakerModel != _name.Text.Trim()) throw new InvalidOperationException("Bản thử không khớp model đang sửa.");
        ServerTrial.Apply(revision); _draft = ModelConfigEditorWindow.ParseModel(revision.SpeakerModel, revision.ConfigJson);
        RefreshForm(); UpdateJsonProjection();
        _error.Text = SessionModelConfigurations.Current.TrialBanner(revision.SpeakerModel);
    }
}
