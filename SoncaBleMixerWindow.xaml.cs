using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoncaAudioInspector;

/// <summary>
/// Bàn điều khiển phần cứng loa Sonca Mixer Studio Console (tập trung 1 cửa sổ theo phong cách ứng dụng Sonca Mixer di động).
/// Mở bằng tổ hợp phím Ctrl + Shift + H.
/// </summary>
public partial class SoncaBleMixerWindow : Window
{
    private readonly SoncaBleManager _ble = SoncaBleManager.Instance;
    private readonly List<BleDeviceInfo> _deviceList = new();
    private readonly DispatcherTimer _sliderDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(75)
    };
    private readonly Dictionary<string, (byte channel, ushort reg, ushort val, string name)> _pendingCommands = new();
    private bool _suppressSliderEvents = true; // Chặn các sự kiện slider nạp ban đầu từ XAML
    private string _targetModelKeyword = "";

    // Channel mute tracking
    private readonly Dictionary<string, bool> _channelMuted = new()
    {
        ["Mic"] = false,
        ["Echo"] = false,
        ["Reverb"] = false,
        ["Music"] = false,
        ["Master"] = false,
        ["Live"] = false
    };

    public SoncaBleMixerWindow(string? modelKeyword = null)
    {
        _suppressSliderEvents = true;
        _targetModelKeyword = modelKeyword?.Trim() ?? "";

        _sliderDebounceTimer.Tick += OnSliderDebounceTick;

        InitializeComponent();

        Loaded += SoncaBleMixerWindow_Loaded;
        Closing += SoncaBleMixerWindow_Closing;
        PreviewKeyDown += SoncaBleMixerWindow_PreviewKeyDown;
    }

    public void SetTargetModel(string? modelKeyword)
    {
        _targetModelKeyword = modelKeyword?.Trim() ?? "";
        if (!string.IsNullOrEmpty(_targetModelKeyword))
        {
            TxtTargetModelIndicator.Text = $"Model mục tiêu: {_targetModelKeyword}";
        }
        UpdateDeviceCombo();
    }

    private void SoncaBleMixerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ble.DeviceDiscovered += OnDeviceDiscovered;
        _ble.ConnectionStatusChanged += OnConnectionStatusChanged;
        _ble.LogMessage += AppendLog;

        UpdateConnectionUi(_ble.IsConnected, _ble.IsConnected ? $"Đã kết nối: {_ble.ConnectedDeviceName}" : "Chưa kết nối");

        // Cho phép nhận sự kiện slider sau khi UI đã nạp đầy đủ
        _suppressSliderEvents = false;

        // Tự động quét tìm loa khi mở cửa sổ
        try
        {
            _ble.StartScanning();
            BtnScan.Content = "⏹ Dừng";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
        }
        catch (Exception ex)
        {
            AppendLog($"Lỗi khởi động quét BLE: {ex.Message}");
        }
    }

    private void SoncaBleMixerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ble.DeviceDiscovered -= OnDeviceDiscovered;
        _ble.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _ble.LogMessage -= AppendLog;
        _sliderDebounceTimer.Stop();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #region Fader Sliders & Parameter Debouncing
    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;

        // CHÍNH SÁCH BẢO VỆ PHẦN CỨNG:
        // Chỉ cho phép tăng/giảm âm lượng của MIC trên app Sonca Mixer.
        // Tất cả các kênh khác (ECHO, REVERB, MUSIC, MASTER, LIVE) chỉ được bật/tắt tiếng,
        // nếu muốn tăng/giảm âm lượng bắt buộc phải chỉnh trực tiếp nút vặn trên loa!
        if (sender == SliderMicVol)
        {
            TxtMicVal.Text = SliderMicVol.Value.ToString("F0");
            QueueCommand("MicVol", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMasterVol, (ushort)SliderMicVol.Value, "Mic Vol");
        }
    }

    private void QueueCommand(string key, byte channel, ushort reg, ushort val, string name)
    {
        lock (_pendingCommands)
        {
            _pendingCommands[key] = (channel, reg, val, name);
        }
        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Start();
    }

    private async void OnSliderDebounceTick(object? sender, EventArgs e)
    {
        _sliderDebounceTimer.Stop();
        if (!_ble.IsConnected) return;

        List<(byte channel, ushort reg, ushort val, string name)> toSend;
        lock (_pendingCommands)
        {
            toSend = _pendingCommands.Values.ToList();
            _pendingCommands.Clear();
        }

        foreach (var cmd in toSend)
        {
            byte[] packet = SoncaBleProtocol.BuildParamPacket(cmd.channel, cmd.reg, cmd.val);
            await _ble.SendPacketAsync(packet, $"{cmd.name}: {cmd.val}");
            await System.Threading.Tasks.Task.Delay(15);
        }
    }
    #endregion

    #region Channel Mute Buttons
    private async void BtnMuteChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string channelTag) return;

        bool currentMute = _channelMuted.TryGetValue(channelTag, out bool isMuted) && isMuted;
        bool newMute = !currentMute;
        _channelMuted[channelTag] = newMute;

        // Cập nhật biểu tượng và màu sắc nút
        btn.Content = newMute ? "🔇" : "🔊";
        btn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(newMute ? "#EF4444" : "#10B981"));

        if (!_ble.IsConnected) return;

        byte[] packet;
        switch (channelTag)
        {
            case "Mic":
                packet = SoncaBleProtocol.BuildMutePacket(SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMute, newMute);
                await _ble.SendPacketAsync(packet, $"Mic Mute: {newMute}");
                break;
            case "Music":
                packet = SoncaBleProtocol.BuildMutePacket(SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicMute, newMute);
                await _ble.SendPacketAsync(packet, $"Music Mute: {newMute}");
                break;
            case "Master":
                packet = SoncaBleProtocol.BuildMutePacket(SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMasterMute, newMute);
                await _ble.SendPacketAsync(packet, $"Master Mute: {newMute}");
                break;
            case "Echo":
                packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelEffect, SoncaBleProtocol.RegEchoLevel, (ushort)(newMute ? 0 : SliderEcho.Value));
                await _ble.SendPacketAsync(packet, $"Echo Mute: {newMute}");
                break;
            case "Reverb":
                packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelEffect, SoncaBleProtocol.RegReverbLevel, (ushort)(newMute ? 0 : SliderReverb.Value));
                await _ble.SendPacketAsync(packet, $"Reverb Mute: {newMute}");
                break;
            case "Live":
                packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegLiveVol, (ushort)(newMute ? 0 : SliderLiveVol.Value));
                await _ble.SendPacketAsync(packet, $"Live Mute: {newMute}");
                break;
        }
    }
    #endregion

    #region Input Sources & Toggles
    private async void RadioInput_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tagStr || !byte.TryParse(tagStr, out byte sourceIdx))
            return;

        if (_ble.IsConnected)
        {
            byte[] packet = SoncaBleProtocol.BuildInputSourcePacket(sourceIdx);
            string sourceName = rb.Content?.ToString() ?? $"Nguồn {sourceIdx}";
            await _ble.SendPacketAsync(packet, $"Chuyển ngõ vào: {sourceName}");
        }
    }

    private async void ChkFbx_Click(object sender, RoutedEventArgs e)
    {
        bool isFbx = ChkFbx.IsChecked == true;
        byte level = (byte)(isFbx ? 2 : 0); // 2: Cấp 2 tiêu chuẩn khi bật, 0: tắt
        if (_ble.IsConnected)
        {
            byte[] packet = SoncaBleProtocol.BuildFbxPacket(level);
            await _ble.SendPacketAsync(packet, $"Mic FBX chống hú: {(isFbx ? "BẬT (Cấp 2)" : "TẮT")}");
        }
    }

    private async void ChkBassBoost_Click(object sender, RoutedEventArgs e)
    {
        bool isBoost = ChkBassBoost.IsChecked == true;
        if (_ble.IsConnected)
        {
            byte[] packet = SoncaBleProtocol.BuildBassBoostPacket(isBoost);
            await _ble.SendPacketAsync(packet, $"Bass Boost: {(isBoost ? "BẬT" : "TẮT")}");
        }
    }

    private async void ChkEnhancer_Click(object sender, RoutedEventArgs e)
    {
        bool isEnhancer = ChkEnhancer.IsChecked == true;
        if (_ble.IsConnected)
        {
            byte[] packet = SoncaBleProtocol.BuildEnhancerPacket(isEnhancer);
            await _ble.SendPacketAsync(packet, $"Enhancer nâng tiếng: {(isEnhancer ? "BẬT" : "TẮT")}");
        }
    }
    #endregion

    #region Presets
    private void BtnPreset_Click(object sender, RoutedEventArgs e)
    {
        if (BtnPreset.ContextMenu != null)
        {
            BtnPreset.ContextMenu.PlacementTarget = BtnPreset;
            BtnPreset.ContextMenu.IsOpen = true;
        }
    }

    private void MenuPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string presetTag) return;

        BtnPreset.Content = $"{mi.Header.ToString()?.ToUpperInvariant()} ▾";

        _suppressSliderEvents = true;
        switch (presetTag)
        {
            case "Acoustic":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = true;
                break;
            case "Karaoke":
                SliderMicVol.Value = 60;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = true;
                break;
            case "Stage":
                SliderMicVol.Value = 65;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = false;
                break;
            case "Dance":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = true;
                ChkEnhancer.IsChecked = true;
                break;
            case "Bolero":
                SliderMicVol.Value = 65;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = false;
                break;
            case "Pop":
                SliderMicVol.Value = 55;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = true;
                break;
            case "Flat":
                SliderMicVol.Value = 50;
                ChkBassBoost.IsChecked = false;
                ChkEnhancer.IsChecked = false;
                break;
        }

        // Cập nhật text hiển thị MIC
        TxtMicVal.Text = SliderMicVol.Value.ToString("F0");

        _suppressSliderEvents = false;

        // Chỉ gửi âm lượng MIC và các toggle (các kênh khác bắt buộc vặn trực tiếp nút cơ trên loa)
        QueueCommand("MicVol", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMasterVol, (ushort)SliderMicVol.Value, "Mic Vol");
        if (_ble.IsConnected)
        {
            _ = _ble.SendPacketAsync(SoncaBleProtocol.BuildBassBoostPacket(ChkBassBoost.IsChecked == true), "Bass Boost");
            _ = _ble.SendPacketAsync(SoncaBleProtocol.BuildEnhancerPacket(ChkEnhancer.IsChecked == true), "Enhancer");
        }
    }
    #endregion

    #region Settings Drawer & Bluetooth Management
    private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        PanelSettings.Visibility = PanelSettings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        if (_ble.IsScanning)
        {
            _ble.StopScanning();
            BtnScan.Content = "🔍 Quét";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
        }
        else
        {
            _ble.StartScanning();
            BtnScan.Content = "⏹ Dừng";
            BtnScan.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
        }
    }

    private void OnDeviceDiscovered(BleDeviceInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            int index = _deviceList.FindIndex(d => d.Address == info.Address);
            if (index >= 0)
            {
                _deviceList[index] = info;
            }
            else
            {
                _deviceList.Add(info);
            }
            UpdateDeviceCombo();
        });
    }

    private void UpdateDeviceCombo()
    {
        object? currentSelected = CmbDevices.SelectedItem;
        CmbDevices.Items.Clear();

        ComboBoxItem? selectedItem = null;

        foreach (var dev in _deviceList.OrderByDescending(d => d.Rssi))
        {
            bool isSonca = SoncaBleProtocol.IsLikelySoncaDevice(dev.Name);
            string display = $"{dev.Name} ({dev.Rssi} dBm) [{(isSonca ? "Sonca/Acnos" : "BLE")}]";
            var item = new ComboBoxItem
            {
                Content = display,
                Tag = dev.Address,
                Foreground = isSonca ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981")) : Brushes.White
            };
            CmbDevices.Items.Add(item);

            if (selectedItem == null && IsMatchingModel(dev.Name))
            {
                selectedItem = item;
            }
        }

        if (selectedItem != null)
        {
            CmbDevices.SelectedItem = selectedItem;
        }
        else if (CmbDevices.Items.Count > 0 && CmbDevices.SelectedIndex == -1)
        {
            CmbDevices.SelectedIndex = 0;
        }
    }

    private bool IsMatchingModel(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(_targetModelKeyword)) return false;
        return BluetoothDeviceRemoval.MatchesModel(deviceName, _targetModelKeyword);
    }

    private void CmbDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnConnect.IsEnabled = CmbDevices.SelectedItem != null && !_ble.IsConnected;
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (CmbDevices.SelectedItem is not ComboBoxItem selected || selected.Tag is not ulong address)
        {
            MessageBox.Show("Vui lòng chọn thiết bị trong danh sách.", "Chưa chọn thiết bị", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BtnConnect.IsEnabled = false;
        BtnScan.IsEnabled = false;

        bool success = await _ble.ConnectAsync(address);

        BtnScan.IsEnabled = true;
        BtnConnect.IsEnabled = !success;
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _ble.Disconnect();
    }

    private void OnConnectionStatusChanged(bool connected, string message)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateConnectionUi(connected, message);
        });
    }

    private void UpdateConnectionUi(bool connected, string message)
    {
        TxtStatus.Text = connected ? $"ĐÃ KẾT NỐI ({_ble.ConnectedDeviceName})" : message;
        DotStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#10B981" : "#EF4444"));

        BtnConnect.IsEnabled = !connected && CmbDevices.SelectedItem != null;
        BtnDisconnect.IsEnabled = connected;
    }
    #endregion

    #region Tone Controls & Hardware Config
    private void SliderTone_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderEvents) return;

        if (sender == SliderMusicBass)
        {
            TxtMusicBassVal.Text = $"{SliderMusicBass.Value:+0;-0;0} dB";
            QueueCommand("MusicBass", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicBass, (ushort)(SliderMusicBass.Value + 10), "Music Bass");
        }
        else if (sender == SliderMusicMid)
        {
            TxtMusicMidVal.Text = $"{SliderMusicMid.Value:+0;-0;0} dB";
            QueueCommand("MusicMid", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicMid, (ushort)(SliderMusicMid.Value + 10), "Music Mid");
        }
        else if (sender == SliderMusicTreble)
        {
            TxtMusicTrebleVal.Text = $"{SliderMusicTreble.Value:+0;-0;0} dB";
            QueueCommand("MusicTreble", SoncaBleProtocol.ChannelMusic, SoncaBleProtocol.RegMusicTreble, (ushort)(SliderMusicTreble.Value + 10), "Music Treble");
        }
        else if (sender == SliderMicBass)
        {
            TxtMicBassVal.Text = $"{SliderMicBass.Value:+0;-0;0} dB";
            QueueCommand("MicBass", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicBass, (ushort)(SliderMicBass.Value + 10), "Mic Bass");
        }
        else if (sender == SliderMicMid)
        {
            TxtMicMidVal.Text = $"{SliderMicMid.Value:+0;-0;0} dB";
            QueueCommand("MicMid", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicMid, (ushort)(SliderMicMid.Value + 10), "Mic Mid");
        }
        else if (sender == SliderMicTreble)
        {
            TxtMicTrebleVal.Text = $"{SliderMicTreble.Value:+0;-0;0} dB";
            QueueCommand("MicTreble", SoncaBleProtocol.ChannelMic, SoncaBleProtocol.RegMicTreble, (ushort)(SliderMicTreble.Value + 10), "Mic Treble");
        }
    }

    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!_ble.IsConnected)
        {
            MessageBox.Show("Vui lòng kết nối Bluetooth LE trước.", "Chưa kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        byte[] packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelMusic, 0x6001, 1);
        bool ok = await _ble.SendPacketAsync(packet, "Lưu cấu hình vào EEPROM");
        if (ok)
        {
            MessageBox.Show("Đã gửi lệnh lưu thông số vào bộ nhớ loa (EEPROM) thành công!", "Lưu cấu hình", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnFactoryReset_Click(object sender, RoutedEventArgs e)
    {
        if (!_ble.IsConnected)
        {
            MessageBox.Show("Vui lòng kết nối Bluetooth LE trước.", "Chưa kết nối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var res = MessageBox.Show("Khôi phục toàn bộ thông số phần cứng về mặc định ban đầu?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (res == MessageBoxResult.Yes)
        {
            byte[] packet = SoncaBleProtocol.BuildParamPacket(SoncaBleProtocol.ChannelMusic, 0x6003, 1);
            await _ble.SendPacketAsync(packet, "Khôi phục cấu hình gốc");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtTrafficLog.Clear();
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            if (TxtTrafficLog.Text.Length > 20000)
            {
                TxtTrafficLog.Text = TxtTrafficLog.Text.Substring(10000);
            }
            TxtTrafficLog.AppendText(line + Environment.NewLine);
            TxtTrafficLog.ScrollToEnd();
        });
    }
    #endregion

    #region Shortcut Handling
    private void SoncaBleMixerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (PanelSettings.Visibility == Visibility.Visible)
            {
                PanelSettings.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            Close();
            e.Handled = true;
            return;
        }

        if (MainWindow.TryHandleShortcutsHelpShortcut(e.Key, Keyboard.Modifiers, e.IsRepeat, OpenShortcutsHelp))
        {
            e.Handled = true;
        }
    }

    private void OpenShortcutsHelp()
    {
        var helpWindow = new ShortcutsHelpWindow
        {
            Owner = this
        };
        helpWindow.ShowDialog();
    }
    #endregion
}
