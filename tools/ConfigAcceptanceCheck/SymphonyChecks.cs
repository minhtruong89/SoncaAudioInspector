using SoncaAudioInspector;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;

internal static class SymphonyChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var model = SymphonyModelDefinition.Create("USB playback", "UMIK-1");
        var io = ModelConfigEditorWindow.ParseModel(model.model, System.Text.Json.JsonSerializer.Serialize(model)).testItems.InOut;
        check(io.IsBleCheckAtStartEnabled && !System.Text.Json.JsonSerializer.Serialize(io).Contains("BleCheckAtStart"), "BLE check defaults ON without changing legacy configuration bytes");
        io.BleCheckAtStart = false;
        check(!System.Text.Json.JsonSerializer.Deserialize<InOutConfig>(System.Text.Json.JsonSerializer.Serialize(io))!.IsBleCheckAtStartEnabled,
            "Explicit BLE check OFF survives JSON save/load");
        io.BleCheckAtStart = null;
        static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T value) yield return value;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var item in Children<T>(child)) yield return item;
        }
        var editor = new ModelConfigEditorWindow(model);
        ModelConfig saved;
        try
        {
            var control = Children<CheckBox>(editor).Single(box => box.Name == "CheckBleAtStart");
            check(control.Content?.ToString() == "BLE check ở đầu" && control.IsChecked == true, "Model editor exposes BLE check at start as ON");
            control.IsChecked = false;
            saved = (ModelConfig)typeof(ModelConfigEditorWindow).GetMethod("ReadEditorModel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null)!;
            check(saved.testItems.InOut.BleCheckAtStart == false && model.testItems.InOut.BleCheckAtStart == null,
                "BLE checkbox OFF commits only to the detached editor draft");
        }
        finally { editor.Close(); }
        var reopened = new ModelConfigEditorWindow(saved);
        try { check(Children<CheckBox>(reopened).Single(box => box.Name == "CheckBleAtStart").IsChecked == false, "Model editor restores explicit BLE check OFF"); }
        finally { reopened.Close(); }
        check(model.model == "SYMPHONY" && io.FirmwareModelId == 1062 && io.AcousticCapture && io.BleSelectStrongestByModel,
            "SYMPHONY uses catalogue firmware 1062, strongest BLE and external microphone");
        check(io.Tests.Select(test => test.id).SequenceEqual(new[] { "IO001", "IO002" }) && io.Tests.Select(test => test.SpeakerStage).SequenceEqual(new[] { "LineIn", "Bluetooth" })
            && io.Tests.All(test => test.IsEnabled && test.PlaybackLevelDbfs == -24 && test.ReferenceTestId == null),
            "SYMPHONY has exactly two independent enabled LINE IN/BT tests at conservative level");
        var now = DateTime.Now;
        BleDeviceInfo Device(ulong address, short rssi, int age = 0) => new(address, "advertised name is not trusted", rssi, true, now.AddSeconds(-age))
            { ServiceUuids = new[] { SoncaBleProtocol.PrimaryServiceUuid } };
        var preferences = new AudioBenchPreferences { KnownDeviceModels = new() { [3] = 1075, [4] = 1062 } };
        var ranked = SoncaBleModelSelection.Ranked(new[] { Device(1, -60), Device(2, -30), Device(3, -10), Device(4, -40), Device(5, 0), Device(6, -20, 11) }, 1062, preferences, now);
        check(ranked.Select(device => device.Address).SequenceEqual(new ulong[] { 2, 4, 1 }), "RSSI ranks fresh candidates, excludes known wrong model, zero RSSI and stale adverts; unknown needs INFO");
        var writer = new Writer();
        var report = await SoncaBleRouteVerification.VerifyAsync(writer, new byte[] { 4, 0 }, () => true, default);
        check(report.Confirmed.SequenceEqual(new byte[] { 4, 0 }) && writer.State[SoncaBleMixerProfile.InputSource] == 4,
            "BLE route preflight verifies LINE IN/BT and restores original source");
        writer = new Writer { RefuseLine = true };
        report = await SoncaBleRouteVerification.VerifyAsync(writer, new byte[] { 4, 0 }, () => true, default);
        check(report.Confirmed.SequenceEqual(new byte[] { 0 }) && writer.State[SoncaBleMixerProfile.InputSource] == 4,
            "Unconfirmed LINE IN is not advertised as verified and source is restored");
        writer = new Writer { RefuseRestore = true };
        bool rejected = false;
        try { await SoncaBleRouteVerification.VerifyAsync(writer, new byte[] { 0 }, () => true, default); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "Route restoration failure stops measurement");
    }

    private sealed class Writer : ISoncaMixerWriter
    {
        public Dictionary<ushort, ushort> State = new() { [SoncaBleMixerProfile.InputSource] = 4, [SoncaBleMixerProfile.InputCapabilities] = 64 };
        public bool RefuseLine, RefuseRestore;
        private int _writes;
        public bool IsConnected => true;
        public bool SupportsMixerControls => true;
        public IReadOnlyDictionary<ushort, ushort> MixerState => State;
        public Task<bool> SetOptionAsync(SoncaMixerOption option, bool enabled) => Task.FromResult(false);
        public Task<bool> SendPacketAsync(byte[] data, string? description = null)
        {
            if (data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildReadMixerPacket())) return Task.FromResult(true);
            SoncaBleProtocol.TryParsePacket(data, out _, out var payload);
            byte source = payload[3]; _writes++;
            if (source == 4 && (RefuseLine && _writes == 1 || RefuseRestore && _writes > 1)) return Task.FromResult(false);
            State[SoncaBleMixerProfile.InputSource] = source;
            return Task.FromResult(true);
        }
    }
}
