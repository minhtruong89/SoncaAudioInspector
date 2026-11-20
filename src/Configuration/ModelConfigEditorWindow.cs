using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>Edits a detached model; only the explicit Save action commits it.</summary>
public sealed partial class ModelConfigEditorWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBox _json;
    private readonly TextBlock _error;
    private bool _saving;
    public Action<ModelConfig>? SaveTemporaryModel { get; set; }
    public Action<ModelConfig>? SaveModel { get; set; }
    public Func<ModelConfig, Task<ModelConfig>>? PublishModel { get; set; }
    /// <summary>Friendly names currently selected in the main window's CẤU HÌNH ĐƯỜNG ÂM THANH panel.</summary>
    public Func<(string? Playback, string? Recording)>? RoutingEndpointNames { get; set; }
    /// <summary>Active Windows endpoints; null disables the name check (offline harnesses, admin stations without the bench).</summary>
    public Func<IReadOnlyList<WindowsEndpointName>>? WindowsEndpoints { get; set; }
    private string? _acknowledgedEndpointIssues;
    private sealed class EndpointNameGateException(string message) : InvalidOperationException(message);

    /// <summary>
    /// A wrong Windows device name is reported here, before the trial configuration is used, instead of as a missing device at
    /// measurement time. A second press with the same issues proceeds (e.g. the device is intentionally unplugged).
    /// </summary>
    private bool ConfirmEndpointNames(ModelConfig candidate)
    {
        if (WindowsEndpoints == null) return true;
        IReadOnlyList<EndpointNameIssue> issues;
        try { issues = ModelEndpointCheck.Check(candidate.testItems.InOut, WindowsEndpoints()); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { return true; }
        if (issues.Count == 0) { _acknowledgedEndpointIssues = null; return true; }
        string signature = ModelEndpointCheck.Describe(issues);
        if (_acknowledgedEndpointIssues == signature) return true;
        _acknowledgedEndpointIssues = signature;
        _error.Foreground = Brushes.Gold;
        _error.Text = "Tên thiết bị chưa khớp Windows — CHƯA dùng cấu hình này:\n" + signature + "\nBấm lại lần nữa nếu vẫn muốn dùng (ví dụ thiết bị đang rút).";
        return false;
    }
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ModelConfigEditorWindow(ModelConfig? model)
    {
        Title = model == null ? "Thêm model — Ctrl+Shift+N" : "Sửa model — Ctrl+Shift+M";
        Width = 1120; Height = 850; MinWidth = 820; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ImpulseTheme.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ConfigTheme.xaml", UriKind.Relative) });
        ImpulseAnalysisWindow.UseDarkTitleBar(this);
        var grid = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Content = grid;
        var heading = new TextBlock { Text = "Tên model — cấu hình bài đo & tiêu chí", FontSize = 16 };
        grid.Children.Add(heading);
        _name = new TextBox { Name = "ModelName", Text = model?.model ?? "", FontSize = 16, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(6) };
        Grid.SetRow(_name, 1); grid.Children.Add(_name);
        var initial = model ?? new ModelConfig
        {
            testItems = new TestItems { InOut = new InOutConfig
            {
                Devices = new DevicesConfig { Input = new() { ["OUT1"] = "Tên thiết bị phát" }, Output = new() { ["IN1"] = "Tên thiết bị thu" } },
                Tests = new() { new TestConfig { id = "IO001", name = "Đo đáp tuyến và THD", PlaybackOut = "OUT1", RecordingIn = "IN1", FrequencyResponseMethod = "LogSweep" } }
            } }
        };
        _json = new TextBox { Name = "ModelJson", IsReadOnly=true, Text = JsonSerializer.Serialize(initial, JsonOptions), AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(8), Background = Brushes.Black, Foreground = Brushes.White };
        _error = new TextBlock { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10),
            Text = "Dùng cấu hình thử trong phiên app; kết quả đo và ảnh được lưu vào dữ liệu thử. JSON chỉ sửa sau khi bấm Edit." };
        Grid.SetRow(_error, 3); grid.Children.Add(_error);
        var form = CreateForm(initial); Grid.SetRow(form, 2); grid.Children.Add(form);
        var footer = new StackPanel();
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddServerTrialControls(footer, buttons);
        var cancel = FooterButton("Đóng", () => Close()); cancel.IsCancel = true;
        var save = FooterButton("Gửi cấu hình thử", () => SaveTemporary(true)); save.Name = "SaveModel";
        save.ToolTip = "Dùng nội dung đang sửa cho lần đo tiếp theo trong phiên app. Kết quả được lưu vào dữ liệu thử.";
        if (Environment.GetEnvironmentVariable("SONCA_CONFIG_ADMIN") == "1")
        {
            var admin = new Button { Name = "PublishModel", Content = "Lưu & đồng bộ (admin)", Margin = new Thickness(0,0,10,0), Padding = new Thickness(12,7,12,7) };
            admin.Click += async (_,_) => await SaveAndSynchronizeAsync(true); buttons.Children.Add(admin);
        }
        Closing+=(_,e)=>{if(_saving)e.Cancel=true;};
        buttons.Children.Add(cancel); buttons.Children.Add(save); footer.Children.Add(buttons); Grid.SetRow(footer, 4); grid.Children.Add(footer);

    }

    private async Task SaveAndSynchronizeAsync(bool closeOnSuccess)
    {
        if (Environment.GetEnvironmentVariable("SONCA_CONFIG_ADMIN") != "1")
            throw new InvalidOperationException("Lưu & đồng bộ chỉ có trong SONCA_CONFIG_ADMIN=1.");
        if(_saving) return;
        bool localSaved=false;
        try
        {
            var candidate=ReadEditorModel(); SaveModel?.Invoke(candidate);localSaved=true;
            _saving=true; _tabs.IsEnabled=_name.IsEnabled=false;
            if(PublishModel!=null)
            {
                _error.Text="Đã lưu tại máy; đang đồng bộ và đọc lại từ server…";
                _draft=await PublishModel(candidate); RefreshForm();
            }
            _error.Foreground=Brushes.MediumSpringGreen;
            _error.Text=PublishModel==null?"Đã lưu model tại máy.":"Đã lưu model và xác minh đồng bộ server.";
            _saving=false;
            if(closeOnSuccess) DialogResult=true;
        }
        catch(Exception ex) {ShowEditorError(localSaved?new InvalidOperationException("Đã lưu tại máy; chưa đồng bộ server: "+ex.Message):ex);}
        finally {_saving=false;_tabs.IsEnabled=_name.IsEnabled=true;}
    }

    private void SaveTemporary(bool closeOnSuccess)
    {
        if (_saving) return;
        try
        {
            var candidate = ReadEditorModel();
            if (SaveTemporaryModel == null) throw new InvalidOperationException("Chưa nối ngữ cảnh phiên app.");
            if (!ConfirmEndpointNames(candidate)) return;
            SaveTemporaryModel(candidate);
            _error.Text = "Đã lưu tạm trong RAM · " + ResultPersistencePolicy.TrialLabel;
            if (closeOnSuccess) DialogResult = true;
        }
        catch (Exception ex) { ShowEditorError(ex); }
    }

    public static ModelConfig ParseModel(string name, string json)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Nhập tên model.");
        var model = JsonSerializer.Deserialize<ModelConfig>(json) ?? throw new InvalidDataException("JSON model trống.");
        model.model = name.Trim();
        var config = model.testItems?.InOut ?? throw new InvalidDataException("Thiếu testItems.InOut.");
        if (config.BleSelectStrongestByModel && (config.FirmwareModelId is not ushort firmwareId || SoncaFirmwareModels.Current.Find(firmwareId).Count == 0))
            throw new InvalidDataException("Dò BLE theo RSSI cần FirmwareModelId có tên trong file model firmware.");
        config.BleNamePrefix = config.EffectiveBleNamePrefix;
        if (config.BleNamePrefix is { Length: > 40 } || config.BleNamePrefix?.Any(char.IsControl) == true)
            throw new InvalidDataException("Tiền tố tên BLE tối đa 40 ký tự, không chứa ký tự điều khiển.");
        C360ModelDefinition.NormalizeCaptureAlias(config);
        config.TestVolume?.Validate();
        if (config.Devices?.Input == null || config.Devices.Output == null || config.Tests == null || config.Tests.Count == 0)
            throw new InvalidDataException("Cần Devices.Input, Devices.Output và ít nhất một bài đo Tests.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var effectiveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in config.Tests)
        {
            test.ImpulseEffects?.Validate();
            test.ValidateAdditionalTests();
            test.ValidateWindowsLevels();
            test.ValidateMeasurementCriteria();
            test.ValidateDipWindows();
            test.ValidateRunPolicy();
            if (string.IsNullOrWhiteSpace(test.id) || !ids.Add(test.id.Trim())) throw new InvalidDataException("Mỗi bài đo phải có id riêng.");
            var effectiveId = TestSequenceOrder.EffectiveIdentity(test);
            if (string.IsNullOrWhiteSpace(effectiveId) || !effectiveIds.Add(effectiveId)) throw new InvalidDataException($"Trùng định danh tham chiếu LINE CHUẨN ({effectiveId}). Mỗi bài đo phải có ReferenceTestId/id hiệu lực riêng.");
            if (!config.Devices.Input.ContainsKey(test.PlaybackOut) || !config.Devices.Output.ContainsKey(test.RecordingIn))
                throw new InvalidDataException($"{test.id}: Playback Out / Recording In không khớp khóa Devices.");
            if (test.PlaybackChannel < 1 || test.RecordingChannel < 1) throw new InvalidDataException($"{test.id}: kênh bắt đầu từ 1; null là cả hai kênh.");
        }
        if (C360ModelDefinition.Matches(config)) C360ModelDefinition.Validate(config);
        return model;
    }
}
