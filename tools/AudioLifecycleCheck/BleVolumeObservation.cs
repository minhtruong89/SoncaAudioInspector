using NAudio.CoreAudioApi;
using SoncaAudioInspector;
using System.Text.Json;

internal static class BleVolumeObservation
{
    public static void WindowsSnapshot()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (!SoncaBleProtocol.IsLikelySoncaDevice(device.FriendlyName)) continue;
                Console.WriteLine(JsonSerializer.Serialize(new { endpoint = device.FriendlyName,
                    volumePercent = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100,
                    volumeDb = device.AudioEndpointVolume.MasterVolumeLevel,
                    channels = Enumerable.Range(0, device.AudioEndpointVolume.Channels.Count).Select(i => device.AudioEndpointVolume.Channels[i].VolumeLevelScalar).ToArray(),
                    mute = device.AudioEndpointVolume.Mute, peak = device.AudioMeterInformation.MasterPeakValue }));
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    Console.WriteLine(JsonSerializer.Serialize(new { session = session.DisplayName, pid = session.GetProcessID,
                        level = session.SimpleAudioVolume.Volume, mute = session.SimpleAudioVolume.Mute, peak = session.AudioMeterInformation.MasterPeakValue }));
                }
            }
        }
    }

    public static async Task RunAsync(int seconds, bool passive)
    {
        void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow, value }));
        using var manager = new SoncaBleManager();
        manager.LogMessage += s => Write(new { log = s });
        manager.ReplyReceived += r => Write(new { reply = r.Command, flag = r.Status, hex = Convert.ToHexString(r.Payload) });
        var peers = await SoncaBleManager.ConnectedAudioPeersAsync(); Write(new { peers });
        manager.StartScanning(); await Task.Delay(8000); manager.StopScanning();
        var preferred = SoncaBleDiscovery.Preferred(manager.DiscoveredDevices, peers);
        // An already-connected radio may stop advertising. This exact address
        // relation was observed for the one connected C360; INFO still validates it.
        ulong address = preferred?.Address ?? (peers.Count == 1 ? peers[0].Address ^ 0x010000000000UL : 0);
        if (address == 0 || !await manager.ConnectAsync(address)) throw new Exception("No associated BLE control link.");
        var previous = new Dictionary<ushort, ushort>();
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end && manager.IsConnected)
        {
            if (!passive && !await manager.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Observe current config (read only)")) break;
            var changes = manager.MixerState.Where(p => !previous.TryGetValue(p.Key, out var old) || old != p.Value)
                .ToDictionary(p => p.Key.ToString("X4"), p => p.Value);
            ushort? Raw(ushort register) => manager.MixerState.TryGetValue(register, out var value) ? value : null;
            Write(new { changes, masterRaw = Raw(0x6102), musicRaw = Raw(0x6103), masterMute = Raw(0x6449), hasSnapshot = manager.MixerState.Count > 0 });
            previous = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (peers.Any(p => device.FriendlyName.Contains(p.Name, StringComparison.OrdinalIgnoreCase)))
                        Write(new { audioEndpoint = device.FriendlyName, volumePercent = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100,
                            mute = device.AudioEndpointVolume.Mute, peak = device.AudioMeterInformation.MasterPeakValue });
                }
            }
            await Task.Delay(2000);
        }
        Write(new { completed = true, passive });
    }
}
