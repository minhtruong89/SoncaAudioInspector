using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class SymphonyLiveChecks
{
    public static async Task Run(string output, string routingFile, bool measure, string? modelFile = null)
    {
        Directory.CreateDirectory(output);
        using var routing = JsonDocument.Parse(File.ReadAllText(routingFile));
        string Text(string key) => routing.RootElement.GetProperty(key).GetString()!;
        var model = modelFile == null ? SymphonyModelDefinition.Create(Text("UsbPlaybackDeviceName"), Text("RecordingDeviceName"))
            : ModelConfigEditorWindow.ParseModel("SYMPHONY", File.ReadAllText(modelFile));
        void Save(string file, object data) => File.WriteAllText(Path.Combine(output, file), JsonSerializer.Serialize(data, new JsonSerializerOptions {
            WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Save("SYMPHONY.model.json", model);
        using var ble = new SoncaBleManager();
        var logLock = new object();
        ble.LogMessage += line => { lock (logLock) File.AppendAllText(Path.Combine(output, "ble.log"), line + Environment.NewLine); if (line.StartsWith("[BLE]") || line.StartsWith("[BT]")) Console.WriteLine(line); };
        if (!await ble.EnsureConnectedAsync(userInitiated: true, expectedModelId: 1062, selectStrongestByModel: true))
            throw new InvalidOperationException("Không xác minh được SYMPHONY mạnh nhất theo file; không phát âm thanh.");
        string identity = ble.ConnectedDeviceAddress;
        Acoustic3ModelDefinition.RequireFirmware(model.testItems.InOut, ble.FirmwareInfoPayload);
        var before = ble.MixerState.ToDictionary(pair => pair.Key, pair => pair.Value);
        Save("baseline.json", new { identity, ble.FirmwareIdentity, ble.SelectedModelRssi, parameters = before });
        var routes = await SoncaBleRouteVerification.VerifyAsync(ble, new byte[] { 4, 0 }, () => ble.IsConnected && ble.ConnectedDeviceAddress == identity, default);
        Save("routes.json", new { original = (int)routes.Original,
            available = routes.Available.Select(source => new { source = (int)source, name = ble.InputSourceName(source) }),
            confirmed = routes.Confirmed.Select(source => new { source = (int)source, name = ble.InputSourceName(source) }) });
        Console.WriteLine("Đã xác nhận ngõ: " + string.Join(", ", routes.Confirmed.Select(ble.InputSourceName)));
        if (!routes.Confirmed.Contains((byte)4) || !routes.Confirmed.Contains((byte)0)) throw new InvalidOperationException("Thiếu ngõ đã xác nhận; không phát tín hiệu.");
        if (!measure) return;
        using var enumeration = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumeration.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device) endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        var plan = C360ModelPlan.Create(model.testItems.InOut, endpoints, new("unused", "", "unused"));
        var engine = new AudioEngine();
        try
        {
            var bench = new C360AudioBench(engine, ble, Path.Combine(output, "capture"));
            var reference = new C360ReferenceBench(bench, plan, model.model, Path.Combine(output, "standards"),
                SoncaMixerControls.Fingerprint(before, ble.FirmwareInfoPayload, 0), false, 3, requireTrialReference: false);
            var results = await new C360ElectronicSuite().RunAsync(plan.Bind(reference), plan.Setup, stages: plan.Stages, initialBaseline: before);
            Save("results.json", results);
            foreach (var result in results) Console.WriteLine(result.StageName + ": " + result.Status + " · " + result.Summary);
        }
        finally
        {
            engine.StopAllAudio();
            bool read = await ble.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Xác nhận khôi phục sau đo SYMPHONY");
            var diff = before.Where(pair => !ble.MixerState.TryGetValue(pair.Key, out ushort actual) || actual != pair.Value)
                .Select(pair => new { register = pair.Key.ToString("X4"), before = pair.Value, after = ble.MixerState.GetValueOrDefault(pair.Key) }).ToArray();
            Save("restoration.json", new { read, diff });
            if (!read || diff.Length > 0) throw new InvalidOperationException("Chưa khôi phục đủ trạng thái loa sau đo; xem restoration.json.");
        }
    }
}
