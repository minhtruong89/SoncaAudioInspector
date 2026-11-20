using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed partial class ModelConfigEditorWindow
{
    private ModelConfig _draft = new();
    private TabControl _tabs = null!;
    private ListBox _testList = null!;
    private StackPanel _fields = null!, _fieldTarget = null!;
    private readonly List<Action> _commits = new();
    private TestConfig? _editing;
    private CheckBox? _testEnableCheck;
    private bool _refreshing, _buildingForm, _updatingJson, _jsonPending;
    private FrameworkElement _formLayout=null!;
    private Button _editJson=null!;

    private TabControl CreateForm(ModelConfig initial)
    {
        _draft = JsonSerializer.Deserialize<ModelConfig>(JsonSerializer.Serialize(initial, JsonOptions))!;
        C360ModelDefinition.NormalizeCaptureAlias(_draft.testItems.InOut);
        _tabs = new TabControl();
        var layout = new Grid { Margin = new Thickness(12) };
        _formLayout=layout;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(0, 0, 15, 0) }; layout.Children.Add(sidebar);
        var actions = new StackPanel { Orientation = Orientation.Vertical }; DockPanel.SetDock(actions, Dock.Bottom); sidebar.Children.Add(actions);

        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        var btnAdd = ActionButton("+ Thêm bài", () => Guard(() =>
        {
            if (!_json.IsReadOnly) return;
            Flush();
            int i = 1; while (_draft.testItems.InOut.Tests.Any(t => t.id == $"IO{i:000}")) i++;
            var devices = _draft.testItems.InOut.Devices;
            var test = new TestConfig { id = $"IO{i:000}", name = "Bài đo mới", PlaybackOut = devices.Input.Keys.FirstOrDefault() ?? "OUT1", RecordingIn = devices.Output.Keys.FirstOrDefault() ?? "MIC1", FrequencyResponseMethod = "LogSweep" };
            _draft.testItems.InOut.Tests.Add(test);
            TestSequenceOrder.RenumberByPosition(_draft.testItems.InOut.Tests);
            TestSequenceOrder.EnsureUniqueIdentityForAddedTest(_draft.testItems.InOut.Tests, test);
            RefreshTests(test);
            UpdateJsonProjection();
        }));
        btnAdd.Padding = new Thickness(6, 6, 6, 6); btnAdd.MinWidth = 65;
        row1.Children.Add(btnAdd);

        var btnDel = ActionButton("Xóa bài", () => Guard(() =>
        {
            if (!_json.IsReadOnly) return;
            if (_editing == null || _draft.testItems.InOut.Tests.Count <= 1) return;
            int idx = _draft.testItems.InOut.Tests.IndexOf(_editing);
            _draft.testItems.InOut.Tests.Remove(_editing);
            TestSequenceOrder.RenumberByPosition(_draft.testItems.InOut.Tests);
            _commits.Clear();
            var next = _draft.testItems.InOut.Tests[Math.Clamp(idx, 0, _draft.testItems.InOut.Tests.Count - 1)];
            RefreshTests(next);
            UpdateJsonProjection();
        }));
        btnDel.Padding = new Thickness(6, 6, 6, 6); btnDel.MinWidth = 55;
        row1.Children.Add(btnDel);

        var btnUp = ActionButton("▲", () => Guard(MoveSelectedTestUp));
        btnUp.Name = "BtnMoveTestUp";
        btnUp.ToolTip = "Di chuyển lên (Alt+Up)";
        btnUp.Padding = new Thickness(6, 6, 6, 6);
        btnUp.MinWidth = 32;
        row1.Children.Add(btnUp);

        var btnDown = ActionButton("▼", () => Guard(MoveSelectedTestDown));
        btnDown.Name = "BtnMoveTestDown";
        btnDown.ToolTip = "Di chuyển xuống (Alt+Down)";
        btnDown.Padding = new Thickness(6, 6, 6, 6);
        btnDown.MinWidth = 32;
        row1.Children.Add(btnDown);

        actions.Children.Add(row1);

        var row2 = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var btnRenumber = ActionButton("Đánh số lại theo vị trí", () => Guard(RenumberTests));
        btnRenumber.Name = "BtnRenumberTests";
        btnRenumber.ToolTip = "Đánh số lại ID các bài theo thứ tự IO001, IO002...";
        btnRenumber.HorizontalAlignment = HorizontalAlignment.Stretch;
        btnRenumber.Margin = new Thickness(0, 0, 7, 4);
        row2.Children.Add(btnRenumber);

        actions.Children.Add(row2);

        var template = new DataTemplate(typeof(TestConfig));
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        factory.SetValue(StackPanel.MarginProperty, new Thickness(2, 3, 2, 3));

        var checkFactory = new FrameworkElementFactory(typeof(CheckBox));
        checkFactory.SetValue(CheckBox.ContentProperty, "Bật");
        checkFactory.SetValue(CheckBox.ForegroundProperty, Brushes.LightGray);
        checkFactory.SetValue(CheckBox.MarginProperty, new Thickness(0, 0, 8, 0));
        checkFactory.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
        checkFactory.SetBinding(CheckBox.IsCheckedProperty, new Binding("IsEnabled") { Mode = BindingMode.OneWay });
        checkFactory.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(OnInlineTestEnableClicked));

        var textFactory = new FrameworkElementFactory(typeof(TextBlock));
        textFactory.SetBinding(TextBlock.TextProperty, new Binding("DisplaySequenceLabel"));
        textFactory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        textFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        factory.AppendChild(checkFactory);
        factory.AppendChild(textFactory);
        template.VisualTree = factory;

        _testList = new ListBox
        {
            Name = "ConfigTests",
            Background = Brushes.Black,
            Foreground = Brushes.White,
            ItemTemplate = template,
            AllowDrop = true
        };
        sidebar.Children.Add(_testList);

        _testList.PreviewKeyDown += (s, e) =>
        {
            e.Handled = HandleSequenceKey(e.Key, Keyboard.Modifiers);
        };

        Point? dragStartPoint = null;
        _testList.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (!_json.IsReadOnly || FindVisualParent<CheckBox>(e.OriginalSource as DependencyObject) != null)
            {
                dragStartPoint = null;
                return;
            }
            dragStartPoint = e.GetPosition(_testList);
        };

        _testList.PreviewMouseMove += (s, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || dragStartPoint == null) return;
            var currentPoint = e.GetPosition(_testList);
            var diff = dragStartPoint.Value - currentPoint;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var item = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
                if (item?.DataContext is TestConfig test)
                {
                    dragStartPoint = null;
                    DragDrop.DoDragDrop(item, test, DragDropEffects.Move);
                }
            }
        };

        _testList.Drop += (s, e) =>
        {
            if (!_json.IsReadOnly) return;
            TestConfig? droppedData = e.Data.GetData(typeof(TestConfig)) as TestConfig;
            if (droppedData == null) return;
            int fromIndex = _draft.testItems.InOut.Tests.IndexOf(droppedData);
            if (fromIndex < 0) return;

            var targetItem = FindVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
            int toIndex = targetItem?.DataContext is TestConfig targetTest
                ? _draft.testItems.InOut.Tests.IndexOf(targetTest)
                : _draft.testItems.InOut.Tests.Count - 1;

            if (toIndex >= 0 && fromIndex != toIndex)
            {
                Flush();
                if (TestSequenceOrder.MoveTest(_draft.testItems.InOut.Tests, fromIndex, toIndex))
                {
                    RefreshTests(droppedData);
                    UpdateJsonProjection();
                }
            }
        };

        _fields = new StackPanel { Margin = new Thickness(4, 0, 12, 10) };
        var scroll = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        _tabs.Items.Add(new TabItem { Header = "Bài đo & tiêu chí PASS/FAIL", Content = layout });
        _testList.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            try { CommitFields(); BuildTest(_testList.SelectedItem as TestConfig); UpdateJsonProjection(); }
            catch (Exception ex) { ShowEditorError(ex); _refreshing = true; _testList.SelectedItem = _editing; _refreshing = false; }
        };
        var json = new DockPanel { Margin = new Thickness(12) };
        var help = new StackPanel(); DockPanel.SetDock(help, Dock.Top); json.Children.Add(help);
        help.Children.Add(Note("JSON đang chỉ đọc. Bấm Edit để sửa; Áp dụng để kiểm tra và khóa lại. Bấm Gửi cấu hình thử để dùng trong phiên app; kết quả được lưu vào dữ liệu thử."));
        _editJson=ActionButton("Edit",()=>Guard(EnableJsonEditing)); _editJson.Name="EditJson"; help.Children.Add(_editJson);
        help.Children.Add(ActionButton("Áp dụng JSON vào bài đo", () => Guard(() => { ApplyJson(); _error.Text = "Đã nạp và khóa JSON. Bấm Gửi cấu hình thử để áp dụng trong phiên."; })));
        json.Children.Add(_json); _tabs.Items.Add(new TabItem { Header = "JSON nâng cao", Content = json });
        _json.TextChanged += (_, _) => { if (!_updatingJson && !_refreshing) _jsonPending = true; };
        _name.TextChanged += (_, _) => SynchronizeForm();
        // Navigation never validates or rebuilds controls. Invalid JSON cannot trap focus.
        RefreshForm(); return _tabs;
    }

    private bool HandleSequenceKey(Key key, ModifierKeys modifiers)
    {
        if (!_json.IsReadOnly || modifiers != ModifierKeys.Alt) return false;
        if (key == Key.Up) { Guard(MoveSelectedTestUp); return true; }
        if (key == Key.Down) { Guard(MoveSelectedTestDown); return true; }
        return false;
    }

    private void OnInlineTestEnableClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TestConfig tc)
        {
            tc.Enable = cb.IsChecked == true;
            if (ReferenceEquals(tc, _editing) && _testEnableCheck != null)
            {
                _testEnableCheck.IsChecked = tc.IsEnabled;
            }
            UpdateJsonProjection();
        }
    }

    public void MoveSelectedTestUp()
    {
        if (!_json.IsReadOnly) return;
        if (_testList.SelectedItem is not TestConfig selected) return;
        int index = _draft.testItems.InOut.Tests.IndexOf(selected);
        if (index <= 0) return;
        Flush();
        if (TestSequenceOrder.MoveTest(_draft.testItems.InOut.Tests, index, index - 1))
        {
            RefreshTests(selected);
            UpdateJsonProjection();
        }
    }

    public void MoveSelectedTestDown()
    {
        if (!_json.IsReadOnly) return;
        if (_testList.SelectedItem is not TestConfig selected) return;
        int index = _draft.testItems.InOut.Tests.IndexOf(selected);
        if (index < 0 || index >= _draft.testItems.InOut.Tests.Count - 1) return;
        Flush();
        if (TestSequenceOrder.MoveTest(_draft.testItems.InOut.Tests, index, index + 1))
        {
            RefreshTests(selected);
            UpdateJsonProjection();
        }
    }

    public void RenumberTests()
    {
        if (!_json.IsReadOnly) return;
        Flush();
        var current = _testList.SelectedItem as TestConfig;
        TestSequenceOrder.RenumberByPosition(_draft.testItems.InOut.Tests);
        RefreshTests(current);
        UpdateJsonProjection();
        _error.Foreground = Brushes.LightGray;
        _error.Text = "Đã đánh số lại các bài đo theo vị trí IO001, IO002... Giữ nguyên LINE CHUẨN gốc.";
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
    private ModelConfig ReadEditorModel()
    {
        if (_jsonPending) ApplyJson(); else Flush();
        return ParseModel(_name.Text, JsonSerializer.Serialize(_draft, JsonOptions));
    }
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 8) };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 7, 4), MinWidth = 80 }; button.Click += (_, _) => action(); return button;
    }
    private void ShowEditorError(Exception ex) { _error.Foreground = Brushes.Gold; _error.Text = ex.Message; }
    private void Guard(Action action) { try { action(); } catch (Exception ex) { ShowEditorError(ex); } }
    private void RefreshForm()
    {
        string? selectedId = _editing?.id;
        _commits.Clear(); _editing = null;
        RefreshTests(_draft.testItems.InOut.Tests.FirstOrDefault(t => t.id == selectedId));
        UpdateJsonProjection();
    }
    private void RefreshTests(TestConfig? selected = null)
    {
        _refreshing = true; _testList.ItemsSource = null; _testList.ItemsSource = _draft.testItems.InOut.Tests;
        _testList.SelectedItem = selected ?? _draft.testItems.InOut.Tests.FirstOrDefault(); _refreshing = false;
        BuildTest(_testList.SelectedItem as TestConfig);
    }
    private void CommitFields() { foreach (var action in _commits) action(); }
    private void Flush()
    {
        CommitFields(); _draft.model = _name.Text.Trim();
        var config = _draft.testItems.InOut;
        foreach (string key in config.Devices.Input.Keys.Where(k => !config.Tests.Any(t => t.PlaybackOut == k)).ToArray()) config.Devices.Input.Remove(key);
        foreach (string key in config.Devices.Output.Keys.Where(k => !config.Tests.Any(t => t.RecordingIn == k)).ToArray()) config.Devices.Output.Remove(key);
    }
    private void ApplyJson()
    {
        var parsed = ParseModel(_name.Text, _json.Text);
        _draft = parsed; RefreshForm(); _json.IsReadOnly=true; _formLayout.IsEnabled=true;_editJson.IsEnabled=true;
    }
    private void EnableJsonEditing()
    {Flush();UpdateJsonProjection();_json.IsReadOnly=false;_formLayout.IsEnabled=false;_editJson.IsEnabled=false;_json.Focus();}
    public void SelectTest(string id)
    {CommitFields();RefreshTests(_draft.testItems.InOut.Tests.FirstOrDefault(t=>t.id==id));UpdateJsonProjection();}
    private void UpdateJsonProjection()
    {
        _updatingJson = true;
        try { _json.Text = JsonSerializer.Serialize(_draft, JsonOptions); _jsonPending = false; }
        finally { _updatingJson = false; }
    }
    private void SynchronizeForm()
    {
        if (_buildingForm || _refreshing || _updatingJson) return;
        if(!_json.IsReadOnly)return;
        try {
            Flush();
            UpdateJsonProjection();
            if (_editing != null && _editing.IsRubBuzzEnabled) _editing.ValidateAdditionalTests();
            if (_testList?.ItemsSource != null) _testList.Items.Refresh();
            RefreshResponseLimitPreview();
            _error.Foreground = Brushes.LightGray;
            _error.Text = "JSON đã khớp với bài đo và tiêu chí. Bấm Gửi cấu hình thử để áp dụng trong phiên; kết quả được lưu vào dữ liệu thử.";
        }
        catch (Exception ex) { ShowEditorError(ex); }
    }
    private TextBox Field(string label, string? value, Action<string> set, string? hint = null, bool readOnly = false)
    {
        _fieldTarget.Children.Add(Label(label));
        var input = new TextBox { Text = value ?? "", ToolTip = hint ?? label, IsReadOnly = readOnly };
        if (readOnly) input.Background = new SolidColorBrush(Color.FromRgb(35, 35, 35));
        _fieldTarget.Children.Add(input); _commits.Add(() => set(input.Text.Trim()));
        input.TextChanged += (_, _) => SynchronizeForm();
        if (hint != null) _fieldTarget.Children.Add(Note(hint));
        return input;
    }
    private TextBox Number(string label, double? value, Action<double?> set, double min, double max, bool required = false, Func<bool>? active = null)
    {
        return Field(label, value?.ToString("R", CultureInfo.InvariantCulture), text =>
        {
            if (active?.Invoke() == false) return;
            if (text.Length == 0 && !required) { set(null); return; }
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n) || n < min || n > max)
                throw new InvalidDataException($"{label}: nhập số từ {min} đến {max}.");
            set(n);
        });
    }
    private void OptionalThdLimit(string name, string label, bool enabled, Action<bool> setEnabled, string limitLabel, double value, Action<double?> setLimit, string hint, double minimum = .001)
    {
        var parent = _fieldTarget;
        var check = new CheckBox { Name = name, Content = label, IsChecked = enabled, Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 2), FontSize = 14, ToolTip = hint };
        var details = new StackPanel { Name = name + "Limit", Margin = new Thickness(22, 0, 0, 0),
            Visibility = enabled ? Visibility.Visible : Visibility.Collapsed };
        parent.Children.Add(check); parent.Children.Add(details);
        _fieldTarget = details;
        Number(limitLabel, value, setLimit, minimum, 100, true, () => check.IsChecked == true);
        _fieldTarget = parent;
        void Update()
        {
            bool selected = check.IsChecked == true;
            setEnabled(selected); details.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            SynchronizeForm();
        }
        check.Checked += (_, _) => Update(); check.Unchecked += (_, _) => Update();
    }
    private CheckBox Check(string label, bool value, Action<bool> set, string hint)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 2), FontSize = 14, ToolTip = hint };
        check.Checked += (_, _) => { set(true); SynchronizeForm(); if (_testList?.ItemsSource != null) _testList.Items.Refresh(); };
        check.Unchecked += (_, _) => { set(false); SynchronizeForm(); if (_testList?.ItemsSource != null) _testList.Items.Refresh(); };
        _fieldTarget.Children.Add(check); _fieldTarget.Children.Add(Note(hint));
        return check;
    }
    private void Choice(string label, string[] choices, string selected, Action<string> set)
    {
        _fieldTarget.Children.Add(Label(label));
        var values = choices.Contains(selected) || selected.Length == 0 ? choices : choices.Append(selected).ToArray();
        var combo = new ComboBox { ItemsSource = values, SelectedItem = selected, ToolTip = label };
        _fieldTarget.Children.Add(combo); _commits.Add(() => set(combo.SelectedItem?.ToString() ?? ""));
        combo.SelectionChanged += (_, _) => SynchronizeForm();
    }
    private void BuildTest(TestConfig? test)
    {
        _buildingForm = true;
        _editing = test; _commits.Clear(); _fields.Children.Clear(); _fieldTarget = _fields;
        if (test == null) { _fieldTarget.Children.Add(Note("Bấm + Thêm bài để tạo bài đo.")); _buildingForm = false; return; }
        var sections = new TabControl(); _fields.Children.Add(sections);
        void Section(string title)
        {
            _fieldTarget = new StackPanel { Margin = new Thickness(10) };
            sections.Items.Add(new TabItem { Header = title, Content = _fieldTarget });
        }
        Section("Đường tín hiệu");
        _fieldTarget.Children.Add(Label("Thông tin & đường tín hiệu"));
        Field("Tên bài đo", test.name, v => test.name = v);
        var idBox = Field("Mã bài đo (không trùng)", test.id, v => test.id = v, readOnly: true);
        idBox.Name = "TestIdBox";
        if (!string.IsNullOrWhiteSpace(test.ReferenceTestId) && !string.Equals(test.ReferenceTestId.Trim(), test.id?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _fieldTarget.Children.Add(Note($"LINE CHUẨN gốc: {test.ReferenceTestId} (giữ nguyên dữ liệu chuẩn sau khi đổi thứ tự/mã bài)"));
        }
        void Integer(string label,int value,Action<int> set) => Number(label,value,v=>
        {if(v!.Value!=Math.Truncate(v.Value)) throw new InvalidDataException(label+": nhập số nguyên.");set((int)v.Value);},1,10,true);
        Integer("MaximumRuns — số lần chạy tối đa",test.MaximumRuns,v=>test.MaximumRuns=v);
        Integer("RequiredPasses — số lần cần đạt",test.RequiredPasses,v=>test.RequiredPasses=v);
        _fieldTarget.Children.Add(Note("Riêng bài đang chọn. Đủ lượt PASS thì dừng; mặc định 2/1: lượt 1 PASS thì bỏ lượt 2, lượt 1 FAIL thì đo lượt 2. INVALID không tính là PASS."));
        _testEnableCheck = Check("Chạy bài này trong Auto Test", test.IsEnabled, v => test.Enable = v, "Bỏ tích để tạm ngừng bài mà vẫn giữ thông số.");
        Check("Bài tùy chọn — không tính vào PASS/FAIL tổng", test.IsOptional, v => test.Optional = v == (test.SpeakerStage == "MicFeedbackGain") ? null : v,
            "Vẫn đo, hiện và gửi kết quả; bỏ qua được và không làm FAIL/INVALID cả phiên. Mặc định bật cho bài Gain trước hú (IO009).");
        _testEnableCheck.Name = "CheckRunInAutoTest";
        Choice("Chuỗi tự động", new[] { "Âm học thông thường", "Điện tử C360 (BLE)", "Điện tử Sonca (BLE)" },
            _draft.testItems.InOut.AutomationProfile == Acoustic3ModelDefinition.Profile ? "Điện tử Sonca (BLE)"
                : C360ModelDefinition.Matches(_draft.testItems.InOut) ? "Điện tử C360 (BLE)" : "Âm học thông thường",
            v => _draft.testItems.InOut.AutomationProfile = v switch { "Điện tử C360 (BLE)" => C360ModelDefinition.Profile,
                "Điện tử Sonca (BLE)" => Acoustic3ModelDefinition.Profile, _ => null });
        Choice("Ngõ loa điều khiển qua BLE", new[] { "Không dùng", "MIC IN · Echo / Reverb", "MIC IN · Di tần (FBX)", "MIC IN · Không effect", "BT", "LINE IN", "USB A", "Mic thực tế · LINE IN", "Mic thực tế · Rè (Rub & Buzz)", "MIC IN · Gain trước hú (FBX)" },
            test.SpeakerStage switch { "MicDry" => "MIC IN · Không effect", "Bluetooth" => "BT", "MicEffects" => "MIC IN · Echo / Reverb", "MicFrequencyShift" => "MIC IN · Di tần (FBX)", "LineIn" => "LINE IN", "UsbA" => "USB A", "AcousticMic" => "Mic thực tế · LINE IN", C360ModelDefinition.AcousticRubBuzzStage => "Mic thực tế · Rè (Rub & Buzz)", "MicFeedbackGain" => "MIC IN · Gain trước hú (FBX)", _ => "Không dùng" },
            v => test.SpeakerStage = v switch { "MIC IN · Không effect" => "MicDry", "BT" => "Bluetooth", "MIC IN · Echo / Reverb" => "MicEffects", "MIC IN · Di tần (FBX)" => "MicFrequencyShift", "LINE IN" => "LineIn", "USB A" => "UsbA", "Mic thực tế · LINE IN" => "AcousticMic", "Mic thực tế · Rè (Rub & Buzz)" => C360ModelDefinition.AcousticRubBuzzStage, "MIC IN · Gain trước hú (FBX)" => "MicFeedbackGain", _ => null });
        if (C360ModelDefinition.IsAcoustic(test))
        {
            Check("Đã đo chỉnh bằng mic thật — cho phép bật Auto Test", !test.AcousticSetupPending, v => test.AcousticSetupPending = !v,
                "Chưa xác nhận: lưu tạm cấu hình, chưa chạy tự động. Chọn đúng mic, calibration và mức phát/thu trước khi xác nhận.");
        }
        var devices = _draft.testItems.InOut.Devices;
        Field("Tên ngõ PHÁT → loa", test.PlaybackOut, v => test.PlaybackOut = v);
        _endpointCache = null;
        var playbackDevice = Field("Tên thiết bị Windows (phát)", devices.Input.GetValueOrDefault(test.PlaybackOut), v => devices.Input[test.PlaybackOut] = v,
            test.SpeakerStage == "Bluetooth" ? "Ngõ BT tự nhận đúng loa BLE theo địa chỉ phần cứng trên máy đang dùng. Tên này chỉ ưu tiên khi cùng loa có nhiều ngõ stereo; không cần sửa theo tên loa/máy cũ."
                : "Nhập tên nhận diện loa/soundcard, ví dụ Analog Connector 1/2. Áp dụng cho các bài cùng tên ngõ phát.");
        playbackDevice.Name = "PlaybackDeviceName";
        EndpointTools(playbackDevice, capture: false, test);
        Field("Tên ngõ THU ← micro", test.RecordingIn, v => test.RecordingIn = v);
        var recordingDevice = Field("Tên thiết bị Windows (thu)", devices.Output.GetValueOrDefault(test.RecordingIn), v => devices.Output[test.RecordingIn] = v, "Nhập tên nhận diện micro/soundcard, ví dụ FastTrack Pro. Kênh thu chọn ở dưới.");
        recordingDevice.Name = "RecordingDeviceName";
        EndpointTools(recordingDevice, capture: true, test);
        void Channel(string label, int? value, Action<int?> set) => Choice(label, new[] { "Cả hai / trộn", "1 — Left", "2 — Right" },
            value == null ? "Cả hai / trộn" : value == 1 ? "1 — Left" : value == 2 ? "2 — Right" : value.ToString()!,
            v => set(v == "Cả hai / trộn" ? null : int.Parse(v.Split(' ')[0], CultureInfo.InvariantCulture)));
        Channel("Kênh phát", test.PlaybackChannel, v => test.PlaybackChannel = v);
        Channel("Kênh thu", test.RecordingChannel, v => test.RecordingChannel = v);
        var calibrationLabel = Note(test.MicrophoneCalibration == null ? "Chưa đính calibration riêng cho bài đo." : $"Calibration: {test.MicrophoneCalibration.FileName} · SHA256 {test.MicrophoneCalibration.Sha256[..12]}");
        _fieldTarget.Children.Add(calibrationLabel);
        _fieldTarget.Children.Add(ActionButton("Chọn file calibration…", () => Guard(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Microphone calibration|*.txt;*.cal;*.csv|Tất cả|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            test.MicrophoneCalibration = TestMicrophoneCalibration.Import(dialog.FileName);
            calibrationLabel.Text = $"Calibration: {test.MicrophoneCalibration.FileName} · SHA256 {test.MicrophoneCalibration.Sha256[..12]}";
            UpdateJsonProjection();
        })));
        _fieldTarget.Children.Add(ActionButton("Bỏ calibration riêng", () => { test.MicrophoneCalibration = null; calibrationLabel.Text = "Chưa đính calibration riêng cho bài đo."; UpdateJsonProjection(); }));
        Choice("Phương pháp đáp tuyến", new[] { "LogSweep", "Multitone" }, test.FrequencyResponseMethod ?? "LogSweep", v => test.FrequencyResponseMethod = v);
        int samples = test.SweepLengthSamples ?? (test.LogSweepDurationSeconds is double duration ? (int)Math.Round(duration * 44100) : SweepLengthSettings.DefaultSamples);
        string selectedLength = SweepLengthSettings.SupportedSamples.Contains(samples) ? SweepLengthSettings.Label(samples) : $"Tùy chỉnh cũ ({test.LogSweepDurationSeconds:0.#####} s)";
        Choice("Sweep length", SweepLengthSettings.SupportedSamples.Select(SweepLengthSettings.Label).ToArray(), selectedLength,
            v => { if (v == selectedLength && !SweepLengthSettings.SupportedSamples.Contains(samples)
                    && test.SweepLengthSamples == null && test.LogSweepDurationSeconds != null) return;
                int index = Array.IndexOf(SweepLengthSettings.SupportedSamples.Select(SweepLengthSettings.Label).ToArray(), v);
                if (index >= 0) { test.SweepLengthSamples = SweepLengthSettings.SupportedSamples[index]; test.LogSweepDurationSeconds = null; } });
        _fieldTarget.Children.Add(Note("Mặc định 64K = 65.536 mẫu; thời lượng tự đổi theo sample rate (1,486 s ở 44,1 kHz). Multitone/FBX dùng thời lượng tone riêng. Sweep dài hơn chỉ dùng khi cần thêm chất lượng tín hiệu."));
        Number("Mức phát (dBFS peak, trống = mức hiện tại)", test.PlaybackLevelDbfs, v => test.PlaybackLevelDbfs = v, -60, 0);
        _fieldTarget.Children.Add(Note("Mức Windows và BLE dùng chung đặt ở tab Âm lượng model. Các mức Windows riêng từ cấu hình cũ được giữ trong JSON và chỉ dùng khi mức chung còn trống."));
        Number("Mức tín hiệu thu tối thiểu (dBFS)", test.MinimumInputSignalDbFs, v => test.MinimumInputSignalDbFs = v, -120, 0);
        Section("Bật/tắt kiểm tra");
        _fieldTarget.Children.Add(Label("Đáp tuyến & THD"));
        Field("freq_range (Hz, chỉ xét PASS/FAIL)", FormattableString.Invariant($"{test.EvaluationRange.MinimumHz:R}–{test.EvaluationRange.MaximumHz:R}"),
            v => test.FrequencyRange = ParseFrequencyRange(v));
        Number("THD_1000Hz — giới hạn tại 1 kHz (%)", test.Thd1000Hz, v => test.Thd1000Hz = v!.Value, .001, 100, true);
        OptionalThdLimit("Thd80", "THD_80 — xét thêm THD tại 80 Hz", test.EvaluateThd80, v => test.EvaluateThd80 = v,
            "Giới hạn THD 80 Hz (%)", test.BassThdLimit ?? test.Thd1000Hz, v => test.BassThdLimit = v,
            "Bật để nhập giới hạn và đưa 80 Hz vào PASS/FAIL; số đo không hợp lệ sẽ yêu cầu đo lại.");
        OptionalThdLimit("Thd4000", "THD_4000 — xét thêm THD tại 4 kHz", test.EvaluateThd4000, v => test.EvaluateThd4000 = v,
            "Giới hạn THD 4000 Hz (%)", test.TrebleThdLimit ?? test.Thd1000Hz, v => test.TrebleThdLimit = v,
            "Bật để nhập giới hạn và đưa 4000 Hz vào PASS/FAIL.");
        _fieldTarget.Children.Add(Note("Mặc định chỉ xét THD tại 1000 Hz. Hai cờ bổ sung không thay đổi dải vẽ 20–20000 Hz."));
        _fieldTarget.Children.Add(Label("Phép kiểm tra bổ sung → tự thêm PASS / FAIL và nguyên nhân"));
        if (C360ModelDefinition.Matches(_draft.testItems.InOut))
        {
            var bleCheck = Check("BLE check ở đầu", _draft.testItems.InOut.IsBleCheckAtStartEnabled,
                v => _draft.testItems.InOut.BleCheckAtStart = v,
                "Mặc định ON. Kiểm tra các ngõ BLE và hiện popup trước Auto Test. SYMPHONY dò đúng model có RSSI mạnh nhất. Tắt để bỏ bước kiểm tra đầu; từng bài vẫn phải xác nhận chuyển ngõ trước khi phát.");
            bleCheck.Name = "CheckBleAtStart";
            var blePrefix = Field("Tiền tố tên BLE (tự kết nối loa mạnh nhất)", _draft.testItems.InOut.BleNamePrefix,
                v => _draft.testItems.InOut.BleNamePrefix = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
                "Ví dụ KBEATBOX. Khi Auto Test bắt đầu, app quét BLE 4 s, chỉ lấy loa có tên bắt đầu bằng chuỗi này và kết nối loa có RSSI mạnh nhất. "
                + "Nếu model có FirmwareModelId (C360 = 1075), INFO phải đúng mã đó, không thì thử loa mạnh kế tiếp. Để trống = cách chọn loa như cũ.");
            blePrefix.Name = "TxtBleNamePrefix";
        }
        Check("Bluetooth auto pair — tự ghép loa trên máy đang dùng", _draft.testItems.InOut.BluetoothAutoPair,
            v => _draft.testItems.InOut.BluetoothAutoPair = v,
            "Bài BT tự tìm/ghép Bluetooth Windows với đúng địa chỉ loa BLE. Windows có thể yêu cầu xác nhận. Tắt mục này để chỉ dùng loa đã kết nối. Không lưu ID ngõ Windows vào model.");
        Check("Bluetooth auto remove — xóa ghép đôi sau Auto Test", _draft.testItems.InOut.BluetoothAutoRemove,
            v => _draft.testItems.InOut.BluetoothAutoRemove = v,
            "Dùng chung cho model đang sửa, mặc định tắt. Sau chuỗi C360 hoàn tất và khôi phục DSP, gỡ ghép đôi đúng loa vừa đo. Không gỡ khi hủy, đo riêng hoặc tìm LINE CHUẨN.");
        Check("TestEffect — kiểm tra echo / reverb", test.TestEffect, v => test.TestEffect = v, "Tự dùng log sweep, chấm theo kỳ vọng echo/reverb phía dưới (C360: không dùng LINE CHUẨN cho bài effect). Chưa đặt kỳ vọng thì mặc định yêu cầu echo.");
        Check("TestAirLeak — rò khí", test.TestAirLeak, v => test.TestAirLeak = v, "Tone bass riêng, tìm nhiễu cao tần điều biên. FAIL là dấu hiệu nghi rò khí, cần đối chiếu mẫu chuẩn.");
        Check("TestLooseParticle — hạt rác", test.TestLooseParticle, v => test.TestLooseParticle = v, "Tìm xung phi chu kỳ. Xung chưa đủ bằng chứng sẽ hiện INVALID, yêu cầu đo lại.");
        Check("TestSoundLevel — lệch mức âm so line chuẩn", test.TestSoundLevel, v => test.TestSoundLevel = v, "So mức thu tuyệt đối cùng route/gain. Thiếu line hoặc bản thu lỗi sẽ không PASS.");
        Number("Cho phép lệch mức âm ± (dB)", test.SoundLevelToleranceDb, v => test.SoundLevelToleranceDb = v!.Value, .01, 30, true);
        TextBox? rubBuzzRmsInput = null;
        bool newPinkRubBuzz = !test.IsRubBuzzEnabled && test.RubBuzzMode == null;
        var chkRubBuzz = Check("TestRubBuzz — rè/rung vỏ & mối ghép", test.IsRubBuzzEnabled,
            v => {
                if (v && newPinkRubBuzz) test.RubBuzzMode = "PinkNoiseChirp";
                test.TestRubBuzz = v ? true : (test.TestRubBuzz.HasValue ? false : null);
                if (v && !test.UsesAcousticRubBuzz && test.RubBuzzPeakLimitDb == null && test.RubBuzzRmsLimitPercent == null)
                {
                    test.RubBuzzRmsLimitPercent = 1.5;
                    if (rubBuzzRmsInput != null) rubBuzzRmsInput.Text = "1.5";
                }
            },
            "Pink noise để sàng lọc rè/rung; tone bước vẫn dùng được. Pink cần mic âm học và tham chiếu riêng, không dùng ngưỡng RMS % của sine.");
        chkRubBuzz.Name = "CheckTestRubBuzz";
        Section("Biên & khoảng xét hõm");
        BuildResponseLimits(test);
        Section("Tone cơ khí");
        _fieldTarget.Children.Add(Label("Thông số tone cho rò khí / hạt rác"));
        Number("Tần số tone (Hz)", test.MechanicalFrequencyHz, v => test.MechanicalFrequencyHz = v!.Value, 20, 200, true);
        Number("Mức tone (dBFS peak)", test.MechanicalLevelDbFs, v => test.MechanicalLevelDbFs = v!.Value, -60, -3, true);
        Number("Thời lượng tone (giây)", test.MechanicalDurationSeconds, v => test.MechanicalDurationSeconds = v!.Value, 1, 5, true);
        Number("Ngưỡng xung trên nền (dB)", test.MechanicalPeakMarginDb, v => test.MechanicalPeakMarginDb = v!.Value, 6, 30, true);
        Section("Rè (Rub & Buzz)");
        var rubBuzzSection = _fieldTarget;
        // Index ↔ mode: 0 Pink+Chirp (recommended), 1 Pink, 2 Chirp, 3 legacy stepped sine.
        string[] modes = { "PinkNoiseChirp", "PinkNoise", "Chirp", "SteppedSine" };
        int modeIndex = newPinkRubBuzz ? 0 : Math.Max(0, Array.IndexOf(modes, test.RubBuzzMode ?? "SteppedSine"));
        var mode = new ComboBox { Name = "ComboRubBuzzMode", ItemsSource = new[] { "Pink + Chirp · v1.4 (khuyến nghị)", "Pink noise · v1.3 (rè lặp lại)",
            "Chirp · v1.4 (tần số rè)", "Sine bước · tương thích cũ" }, SelectedIndex = modeIndex, Margin = new Thickness(0, 4, 0, 8) };
        rubBuzzSection.Children.Add(Label("Tín hiệu phát hiện rè/rung")); rubBuzzSection.Children.Add(mode);
        var pinkFields = new StackPanel(); rubBuzzSection.Children.Add(pinkFields); _fieldTarget = pinkFields;
        bool rubBuzzModeChanged = false;
        _commits.Add(() => { if (test.IsRubBuzzEnabled || test.RubBuzzMode != null || rubBuzzModeChanged) test.RubBuzzMode = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)]; });
        _fieldTarget.Children.Add(Note("Pink 25–150 Hz cùng một dạng sóng phát lặp N lượt, thu WASAPI Exclusive. Chỉ số: tỷ lệ rè lặp lại (cao tần 4–12 kHz lặp lại theo bass). "
            + "C360 IO008: chỉ ngưỡng tuyệt đối, PASS khi rè lặp lại < ngưỡng FAIL (30 %), không dùng LINE CHUẨN. Model khác: so với LINE CHUẨN RÈ nếu có, chưa có thì ngưỡng tuyệt đối. Kết quả ở Ctrl+Shift+I → Rè (Rub & Buzz)."));
        Number("Mức pink (dBFS RMS)", test.PinkSettings.LevelRmsDbFs, v => { if (test.UsesPinkRubBuzz) test.RubBuzzPink = test.PinkSettings with { LevelRmsDbFs = v!.Value }; }, -60, PinkRubBuzzSettings.MaxLevelRmsDbFs, true).Name = "TxtPinkRubBuzzLevel";
        Number("Thời lượng mỗi lượt pink (giây)", test.PinkSettings.DurationSeconds, v => { if (test.UsesPinkRubBuzz) test.RubBuzzPink = test.PinkSettings with { DurationSeconds = v!.Value }; }, 4, 15, true).Name = "TxtPinkRubBuzzSeconds";
        Number("Số lượt lặp cùng dạng sóng", test.PinkSettings.Repeats, v => { if (test.UsesPinkRubBuzz) test.RubBuzzPink = test.PinkSettings with { Repeats = (int)Math.Round(v!.Value) }; }, 2, 5, true).Name = "TxtPinkRubBuzzRepeats";
        var pinkLimits = test.PinkLimits;
        void Limit(string label, double value, Func<PinkRubBuzzLimits, double, PinkRubBuzzLimits> apply, double min, double max, string name)
            => Number(label, value, v => { if (test.UsesPinkRubBuzz) test.RubBuzzPinkLimits = apply(test.PinkLimits, v!.Value); }, min, max, true).Name = name;
        Limit("Tỷ lệ rè lặp lại cho phép so với line chuẩn (+ điểm %)", pinkLimits.RepeatMarginPct, (l, v) => l with { RepeatMarginPct = v }, 1, 100, "TxtPinkRepeatMargin");
        Limit("Năng lượng cao tần 4–12 kHz cho phép so với line chuẩn (+ dB)", pinkLimits.HfMarginDb, (l, v) => l with { HfMarginDb = v }, .5, 30, "TxtPinkHfMargin");
        Limit("Chưa có line chuẩn: PASS khi rè lặp lại ≤ (%)", pinkLimits.AbsolutePassPct, (l, v) => l with { AbsolutePassPct = v }, 0, 100, "TxtPinkAbsolutePass");
        Limit("Chưa có line chuẩn: FAIL khi rè lặp lại ≥ (%)", pinkLimits.AbsoluteFailPct, (l, v) => l with { AbsoluteFailPct = v }, 0, 100, "TxtPinkAbsoluteFail");
        Limit("Vùng cần duyệt sát giới hạn (0 = chỉ PASS/FAIL; 0,2 = 20 %)", pinkLimits.ReviewFraction, (l, v) => l with { ReviewFraction = v }, 0, .9, "TxtPinkReviewFraction");
        Limit("Độ nhạy tối thiểu: bass tại mic − nền cao tần (dB)", pinkLimits.MinSensitivityDb, (l, v) => l with { MinSensitivityDb = v }, 20, 90, "TxtPinkMinSensitivity");
        var chirpFields = new StackPanel(); rubBuzzSection.Children.Add(chirpFields); _fieldTarget = chirpFields;
        _fieldTarget.Children.Add(Note("Chirp (sine quét) bass–trung phát lặp N lượt, thu WASAPI Exclusive. Chỉ số: đỉnh rè = méo trên hài bậc 10 nhô lên khỏi xu hướng trơn "
            + "tại một tần số, lặp lại ở mọi lượt → cho biết rè ở TẦN SỐ nào. C360 IO008: PASS khi đỉnh rè < ngưỡng FAIL, không dùng LINE CHUẨN; model khác so với LINE CHUẨN CHIRP nếu có. Kết quả ở Ctrl+Shift+I → Rè (Rub & Buzz)."));
        void ChirpSetting(string label, double value, Func<ChirpRubBuzzSettings, double, ChirpRubBuzzSettings> apply, double min, double max, string name)
            => Number(label, value, v => { if (test.UsesChirpRubBuzz) test.RubBuzzChirp = apply(test.ChirpSettings, v!.Value); }, min, max, true).Name = name;
        var chirp = test.ChirpSettings;
        ChirpSetting("Tần số bắt đầu chirp (Hz)", chirp.F1Hz, (s, v) => s with { F1Hz = v }, 10, 100, "TxtChirpF1");
        ChirpSetting("Tần số kết thúc chirp (Hz)", chirp.F2Hz, (s, v) => s with { F2Hz = v }, 500, 5000, "TxtChirpF2");
        ChirpSetting("Thời lượng mỗi lượt chirp (giây)", chirp.DurationSeconds, (s, v) => s with { DurationSeconds = v }, 1, 15, "TxtChirpSeconds");
        ChirpSetting("Mức chirp (dBFS đỉnh)", chirp.PeakDbFs, (s, v) => s with { PeakDbFs = v }, -40, -6, "TxtChirpPeak");
        ChirpSetting("Số lượt chirp", chirp.Repeats, (s, v) => s with { Repeats = (int)Math.Round(v) }, 2, 5, "TxtChirpRepeats");
        void ChirpLimit(string label, double value, Func<ChirpRubBuzzLimits, double, ChirpRubBuzzLimits> apply, double min, double max, string name)
            => Number(label, value, v => { if (test.UsesChirpRubBuzz) test.RubBuzzChirpLimits = apply(test.ChirpLimits, v!.Value); }, min, max, true).Name = name;
        var chirpLimits = test.ChirpLimits;
        ChirpLimit("Đỉnh rè cho phép cao hơn line chuẩn (+ dB)", chirpLimits.BumpMarginDb, (l, v) => l with { BumpMarginDb = v }, 1, 40, "TxtChirpBumpMargin");
        ChirpLimit("Chưa có line chuẩn: PASS khi đỉnh rè ≤ (dB)", chirpLimits.AbsolutePassDb, (l, v) => l with { AbsolutePassDb = v }, 0, 40, "TxtChirpAbsolutePass");
        ChirpLimit("Chưa có line chuẩn: FAIL khi đỉnh rè ≥ (dB)", chirpLimits.AbsoluteFailDb, (l, v) => l with { AbsoluteFailDb = v }, 0, 60, "TxtChirpAbsoluteFail");
        ChirpLimit("Vùng cần duyệt sát giới hạn (0 = chỉ PASS/FAIL)", chirpLimits.ReviewFraction, (l, v) => l with { ReviewFraction = v }, 0, .9, "TxtChirpReviewFraction");
        var sineFields = new StackPanel(); rubBuzzSection.Children.Add(sineFields); _fieldTarget = sineFields;
        void ShowRubBuzzMode()
        {
            string selected = modes[Math.Clamp(mode.SelectedIndex, 0, modes.Length - 1)];
            pinkFields.Visibility = selected is "PinkNoise" or "PinkNoiseChirp" ? Visibility.Visible : Visibility.Collapsed;
            chirpFields.Visibility = selected is "Chirp" or "PinkNoiseChirp" ? Visibility.Visible : Visibility.Collapsed;
            sineFields.Visibility = selected == "SteppedSine" ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => { rubBuzzModeChanged = true; ShowRubBuzzMode(); SynchronizeForm(); }; ShowRubBuzzMode();
        _fieldTarget.Children.Add(Note("Tần số mặc định (50, 70, 100, 150 Hz) dành cho woofer. Muốn thử tweeter thì chọn tần số quanh Fs của tweeter và phải có tụ bảo vệ. Cảnh báo: mức phát quá cao có thể làm hỏng loa."));
        string rubBuzzFrequencies = string.Join(", ", test.EffectiveRubBuzzFrequenciesHz.Select(f => f.ToString("0.##", CultureInfo.InvariantCulture)));
        Field("Danh sách tần số (Hz, 2–8 giá trị, cách nhau bởi dấu phẩy)", rubBuzzFrequencies, text =>
        {
            if (!test.IsRubBuzzEnabled && test.RubBuzzFrequenciesHz == null && text == rubBuzzFrequencies) return;
            var entries = text.Split(',');
            var values = new List<double>();
            foreach (string entry in entries)
            {
                if (!double.TryParse(entry.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f) || !double.IsFinite(f))
                    throw new InvalidDataException("Rub & Buzz: nhập danh sách tần số bằng số, cách nhau bởi dấu phẩy.");
                values.Add(f);
            }
            test.RubBuzzFrequenciesHz = values.ToArray();
        }).Name = "TxtRubBuzzFrequencies";
        Number("Mức phát (dBFS peak, trống = theo mức phát bài / -18 dBFS)", test.RubBuzzLevelDbFs, v => test.RubBuzzLevelDbFs = v, -60, -3).Name = "TxtRubBuzzLevel";
        Number("Thời gian mỗi bước (giây, 0,6–3 s)", test.RubBuzzStepSeconds, v => test.RubBuzzStepSeconds = v, .6, 3).Name = "TxtRubBuzzStepSeconds";
        _fieldTarget.Children.Add(Note("Mặc định 1,0 s. Mỗi tần số f cần (StepSeconds − 0,35) × f ≥ 24 chu kỳ ổn định."));
        Number("Giới hạn đỉnh dư (dB so với đỉnh tone, trống = chỉ ghi nhận)", test.RubBuzzPeakLimitDb, v => test.RubBuzzPeakLimitDb = v, -80, 0).Name = "TxtRubBuzzPeakLimit";
        rubBuzzRmsInput = Number("Giới hạn RMS dư (%, trống = không chặn theo RMS)", test.RubBuzzRmsLimitPercent, v => test.RubBuzzRmsLimitPercent = v, .01, 100);
        rubBuzzRmsInput.Name = "TxtRubBuzzRmsLimit";
        Section("Echo / Reverb");
        _fieldTarget.Children.Add(Label("Kỳ vọng effect"));
        var effect = test.ImpulseEffects ?? new ImpulseEffectSettings();
        static string Expect(bool? v) => v.HasValue ? v.Value ? "Phải có" : "Không được có" : "Theo line / mặc định";
        static bool? ParseExpect(string v) => v == "Phải có" ? true : v == "Không được có" ? false : null;
        var choices = new[] { "Theo line / mặc định", "Phải có", "Không được có" };
        Choice("Echo", choices, Expect(effect.ExpectEcho), v => effect = effect with { ExpectEcho = ParseExpect(v) });
        Choice("Reverb", choices, Expect(effect.ExpectReverb), v => effect = effect with { ExpectReverb = ParseExpect(v) });
        Number("Delay echo chuẩn (ms, trống = theo line)", effect.ExpectedEchoDelayMs, v => effect = effect with { ExpectedEchoDelayMs = v }, 10, 2000);
        Number("Cho phép lệch delay ± (ms)", effect.EchoDelayToleranceMs, v => effect = effect with { EchoDelayToleranceMs = v!.Value }, .1, 1000, true);
        Number("Thu thêm đuôi sau sweep (giây)", effect.CaptureTailSeconds, v => effect = effect with { CaptureTailSeconds = v!.Value }, 1, 10, true);
        _commits.Add(() => { if (test.TestEffect || test.ImpulseEffects != null) test.ImpulseEffects = effect; });
        Section("Di tần MIC / FBX");
        var shift = test.FrequencyShift ?? new FrequencyShiftSettings();
        _fieldTarget.Children.Add(Note("Bài MIC IN · Di tần phát riêng ba tone 700 / 1700 / 3700 Hz khi FBX tắt và bật; không suy ra gain chống hú dB. Ngõ thu giữ nguyên " + C360ModelDefinition.CaptureNameFor(_draft.testItems.InOut) + "."));
        Number("Thời lượng multitone mỗi trạng thái FBX (giây)", shift.DurationSeconds, v => shift = shift with { DurationSeconds = v!.Value }, 2, 5, true);
        Number("Ngưỡng xác định di tần (Hz)", shift.DetectionThresholdHz, v => shift = shift with { DetectionThresholdHz = v!.Value }, .3, 15, true);
        Choice("Kỳ vọng di tần trong cả đường MIC", new[] { "Chỉ ghi nhận có / chưa thấy", "Phải có", "Không được có" },
            shift.ExpectedDetected == true ? "Phải có" : shift.ExpectedDetected == false ? "Không được có" : "Chỉ ghi nhận có / chưa thấy",
            v => shift = shift with { ExpectedDetected = v == "Phải có" ? true : v == "Không được có" ? false : null });
        _commits.Add(() => { if (test.SpeakerStage == "MicFrequencyShift" || test.FrequencyShift != null) test.FrequencyShift = shift; });
        Choice("Kỳ vọng thay đổi khi bật FBX", new[] { "Chỉ ghi nhận", "Phải thay đổi", "Không được thay đổi" },
            shift.ExpectedFbxChange == true ? "Phải thay đổi" : shift.ExpectedFbxChange == false ? "Không được thay đổi" : "Chỉ ghi nhận",
            v => shift = shift with { ExpectedFbxChange = v == "Phải thay đổi" ? true : v == "Không được thay đổi" ? false : null });
        if (test.SpeakerStage == "MicFeedbackGain" || test.FeedbackGain != null)
        {
            Section("Gain trước hú (FBX)");
            var gain = test.FeedbackGain ?? new FeedbackGainSettings();
            _fieldTarget.Children.Add(Note("Vòng mic đo → MIC IN → loa → mic đo, tăng gain tới khi hú, FBX tắt rồi bật. FAIL khi FBX thêm được ít hơn ngưỡng; FBX tắt mà chưa hú thì INVALID."));
            Number("FBX thêm tối thiểu (dB, trống = chỉ báo số)", gain.MinimumAddedDb, v => gain = gain with { MinimumAddedDb = v }, 0, 40);
            _commits.Add(() => test.FeedbackGain = gain);
        }
        Section("Âm lượng model");
        var volume = _draft.testItems.InOut.TestVolume ??= new ModelTestVolume();
        _fieldTarget.Children.Add(Note("Dùng chung cho mọi bài của model, cả Auto Test và tìm LINE CHUẨN. BLE chỉ đọc mức thực tế: chưa khớp sẽ yêu cầu chỉnh trước khi phát. Để trống nếu chưa quy định."));
        Number("BLE MASTER DSP cố định (%)", volume.BleMasterPercent, v => volume.BleMasterPercent = v, 1, 100);
        Number("BLE MUSIC cố định (%)", volume.BleMusicPercent, v => volume.BleMusicPercent = v, 1, 100);
        Number("BLE MIC cố định (%)", volume.BleMicPercent, v => volume.BleMicPercent = v, 1, 100);
        Number("Windows phát chung model (%)", volume.WindowsPlaybackPercent, v => volume.WindowsPlaybackPercent = v, 1, 100);
        Number("Windows thu chung model (%)", volume.WindowsRecordingPercent, v => volume.WindowsRecordingPercent = v, 1, 100);
        _fieldTarget.Children.Add(Note("Windows tự áp dụng, đọc lại và khôi phục khi kết thúc. Mức chung ưu tiên hơn Windows Levels riêng của từng bài. MASTER DSP, MUSIC, MIC và mức Windows là các gain riêng biệt."));
        _buildingForm = false;
    }
    private IReadOnlyList<WindowsEndpointName>? _endpointCache;
    private IReadOnlyList<WindowsEndpointName>? Endpoints(bool refresh = false)
    {
        if (WindowsEndpoints == null) return null;
        if (refresh || _endpointCache == null)
        {
            try { _endpointCache = WindowsEndpoints(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { _endpointCache = null; }
        }
        return _endpointCache;
    }

    /// <summary>"Take the endpoint selected in CẤU HÌNH ĐƯỜNG ÂM THANH" plus a live check of the typed name against Windows.</summary>
    private void EndpointTools(TextBox input, bool capture, TestConfig test)
    {
        bool electronic = C360ModelDefinition.Matches(_draft.testItems.InOut);
        string side = capture ? "THU" : "PHÁT";
        var status = new TextBlock { Name = capture ? "RecordingEndpointStatus" : "PlaybackEndpointStatus", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8), Foreground = Brushes.LightGray };
        void Refresh(bool reload = false)
        {
            if (!capture && electronic && test.SpeakerStage is "Bluetooth" or "UsbA")
            {
                status.Text = test.SpeakerStage == "Bluetooth" ? "Ngõ BT tự nhận theo địa chỉ loa BLE; không cần khớp tên khi đo."
                    : "Ngõ USB A chỉ xuất hiện sau khi loa chuyển nguồn USB qua BLE.";
                status.Foreground = Brushes.LightGray; return;
            }
            var endpoints = Endpoints(reload);
            if (endpoints == null) { status.Text = WindowsEndpoints == null ? "" : "Không đọc được danh sách thiết bị Windows."; return; }
            if (ModelEndpointCheck.Problem(input.Text, capture, endpoints, electronic, out var suggestions) is string problem)
            {
                status.Text = "⚠ " + problem + (suggestions.Count > 0 ? "\nWindows đang có: " + string.Join(" | ", suggestions) : "");
                status.Foreground = Brushes.Gold;
            }
            else
            {
                status.Text = "✓ Khớp thiết bị Windows: " + ModelEndpointCheck.Matches(input.Text, endpoints.Where(e => e.Capture == capture), electronic)[0].Name;
                status.Foreground = Brushes.MediumSpringGreen;
            }
        }
        var take = ActionButton($"↧ Lấy ngõ {side} đang chọn ở Cấu hình đường âm thanh", () => Guard(() =>
        {
            if (!_json.IsReadOnly) return;
            var selected = RoutingEndpointNames?.Invoke() ?? throw new InvalidOperationException("Chưa nối màn hình Cấu hình đường âm thanh.");
            string name = (capture ? selected.Recording : selected.Playback) is { Length: > 0 } chosen ? chosen
                : throw new InvalidOperationException($"Chưa chọn thiết bị {side.ToLowerInvariant()} ở Cấu hình đường âm thanh (màn hình chính).");
            input.Text = name; Refresh(reload: true);
            _error.Foreground = Brushes.LightGray;
            _error.Text = $"Đã lấy ngõ {side.ToLowerInvariant()} \"{name}\" cho {(capture ? test.RecordingIn : test.PlaybackOut)}; áp dụng cho mọi bài cùng tên ngõ.";
        }));
        take.Name = capture ? "UseRoutingRecording" : "UseRoutingPlayback";
        take.HorizontalAlignment = HorizontalAlignment.Left;
        take.ToolTip = $"Điền tên thiết bị {side.ToLowerInvariant()} đang chọn ở CẤU HÌNH ĐƯỜNG ÂM THANH vào cấu hình model.";
        var check = ActionButton("Kiểm tra lại", () => Refresh(reload: true));
        check.Name = capture ? "RecheckRecording" : "RecheckPlayback";
        var row = new WrapPanel(); row.Children.Add(take); row.Children.Add(check);
        _fieldTarget.Children.Add(row); _fieldTarget.Children.Add(status);
        input.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    private static double[] ParseFrequencyRange(string text)
    {
        double[]? values;
        try
        {
            if (text.StartsWith('[')) values = JsonSerializer.Deserialize<double[]>(text);
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(text,
                    @"^\s*(\d+(?:\.\d+)?)\s*(?:[-–—,;]|\s+)\s*(\d+(?:\.\d+)?)\s*(?:Hz)?\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success) throw new FormatException();
                values = new[] { double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) };
            }
            if (values is not { Length: 2 }) throw new FormatException();
            new FrequencyEvaluationRange(values[0], values[1]).Validate();
            return values;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or OverflowException)
        { throw new InvalidDataException("freq_range: nhập 20 ≤ từ < đến ≤ 20000 Hz, ví dụ 50–18000 hoặc [50, 18000]."); }
    }
}
