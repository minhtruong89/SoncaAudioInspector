namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    public short? SelectedModelRssi { get; private set; }

    private async Task<bool> PrepareStrongestModelAsync(ushort expectedModel, CancellationToken token)
    {
        SelectedModelRssi = null;
        if (SoncaFirmwareModels.Current.Find(expectedModel).Count == 0) return false;
        try
        {
            StartScanning();
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            var preferences = AudioBenchPreferences.Load();
            var candidates = SoncaBleModelSelection.Ranked(DiscoveredDevices, expectedModel, preferences, DateTime.Now);
            StopScanning();
            foreach (var device in candidates.Take(8))
            {
                token.ThrowIfCancellationRequested();
                LogMessage?.Invoke($"[BLE] Dò model {expectedModel}: {device.FormattedAddress}, RSSI {device.Rssi} dBm; chờ INFO xác nhận tên trong file.");
                bool ready = await ConnectCoreAsync(device.Address, (info, _) =>
                    SoncaBleMixerProfile.TryReadModelId(info, out ushort actual) && actual == expectedModel
                    && SoncaFirmwareModels.Current.Find(actual).Count > 0);
                token.ThrowIfCancellationRequested();
                if (!ready) continue;
                SelectedModelRssi = device.Rssi;
                LogMessage?.Invoke($"[BLE] Đã chọn {string.Join(" / ", SoncaFirmwareModels.Current.Find(expectedModel))} · {device.FormattedAddress} · {device.Rssi} dBm (mạnh nhất đã xác minh).");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) { Disconnect(); throw; }
        finally { StopScanning(); }
    }

    /// <summary>Model option "Tiền tố tên BLE" (Ctrl+Shift+M, owner 2026-11-19): scan one window, keep the controllers whose
    /// advertised name starts with <paramref name="prefix"/>, try them from the strongest RSSI down. When the model has a
    /// FirmwareModelId the INFO reply must carry it (C360 still advertises KBEATBOX, the same name as KBEATBOX CHILL).</summary>
    private async Task<bool> PrepareStrongestByNamePrefixAsync(string prefix, ushort? expectedModel, CancellationToken token)
    {
        SelectedModelRssi = null;
        try
        {
            StartScanning();
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            var preferences = AudioBenchPreferences.Load();
            var candidates = SoncaBleModelSelection.RankedByNamePrefix(DiscoveredDevices, prefix, expectedModel, preferences, DateTime.Now);
            StopScanning();
            if (candidates.Length == 0)
                LogMessage?.Invoke($"[BLE] Không thấy loa BLE nào có tên bắt đầu bằng \"{prefix}\" trong 4 s quét.");
            foreach (var device in candidates.Take(8))
            {
                token.ThrowIfCancellationRequested();
                LogMessage?.Invoke($"[BLE] Tiền tố \"{prefix}\": thử {device.Name} · {device.FormattedAddress} · RSSI {device.Rssi} dBm"
                    + (expectedModel is ushort id ? $"; chờ INFO xác nhận model {id}." : "."));
                bool ready = await ConnectCoreAsync(device.Address, (info, _) => expectedModel is not ushort wanted
                    || SoncaBleMixerProfile.TryReadModelId(info, out ushort actual) && actual == wanted);
                token.ThrowIfCancellationRequested();
                if (!ready) continue;
                SelectedModelRssi = device.Rssi;
                LogMessage?.Invoke($"[BLE] Đã chọn {device.Name} · {device.FormattedAddress} · {device.Rssi} dBm (mạnh nhất theo tiền tố \"{prefix}\").");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) { Disconnect(); throw; }
        finally { StopScanning(); }
    }
}

public static class SoncaBleModelSelection
{
    public static BleDeviceInfo[] Ranked(IEnumerable<BleDeviceInfo> devices, ushort expectedModel, AudioBenchPreferences preferences, DateTime now)
        => devices.Where(device => device.ServiceUuids.Contains(SoncaBleProtocol.PrimaryServiceUuid)
            && device.Rssi is >= -127 and < 0 && now - device.LastSeen >= TimeSpan.Zero && now - device.LastSeen <= TimeSpan.FromSeconds(10)
            && (preferences.KnownDeviceModels == null || !preferences.KnownDeviceModels.TryGetValue(device.Address, out ushort actual) || actual == expectedModel))
            .GroupBy(device => device.Address).Select(group => group.OrderByDescending(device => device.LastSeen).First())
            .OrderByDescending(device => device.Rssi).ThenBy(device => device.Address).ToArray();

    public static bool MatchesNamePrefix(string? name, string? prefix)
        => !string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(name)
            && name.Trim().StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Fresh advertisements (≤ 10 s, valid RSSI) whose name starts with the prefix, strongest first. The name may only be in
    /// some packets (scan response), so any named packet of the address counts. Addresses remembered as another model are skipped.</summary>
    public static BleDeviceInfo[] RankedByNamePrefix(IEnumerable<BleDeviceInfo> devices, string prefix, ushort? expectedModel,
        AudioBenchPreferences preferences, DateTime now)
        => devices.GroupBy(device => device.Address).Select(group =>
            {
                var latest = group.OrderByDescending(device => device.LastSeen).First();
                string name = group.OrderByDescending(device => device.LastSeen).Select(device => device.Name)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
                return latest with { Name = name };
            })
            .Where(device => MatchesNamePrefix(device.Name, prefix)
                && device.Rssi is >= -127 and < 0 && now - device.LastSeen >= TimeSpan.Zero && now - device.LastSeen <= TimeSpan.FromSeconds(10)
                && (expectedModel is not ushort wanted || preferences.KnownDeviceModels == null
                    || !preferences.KnownDeviceModels.TryGetValue(device.Address, out ushort actual) || actual == wanted))
            .OrderByDescending(device => device.Rssi).ThenBy(device => device.Address).ToArray();
}
