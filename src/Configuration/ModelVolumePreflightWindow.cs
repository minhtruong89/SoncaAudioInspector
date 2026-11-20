using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed class ModelVolumePreflightWindow : Window
{
    public bool? ConfirmedResult { get; private set; }

    public ModelVolumePreflightWindow(ModelTestVolume policy, Func<Task<IReadOnlyDictionary<ushort, ushort>>> read,
        IReadOnlyDictionary<ushort, ushort> state, CancellationToken token,
        Func<Task<IReadOnlyDictionary<ushort, ushort>>>? applyBle = null)
    {
        Title = "Chỉnh âm lượng theo model"; Width = 620; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ConfigTheme.xaml", UriKind.Relative) });
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        var message = new TextBlock { Name = "VolumeMismatch", Text = string.Join("\n", policy.Mismatches(state)), TextWrapping = TextWrapping.Wrap, FontSize = 16, Foreground = Brushes.Gold, Margin = new Thickness(0,0,0,15) };
        panel.Children.Add(new TextBlock { Text = "Chưa phát tín hiệu đo. Bấm Đặt đúng mức qua BLE, hoặc chỉnh nút trên loa / điều khiển BLE rồi kiểm tra lại.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,15) });
        panel.Children.Add(message);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        var apply = new Button { Name = "ApplyBleVolume", Content = "Đặt đúng mức qua BLE", Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,0) };
        var mixer = new Button { Content = "Điều khiển BLE…", Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,8,0) };
        var retry = new Button { Name = "RecheckVolume", Content = "Kiểm tra lại và tiếp tục", Padding = new Thickness(12,8,12,8) };
        buttons.Children.Add(apply);
        mixer.Click += (_,_) => new SoncaBleMixerWindow { Owner = this, BluetoothAutoPairEnabled = () => false }.ShowDialog(); buttons.Children.Add(mixer);
        buttons.Children.Add(retry);
        void CompleteSuccess()
        {
            ConfirmedResult = true;
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }
        }
        apply.Click += async (_,_) =>
        {
            apply.IsEnabled = retry.IsEnabled = mixer.IsEnabled = false;
            try
            {
                token.ThrowIfCancellationRequested();
                var current = applyBle != null ? await applyBle() : await read();
                token.ThrowIfCancellationRequested();
                var errors = policy.Mismatches(current);
                if (errors.Length == 0) CompleteSuccess();
                else message.Text = string.Join("\n", errors);
            }
            catch (OperationCanceledException) { Close(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { apply.IsEnabled = retry.IsEnabled = mixer.IsEnabled = true; }
        };
        retry.Click += async (_,_) =>
        {
            apply.IsEnabled = retry.IsEnabled = mixer.IsEnabled = false;
            try
            {
                token.ThrowIfCancellationRequested(); var current = await read(); token.ThrowIfCancellationRequested();
                var errors = policy.Mismatches(current);
                if (errors.Length == 0) CompleteSuccess();
                else message.Text = string.Join("\n", errors);
            }
            catch (OperationCanceledException) { Close(); }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { apply.IsEnabled = retry.IsEnabled = mixer.IsEnabled = true; }
        };
        var cancel = new Button { Content = "Hủy đo", IsCancel = true, Margin = new Thickness(8,0,0,0), Padding = new Thickness(12,8,12,8) };
        cancel.Click += (_,_) =>
        {
            ConfirmedResult = false;
            try { DialogResult = false; }
            catch (InvalidOperationException) { Close(); }
        };
        buttons.Children.Add(cancel);
        CancellationTokenRegistration registration = default;
        Loaded += (_,_) => registration = token.Register(() => Dispatcher.BeginInvoke(new Action(Close)));
        Closed += (_,_) => registration.Dispose();
    }
}

public partial class AudioRouting
{
    private readonly Dictionary<ushort, ushort> _genericOriginalBleVolume = new();
    private string? _genericBleVolumeIdentity;

    public Func<ModelVolumePreflightWindow, Task<bool?>>? VolumePreflightPresenter { get; set; }

    public async Task<IReadOnlyDictionary<ushort, ushort>> ConfirmModelVolumeAsync(ModelTestVolume? policy,
        Func<Task<IReadOnlyDictionary<ushort, ushort>>> read, IReadOnlyDictionary<ushort, ushort> state, CancellationToken token,
        Func<ushort, ushort, CancellationToken, Task>? write = null,
        Action<IReadOnlyDictionary<ushort, ushort>, IReadOnlyList<ushort>>? recordOriginal = null,
        Func<CancellationToken, Task>? restoreOnCancel = null,
        int settleDelayMs = 100)
    {
        policy?.Validate();
        if (policy?.RequiresBle != true || policy.Mismatches(state).Length == 0) return state;

        async Task<IReadOnlyDictionary<ushort, ushort>> TryApplyBleVolumeAsync(IReadOnlyDictionary<ushort, ushort> current)
        {
            if (write == null) return await read();
            var targets = policy.BleTargets()
                .Where(t => !current.TryGetValue(t.Register, out ushort raw) || raw > 8192
                    || Math.Abs(SoncaBleMixerProfile.VolumeFromRaw(raw) - t.Required) > .55)
                .ToList();
            if (targets.Count == 0) return current;
            var changedRegisters = targets
                .Select(t => (t.Name, t.Register, t.Required, TargetRaw: SoncaBleMixerProfile.VolumeToRaw(t.Required)))
                .Where(t => SoncaBleMixerProfile.CanWrite(t.Register, t.TargetRaw))
                .ToList();
            if (changedRegisters.Count == 0) return current;

            recordOriginal?.Invoke(current, changedRegisters.Select(t => t.Register).ToArray());
            foreach (var item in changedRegisters)
            {
                token.ThrowIfCancellationRequested();
                await write(item.Register, item.TargetRaw, token);
            }
            if (settleDelayMs > 0) await Task.Delay(settleDelayMs, token);
            var updated = await read();
            foreach (var item in changedRegisters)
            {
                if (updated.TryGetValue(item.Register, out ushort afterRaw)
                    && Math.Abs(SoncaBleMixerProfile.VolumeFromRaw(afterRaw) - item.Required) <= .55)
                {
                    string beforeText = current.TryGetValue(item.Register, out ushort beforeRaw) && beforeRaw <= 8192
                        ? $"{SoncaBleMixerProfile.VolumeFromRaw(beforeRaw):0.##}%"
                        : "chưa rõ";
                    AppendLog("Âm lượng", $"Đã đặt {item.Name} {beforeText} → {item.Required:0.##}% qua BLE.");
                }
            }
            return updated;
        }

        if (write != null)
        {
            try
            {
                state = await TryApplyBleVolumeAsync(state);
                if (policy.Mismatches(state).Length == 0) return state;
            }
            catch (OperationCanceledException)
            {
                if (restoreOnCancel != null)
                    try { await restoreOnCancel(CancellationToken.None); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("Âm lượng", "Chưa tự đặt được âm lượng qua BLE: " + ex.Message);
            }
        }

        var dialog = new ModelVolumePreflightWindow(policy, read, state, token,
            write != null ? async () => state = await TryApplyBleVolumeAsync(state) : null)
        { Owner = IsLoaded ? Window.GetWindow(this) : null };
        bool? accepted = VolumePreflightPresenter != null
            ? await VolumePreflightPresenter(dialog)
            : dialog.ShowDialog();
        if (accepted != true && dialog.ConfirmedResult != true)
        {
            if (restoreOnCancel != null)
                try { await restoreOnCancel(CancellationToken.None); } catch { }
            token.ThrowIfCancellationRequested();
            throw new OperationCanceledException("Đã hủy đo trước khi phát: âm lượng loa chưa đúng model.");
        }
        var confirmed = await read();
        token.ThrowIfCancellationRequested();
        policy.RequireMatch(confirmed);
        return confirmed;
    }

    private async Task ConfirmGenericModelVolumeAsync()
    {
        var policy = _activeInOutConfig?.TestVolume;
        policy?.Validate(); if (policy?.RequiresBle != true) return;
        var ble = SoncaBleManager.Instance;
        string identity = ble.ConnectedDeviceAddress;
        if (!string.IsNullOrEmpty(_genericBleVolumeIdentity) && _genericBleVolumeIdentity != identity)
            _genericOriginalBleVolume.Clear();
        _genericBleVolumeIdentity = identity;
        async Task<IReadOnlyDictionary<ushort, ushort>> Read()
        {
            if (!ble.IsConnected || !ble.SupportsMixerControls || !await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket()))
                throw new InvalidOperationException("Model yêu cầu kiểm tra âm lượng loa. Kết nối đúng BLE trong Ctrl+Shift+H trước khi đo.");
            if (ble.ConnectedDeviceAddress != identity) throw new InvalidOperationException("Loa BLE đã đổi; chọn lại đúng loa trước khi đo.");
            return ble.MixerState.ToDictionary(p => p.Key, p => p.Value);
        }
        async Task Write(ushort reg, ushort raw, CancellationToken _)
        {
            if (!ble.IsConnected || ble.ConnectedDeviceAddress != identity)
                throw new InvalidOperationException("Loa BLE đã đổi trong lúc chỉnh âm lượng.");
            if (!await ble.SendPacketAsync(SoncaBleProtocol.BuildParamPacket(0, reg, raw)))
                throw new InvalidOperationException($"Không ghi được thanh ghi âm lượng 0x{reg:X4} qua BLE.");
        }
        void RecordOriginal(IReadOnlyDictionary<ushort, ushort> before, IReadOnlyList<ushort> regs)
        {
            foreach (ushort reg in regs)
                if (before.TryGetValue(reg, out ushort raw) && SoncaBleMixerProfile.CanWrite(reg, raw))
                    _genericOriginalBleVolume.TryAdd(reg, raw);
        }
        await ConfirmModelVolumeAsync(policy, Read, await Read(), CancellationToken.None,
            write: Write, recordOriginal: RecordOriginal, restoreOnCancel: _ => RestoreGenericModelVolumeAsync(clearSession: true), settleDelayMs: 100);
    }

    private async Task RestoreGenericModelVolumeAsync(bool clearSession = false)
    {
        if (_genericOriginalBleVolume.Count == 0) return;
        var ble = SoncaBleManager.Instance;
        if (!ble.IsConnected || !ble.SupportsMixerControls
            || (!string.IsNullOrEmpty(_genericBleVolumeIdentity) && ble.ConnectedDeviceAddress != _genericBleVolumeIdentity))
        {
            AppendLog("Âm lượng", "Chưa khôi phục được âm lượng gốc qua BLE: loa đã ngắt hoặc đổi thiết bị.");
            return;
        }
        var failures = new List<string>();
        foreach (var (reg, raw) in _genericOriginalBleVolume)
        {
            if (!await ble.SendPacketAsync(SoncaBleProtocol.BuildParamPacket(0, reg, raw)))
                failures.Add($"0x{reg:X4}");
        }
        await Task.Delay(100); // SET → 100 ms → GET, same settle rule as Ctrl+Shift+H.
        if (!await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket()))
            failures.Add("readback");
        else
        {
            foreach (var (reg, raw) in _genericOriginalBleVolume)
                if (!ble.MixerState.TryGetValue(reg, out ushort actual) || actual != raw)
                    failures.Add($"0x{reg:X4}≠{raw}");
        }
        if (failures.Count > 0)
            AppendLog("Âm lượng", "Chưa khôi phục đủ âm lượng gốc qua BLE: " + string.Join(", ", failures));
        else
        {
            AppendLog("Âm lượng", "Đã khôi phục âm lượng loa về mức trước khi đo.");
            if (clearSession || _autoTestResumeSession == null)
            {
                _genericOriginalBleVolume.Clear();
                _genericBleVolumeIdentity = null;
            }
        }
    }
}
