using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>Edits a detached model; only the explicit Save action commits it.</summary>
public sealed class ModelConfigEditorWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBox _json;
    private readonly TextBlock _error;
    public Action<ModelConfig>? SaveModel { get; set; }
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ModelConfigEditorWindow(ModelConfig? model)
    {
        Title = model == null ? "Thêm model — Ctrl+Shift+N" : "Sửa model — Ctrl+Shift+M";
        Width = 900; Height = 740; MinWidth = 650; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        var grid = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Content = grid;
        var heading = new TextBlock { Text = "Tên model", FontSize = 15 };
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
        _json = new TextBox { Name = "ModelJson", Text = JsonSerializer.Serialize(initial, JsonOptions), AcceptsReturn = true, AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(8), Background = Brushes.Black, Foreground = Brushes.White };
        Grid.SetRow(_json, 2); grid.Children.Add(_json);
        _error = new TextBlock { Foreground = Brushes.Gold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10),
            Text = "Sửa route, kênh, bài đo và giới hạn trong JSON. Tên ở ô trên được dùng khi lưu. Lưu tạo bản dự phòng .bak." };
        Grid.SetRow(_error, 3); grid.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Hủy", IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Name = "SaveModel", Content = "Lưu model", MinWidth = 120, Padding = new Thickness(12, 7, 12, 7) };
        save.Click += (_, _) =>
        {
            try { SaveModel?.Invoke(ParseModel(_name.Text, _json.Text)); DialogResult = true; }
            catch (Exception ex) { _error.Text = ex.Message; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
    }

    public static ModelConfig ParseModel(string name, string json)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Nhập tên model.");
        var model = JsonSerializer.Deserialize<ModelConfig>(json) ?? throw new InvalidDataException("JSON model trống.");
        model.model = name.Trim();
        var config = model.testItems?.InOut ?? throw new InvalidDataException("Thiếu testItems.InOut.");
        if (config.Devices?.Input == null || config.Devices.Output == null || config.Tests == null || config.Tests.Count == 0)
            throw new InvalidDataException("Cần Devices.Input, Devices.Output và ít nhất một bài đo Tests.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in config.Tests)
        {
            test.ImpulseEffects?.Validate();
            if (string.IsNullOrWhiteSpace(test.id) || !ids.Add(test.id.Trim())) throw new InvalidDataException("Mỗi bài đo phải có id riêng.");
            if (!config.Devices.Input.ContainsKey(test.PlaybackOut) || !config.Devices.Output.ContainsKey(test.RecordingIn))
                throw new InvalidDataException($"{test.id}: Playback Out / Recording In không khớp khóa Devices.");
            if (test.PlaybackChannel < 1 || test.RecordingChannel < 1) throw new InvalidDataException($"{test.id}: kênh bắt đầu từ 1; null là cả hai kênh.");
        }
        return model;
    }
}
