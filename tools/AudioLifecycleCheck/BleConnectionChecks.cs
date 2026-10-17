using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

internal static class BleConnectionChecks
{
    public static void Logic()
    {
        Protocol();
        const ulong audio = 0x433E3F752ED6, ble = 0x423E3F752ED6;
        var services = new[] { SoncaAudioInspector.SoncaBleProtocol.PrimaryServiceUuid };
        var unnamed = SoncaAudioInspector.SoncaBleDiscovery.Merge(ble, "", -50, services)
            ?? throw new Exception("Unnamed Sonca service advertisement was discarded.");
        var response = SoncaAudioInspector.SoncaBleDiscovery.Merge(ble, "", -48, Array.Empty<Guid>(), unnamed)!;
        if (!response.ServiceUuids.Contains(services[0]) || !response.IsLikelySonca)
            throw new Exception("Scan response erased the service identification.");
        if (SoncaAudioInspector.SoncaBleDiscovery.Merge(123, "", -20, Array.Empty<Guid>()) != null)
            throw new Exception("Unidentified nearby radio became a Sonca controller.");
        var other = SoncaAudioInspector.SoncaBleDiscovery.Merge(0x423E3F752E09, "", -20, services)!;
        var preferred = SoncaAudioInspector.SoncaBleDiscovery.Preferred(new[] { other, response }, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") });
        if (preferred?.Address != ble || SoncaAudioInspector.SoncaBleDiscovery.Preferred(new[] { other }, new[] { new SoncaAudioInspector.BluetoothAudioPeer(audio, "KBEATBOX") }) != null)
            throw new Exception("Controller selection followed RSSI instead of the connected peer address.");
        Console.WriteLine("PASS unnamed Sonca service, merged scan response, unrelated-radio rejection and unique connected-peer association.");
    }

    private static void Protocol()
    {
        byte[] config = Convert.FromHexString(string.Concat(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "C360-config.hex")).Where(c => !char.IsWhiteSpace(c))));
        var decoder = new SoncaAudioInspector.SoncaBleReplyDecoder();
        for (int split = 0; split <= config.Length; split++)
        {
            decoder.Clear();
            var replies = decoder.Push(config.AsSpan(0, split)).Concat(decoder.Push(config.AsSpan(split))).ToArray();
            if (replies.Length != 1 || !SoncaAudioInspector.SoncaBleMixerProfile.TryReadSnapshot(replies[0], out var state)
                || state.Count != 216 || state[0x6400] != 4096 || state[0x6102] != 8192 || state[0x6404] != 1)
                throw new Exception($"Captured C360 config failed at split {split}.");
        }
        byte[] damaged = config.ToArray(); damaged[50] ^= 1;
        decoder.Clear();
        if (decoder.Push(damaged.Concat(config).ToArray()).Count != 1) throw new Exception("CRC failure accepted or prevented next frame recovery.");
        decoder.Clear();
        var ack = Convert.FromHexString("000302012D");
        if (decoder.Push(ack.Concat(config).Concat(ack).ToArray()).Count != 3) throw new Exception("Coalesced frames failed.");
        if (!SoncaAudioInspector.SoncaBleProtocol.BuildReadMixerPacket().AsSpan().SequenceEqual(Convert.FromHexString("028002046209000199"))) throw new Exception("GET config wire packet mismatch.");
        if (SoncaAudioInspector.SoncaBleMixerProfile.VolumeToRaw(100) != 4096 || SoncaAudioInspector.SoncaBleMixerProfile.VolumeFromRaw(8192) != 200
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x3140, 50) || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6400, 8193)
            || SoncaAudioInspector.SoncaBleMixerProfile.CanWrite(0x6448, 2)) throw new Exception("Unsafe legacy address or gain encoding accepted.");
        Console.WriteLine("PASS captured 216-parameter C360 frame at every split, coalesced packets, CRC recovery, config query and control bounds.");
    }

    public static async Task ConnectControlAsync(bool exerciseControls = false)
    {
        using var manager = new SoncaAudioInspector.SoncaBleManager();
        manager.LogMessage += message => Console.WriteLine(message);
        manager.ConnectionStatusChanged += (ready, status) => Console.WriteLine($"READY={ready}: {status}");
        int notifications = 0;
        manager.DataReceived += (data, description) => { Interlocked.Increment(ref notifications); Console.WriteLine("REPLY " + description); };
        var peers = await SoncaAudioInspector.SoncaBleManager.ConnectedAudioPeersAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "connected-audio", peers }));
        manager.StartScanning(); await Task.Delay(10000); manager.StopScanning();
        foreach (var info in manager.DiscoveredDevices.Where(d => d.IsLikelySonca)) Console.WriteLine(JsonSerializer.Serialize(info));
        var preferred = SoncaAudioInspector.SoncaBleDiscovery.Preferred(manager.DiscoveredDevices, peers);
        if (preferred == null) { Console.WriteLine("INCONCLUSIVE: no unique Sonca BLE radio associated with the connected Windows speaker; no commands sent."); return; }
        bool connected = await manager.ConnectAsync(preferred.Address);
        Console.WriteLine($"Connection ready={connected}; service={manager.ActiveServiceUuid}; write={manager.ActiveWriteCharUuid}; notify={manager.ActiveNotifyCharUuid}");
        if (connected)
        {
            if (!await manager.SendPacketAsync(SoncaAudioInspector.SoncaBleProtocol.BuildReadMixerPacket(), "READ ONLY config query")) throw new Exception("Config readback failed.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "snapshot", manager.SupportsMixerControls, parameters = manager.MixerState.ToDictionary(p => p.Key.ToString("X4"), p => p.Value) }));
            if (exerciseControls) await ExerciseAsync(manager);
        }
        Console.WriteLine($"Notifications={notifications}; controlExercise={exerciseControls}; no EEPROM or reset commands were emitted.");
    }

    private static async Task ExerciseAsync(SoncaAudioInspector.SoncaBleManager manager)
    {
        if (!manager.SupportsMixerControls) throw new Exception("No verified device profile: no writes allowed.");
        var baseline = manager.MixerState.ToDictionary(p => p.Key, p => p.Value);
        foreach (ushort register in new ushort[] { 0x6447, 0x6448, 0x6449, 0x6404, 0x640A, 0x6468, 0x6400 })
        {
            if (!baseline.TryGetValue(register, out ushort original)) throw new Exception($"Missing original 0x{register:X4}; no write allowed.");
            ushort changed = register == 0x6400 ? (ushort)(original > 41 ? original - 41 : original) : (ushort)(original == 0 ? 1 : 0);
            bool attempted = false;
            try
            {
                attempted = true;
                if (!await manager.SetParamAsync(0, register, changed, "Reversible hardware check")) throw new Exception($"Write/readback failed 0x{register:X4}.");
                Console.WriteLine($"VERIFIED 0x{register:X4}: {original} -> {manager.MixerState[register]} (DSP readback).");
            }
            finally
            {
                if (attempted)
                {
                    bool restored = await manager.SetParamAsync(0, register, original, "Restore exact original setting");
                    if (!restored) restored = await manager.SetParamAsync(0, register, original, "Retry original setting restore");
                    if (!restored) throw new Exception($"RESTORE FAILED 0x{register:X4}; original={original}.");
                    Console.WriteLine($"RESTORED 0x{register:X4}={original} (DSP readback).");
                }
            }
        }
        var changes = baseline.Where(p => !manager.MixerState.TryGetValue(p.Key, out var actual) || actual != p.Value).ToArray();
        if (changes.Length != 0) throw new Exception("Final config differs: " + JsonSerializer.Serialize(changes));
        Console.WriteLine($"PASS all six mute/enable controls and MIC gain, restored all {baseline.Count} parameters to the initial readback; no audible-output claim.");
    }

    public static async Task SnapshotAsync()
    {
        void Write(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        Write(new { stage = "adapter", adapter?.IsLowEnergySupported, adapter?.IsClassicSupported, adapter?.IsCentralRoleSupported });
        foreach (var pair in new[] { ("classic-connected", BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)),
            ("ble-known", BluetoothLEDevice.GetDeviceSelector()) })
        {
            var devices = await DeviceInformation.FindAllAsync(pair.Item2);
            foreach (var info in devices)
            {
                if (pair.Item1 == "classic-connected")
                {
                    using var device = await BluetoothDevice.FromIdAsync(info.Id);
                    Write(new { stage = pair.Item1, info.Name, info.Pairing.IsPaired, address = device?.BluetoothAddress.ToString("X12"), state = device?.ConnectionStatus.ToString(),
                        properties = device?.DeviceInformation.Properties.Where(p => p.Key.Contains("Container")).ToDictionary(p => p.Key, p => p.Value?.ToString()) });
                }
                else Write(new { stage = pair.Item1, info.Name, info.Pairing.IsPaired });
            }
        }
        var adverts = new ConcurrentDictionary<ulong, (string Name, short Rssi, string[] Services, string Type)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) => adverts.AddOrUpdate(e.BluetoothAddress,
            _ => (e.Advertisement.LocalName, e.RawSignalStrengthInDBm, e.Advertisement.ServiceUuids.Select(u => u.ToString()).ToArray(), e.AdvertisementType.ToString()),
            (_, old) => (string.IsNullOrWhiteSpace(e.Advertisement.LocalName) ? old.Name : e.Advertisement.LocalName,
                e.RawSignalStrengthInDBm, old.Services.Union(e.Advertisement.ServiceUuids.Select(u => u.ToString())).ToArray(), e.AdvertisementType.ToString()));
        watcher.Stopped += (_, e) => Write(new { stage = "scan-stopped", error = e.Error.ToString() });
        watcher.Start(); await Task.Delay(12000); watcher.Stop();
        Write(new { stage = "scan-summary", count = adverts.Count });
        foreach (var pair in adverts.OrderByDescending(p => p.Value.Rssi))
        {
            // Discovery only. No application packets, volume/mute writes or pairing changes.
            Write(new { stage = "advertisement", address = pair.Key.ToString("X12"), name = pair.Value.Name,
                rssi = pair.Value.Rssi, services = pair.Value.Services, type = pair.Value.Type });
            if (!SoncaAudioInspector.SoncaBleProtocol.IsLikelySoncaDevice(pair.Value.Name)
                && !pair.Value.Services.Contains(SoncaAudioInspector.SoncaBleProtocol.PrimaryServiceUuid.ToString())) continue;
            try
            {
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(pair.Key);
                if (device == null) continue;
                Write(new { stage = "ble-device", device.Name, device.DeviceId, address = device.BluetoothAddress.ToString("X12"),
                    properties = device.DeviceInformation.Properties.Where(p => p.Key.Contains("Container") || p.Key.Contains("Address")).ToDictionary(p => p.Key, p => p.Value?.ToString()) });
                var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
                Write(new { stage = "gatt-services", device.Name, status = services.Status.ToString() });
                foreach (var service in services.Services)
                {
                    using (service)
                    {
                        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                        Write(new { stage = "gatt-characteristics", service = service.Uuid, status = chars.Status.ToString(),
                            characteristics = chars.Characteristics.Select(c => new { c.Uuid, properties = c.CharacteristicProperties.ToString() }) });
                        foreach (var characteristic in chars.Characteristics.Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)))
                        {
                            var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                            if (value.Value == null) continue;
                            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(value.Value);
                            var bytes = new byte[value.Value.Length]; reader.ReadBytes(bytes);
                            Write(new { stage = "gatt-read", characteristic = characteristic.Uuid, status = value.Status.ToString(), length = bytes.Length,
                                hex = Convert.ToHexString(bytes.Take(256).ToArray()), ascii = System.Text.Encoding.ASCII.GetString(bytes.Take(256).Select(b => b >= 32 && b < 127 ? b : (byte)'.').ToArray()) });
                        }
                    }
                }
            }
            catch (Exception ex) { Write(new { stage = "gatt-probe-error", name = pair.Value.Name, error = ex.Message }); }
        }
    }
}
