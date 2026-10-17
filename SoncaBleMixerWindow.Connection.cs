using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class SoncaBleMixerWindow
{
    private IReadOnlyList<BluetoothAudioPeer> _audioPeers = Array.Empty<BluetoothAudioPeer>();
    private bool _windowClosing, _autoConnectEnabled = true, _settingsOpenedForConnection;
    private bool _refreshingAudio, _readingMixer;
    private int _commandEpoch;
    private readonly System.Windows.Threading.DispatcherTimer _audioRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<ulong, DateTime> _autoAttempted = new();
    private readonly HashSet<string> _knownMuteChannels = new(), _busyMuteChannels = new();
    private ushort? _knownMicLevel;

    private async Task RefreshAudioPeersAsync()
    {
        if (_refreshingAudio || _windowClosing) return;
        _refreshingAudio = true;
        try
        {
            _audioPeers = await SoncaBleManager.ConnectedAudioPeersAsync();
            TxtAudioConnection.Text = _audioPeers.Count > 0 ? "Bluetooth Windows: " + string.Join(", ", _audioPeers.Select(p => p.Name))
                : "Bluetooth Windows: chưa thấy loa Sonca đang kết nối.";
            await TryAutoConnectAsync();
            if (_writer.IsConnected && _writer.SupportsMixerControls) await ReadMixerStateAsync();
        }
        catch (Exception ex) { AppendLog("Không đọc được kết nối Bluetooth Windows: " + ex.Message); }
        finally { _refreshingAudio = false; }
    }

    private async Task TryAutoConnectAsync()
    {
        if (_windowClosing || !_autoConnectEnabled || _ble.IsConnected || _ble.IsConnecting) return;
        var preferred = SoncaBleDiscovery.Preferred(_deviceList, _audioPeers);
        if (preferred == null || _autoAttempted.TryGetValue(preferred.Address, out var lastAttempt)
            && DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(15)) return;
        _autoAttempted[preferred.Address] = DateTime.UtcNow;
        AppendLog($"Tìm thấy ngõ Sonca BLE {preferred.FormattedAddress} tương ứng địa chỉ loa đang nối Windows; kiểm tra GATT.");
        var item = CmbDevices.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is ulong address && address == preferred.Address);
        if (item != null) CmbDevices.SelectedItem = item;
        BtnConnect.IsEnabled = BtnScan.IsEnabled = false;
        bool connected = await _ble.ConnectAsync(preferred.Address);
        if (_windowClosing) return;
        BtnScan.IsEnabled = true;
        BtnConnect.IsEnabled = !connected && CmbDevices.SelectedItem != null;
        if (connected) await ReadMixerStateAsync();
    }

    private void ClearPendingCommands()
    {
        _sliderDebounceTimer.Stop();
        lock (_pendingCommands) _pendingCommands.Clear();
    }

    private void SetControlAvailability(bool connected)
    {
        // Old tone/input/preset addresses were guesses; keep these unavailable
        // until a device profile validates their encoding and readback.
        foreach (FrameworkElement control in new FrameworkElement[] {
            RadioInputBt, RadioInputUsb, RadioInputOpt, RadioInputHdmi, RadioInputLineIn, RadioInputKaraoke,
            ChkFbx, ChkBassBoost, ChkEnhancer, BtnPreset, SliderMusicBass, SliderMusicMid, SliderMusicTreble,
            SliderMicBass, SliderMicMid, SliderMicTreble, BtnSaveSpeakerConfig, BtnResetSpeakerConfig })
        {
            control.IsEnabled = false;
            control.ToolTip = "Chức năng này chưa được xác minh cho firmware đang nối.";
        }
        if (!connected)
        {
            _commandEpoch++;
            _knownMuteChannels.Clear(); _knownMicLevel = null;
            foreach (var text in new[] { TxtMicVal, TxtMusicVal, TxtMasterVal, TxtEchoVal, TxtReverbVal, TxtLiveVal }) text.Text = "—";
            foreach (var channel in _channelMuted.Keys.ToArray()) _channelMuted[channel] = false;
            foreach (var button in new[] { BtnMuteMic, BtnMuteMusic, BtnMuteMaster, BtnMuteEcho, BtnMuteReverb, BtnMuteLive }) button.Content = "?";
        }
        SliderMicVol.IsEnabled = connected && _writer.SupportsMixerControls && _knownMicLevel.HasValue;
        foreach (var pair in new[] { ("Mic", BtnMuteMic), ("Music", BtnMuteMusic), ("Master", BtnMuteMaster), ("Echo", BtnMuteEcho), ("Reverb", BtnMuteReverb), ("Live", BtnMuteLive) })
        {
            pair.Item2.IsEnabled = connected && _writer.SupportsMixerControls && _knownMuteChannels.Contains(pair.Item1) && !_busyMuteChannels.Contains(pair.Item1);
            pair.Item2.ToolTip = pair.Item2.IsEnabled ? "Tắt/bật tiếng; giữ nguyên mức âm lượng của loa."
                : "Chưa đọc được trạng thái điều khiển từ loa.";
        }
    }

    private async Task ReadMixerStateAsync()
    {
        if (_windowClosing || !_writer.IsConnected || _readingMixer) return;
        _readingMixer = true;
        try
        {
            if (!await _writer.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc trạng thái mixer"))
                AppendLog("Chưa đọc được trạng thái mixer; bấm Quét/Kết nối để thử lại.");
        }
        finally { _readingMixer = false; }
    }

    private void OnMixerStateReceived(IReadOnlyDictionary<ushort, ushort> state)
    {
        int epoch = _commandEpoch;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_windowClosing || epoch != _commandEpoch || !_writer.IsConnected) return;
            foreach (var pair in state) ApplyParameterReadback(pair.Key, pair.Value);
            if (_settingsOpenedForConnection && _knownMuteChannels.Count > 0)
            { PanelSettings.Visibility = Visibility.Collapsed; _settingsOpenedForConnection = false; }
        }));
    }

    private void ApplyParameterReadback(ushort register, ushort value)
    {
        if (_windowClosing) return;
        var volume = register switch
        {
            SoncaBleMixerProfile.MicVolume => (SliderMicVol, TxtMicVal), SoncaBleMixerProfile.MusicVolume => (SliderMusicVol, TxtMusicVal),
            SoncaBleMixerProfile.MasterVolume => (SliderMasterVol, TxtMasterVal), SoncaBleMixerProfile.EchoVolume => (SliderEcho, TxtEchoVal),
            SoncaBleMixerProfile.ReverbVolume => (SliderReverb, TxtReverbVal), SoncaBleMixerProfile.LiveVolume => (SliderLiveVol, TxtLiveVal), _ => (null, null)
        };
        if (volume.Item1 != null && value <= 8192)
        {
            bool deferMic = register == SoncaBleMixerProfile.MicVolume
                && (_sendingSliderCommand || _pendingCommands.ContainsKey("MicVol") || SliderMicVol.IsMouseCaptureWithin);
            if (register == SoncaBleMixerProfile.MicVolume) _knownMicLevel = value;
            if (!deferMic)
            {
                _suppressSliderEvents = true;
                volume.Item1.Maximum = 200;
                volume.Item1.Value = SoncaBleMixerProfile.VolumeFromRaw(value);
                volume.Item2!.Text = volume.Item1.Value.ToString("F0");
                _suppressSliderEvents = false;
            }
        }
        foreach (var pair in new[] { ("Mic", BtnMuteMic), ("Music", BtnMuteMusic), ("Master", BtnMuteMaster), ("Echo", BtnMuteEcho), ("Reverb", BtnMuteReverb), ("Live", BtnMuteLive) })
        {
            if (!SoncaBleMixerProfile.TryMuteRegister(pair.Item1, out ushort muteRegister, out bool inverted) || muteRegister != register || value > 1) continue;
            bool muted = inverted ? value == 0 : value == 1;
            _knownMuteChannels.Add(pair.Item1); _channelMuted[pair.Item1] = muted;
            pair.Item2.Content = muted ? "🔇" : "🔊";
            pair.Item2.Foreground = muted ? Brushes.IndianRed : Brushes.LightGreen;
        }
        SetControlAvailability(_writer.IsConnected);
    }

    private void RestoreKnownMicLevel()
    {
        if (_knownMicLevel.HasValue) ApplyParameterReadback(SoncaBleMixerProfile.MicVolume, _knownMicLevel.Value);
    }
}
