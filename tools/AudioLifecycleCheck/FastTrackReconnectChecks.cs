using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class FastTrackReconnectChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Logic()
    {
        const string filter = "#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\global/00010006";
        string oldKey = FastTrackDeviceSetup.NormalizeConnectorIdentity("usb#vid_0763&pid_2012&mi_00#old-port" + filter, "wdma_usb.inf");
        string newKey = FastTrackDeviceSetup.NormalizeConnectorIdentity("usb#vid_0763&pid_2012&mi_00#new-port" + filter, "wdma_usb.inf");
        Require(oldKey.Length > 0 && oldKey == newKey, "USB port change did not preserve the KS output identity.");
        Require(oldKey != FastTrackDeviceSetup.NormalizeConnectorIdentity("usb#vid_0763&pid_2012&mi_00#new-port" + filter.Replace("00010006", "00010005"), "wdma_usb.inf"), "Confused two physical outputs.");
        Require(oldKey != FastTrackDeviceSetup.NormalizeConnectorIdentity("usb#vid_0763&pid_2012&mi_00#new-port" + filter, "different-driver.inf"), "Confused different driver layouts.");
        Require(FastTrackDeviceSetup.NormalizeConnectorIdentity("usb#vid_9999&pid_2012&mi_00#new-port" + filter, "wdma_usb.inf") == "", "Matched another soundcard.");
        Require(FastTrackDeviceSetup.LearnPlayback12Identity(new[] { ("Analog Connector 1/2 (FastTrack Pro)", oldKey), ("Analog Connector 1/2 (8- FastTrack Pro)", newKey) }, "") == oldKey, "Could not learn the same output across ports.");
        Require(FastTrackDeviceSetup.LearnPlayback12Identity(new[] { ("Analog Connector 1/2 (FastTrack Pro)", oldKey), ("Analog Connector 1/2 (FastTrack Pro)", "conflict") }, oldKey) == "", "Accepted conflicting historical labels.");
        Require(FastTrackDeviceSetup.LearnPlayback12Identity(Array.Empty<(string, string)>(), oldKey) == oldKey, "Lost persisted identity when Windows pruned old endpoints.");
        Require(FastTrackDeviceSetup.IsUnnamedAnalog("Analog Connector (4- FastTrack Pro)"), "Failed to recognize reset name.");
        Require(!FastTrackDeviceSetup.IsUnnamedAnalog("Analog Connector 3/4 (FastTrack Pro)"), "Would rename a labeled 3/4 output.");
        Require(FastTrackDeviceSetup.NormalizeFriendlyName("Analog Connector 1/2 (8- FastTrack Pro)") == "Analog Connector 1/2 (FastTrack Pro)", "Did not remove USB-instance prefix.");

        // WAVEFORMATEXTENSIBLE: rate and byte rate change; channels/bit depth/mask/subtype do not.
        byte[] original = new byte[40];
        for (int i = 0; i < original.Length; i++) original[i] = (byte)(i + 1);
        BitConverter.GetBytes((ushort)8).CopyTo(original, 12);
        IntPtr format = Marshal.AllocCoTaskMem(original.Length);
        try
        {
            Marshal.Copy(original, 0, format, original.Length);
            var type = typeof(FastTrackDeviceSetup).Assembly.GetType("SoncaAudioInspector.WindowsEndpointPolicy")!;
            type.GetMethod("ChangeRate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { format, 44100 });
            byte[] after = new byte[40];
            Marshal.Copy(format, after, 0, after.Length);
            Require(BitConverter.ToInt32(after, 4) == 44100 && BitConverter.ToInt32(after, 8) == 44100 * 8, "Incorrect byte rate.");
            Require(original.Take(4).SequenceEqual(after.Take(4)) && original.Skip(12).SequenceEqual(after.Skip(12)), "Changed bit depth/channel mask/subformat.");
        }
        finally { Marshal.FreeCoTaskMem(format); }
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "fasttrack-reconnect-logic-pass" }));
    }

    public static void Hardware(bool apply)
    {
        using var enumerator = new MMDeviceEnumerator();
        var history = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
        var active = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
        try
        {
            string identity = FastTrackDeviceSetup.LearnPlayback12Identity(history.Where(FastTrackDeviceSetup.IsFastTrack)
                .Select(d => (d.FriendlyName, FastTrackDeviceSetup.GetPlaybackIdentity(d))), "");
            Require(!string.IsNullOrWhiteSpace(identity), "No unambiguous historical FastTrack 1/2 connector identity.");
            foreach (var device in active)
            {
                using var client = device.AudioClient;
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "endpoint-before", device.ID, device.FriendlyName, flow = device.DataFlow.ToString(), format = client.MixFormat.ToString(), volume = device.AudioEndpointVolume.MasterVolumeLevelScalar, mute = device.AudioEndpointVolume.Mute, identity = FastTrackDeviceSetup.GetPlaybackIdentity(device) }));
            }
            if (!apply) return;
            var output = active.Single(d => FastTrackDeviceSetup.GetPlaybackIdentity(d) == identity);
            var input = active.Single(d => d.DataFlow == DataFlow.Capture && FastTrackDeviceSetup.IsFastTrack(d));
            var snapshots = active.Select(d => (d.ID, Rate: FastTrackDeviceSetup.ReadMixSampleRate(d.ID), d.FriendlyName, Volume: d.AudioEndpointVolume.MasterVolumeLevelScalar, Mute: d.AudioEndpointVolume.Mute)).ToArray();
            bool renamed = FastTrackDeviceSetup.TryRenamePlayback12(output, identity, out var renameMessage);
            Require(renamed || output.FriendlyName.Contains(FastTrackDeviceSetup.Playback12Name), renameMessage);
            Require(FastTrackDeviceSetup.TrySetRecordingSampleRate(input, out var rateMessage), rateMessage);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "fasttrack-settings-applied", renameMessage, rateMessage }));
            foreach (var snapshot in snapshots)
            {
                using var fresh = enumerator.GetDevice(snapshot.ID);
                Require(fresh.AudioEndpointVolume.MasterVolumeLevelScalar == snapshot.Volume && fresh.AudioEndpointVolume.Mute == snapshot.Mute, "Changed volume/mute.");
                if (snapshot.ID != input.ID) Require(FastTrackDeviceSetup.ReadMixSampleRate(snapshot.ID) == snapshot.Rate, "Changed playback or another capture endpoint sample rate.");
                if (snapshot.ID != output.ID) Require(fresh.FriendlyName == snapshot.FriendlyName, "Renamed another endpoint.");
                using var client = fresh.AudioClient;
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "endpoint-after", fresh.ID, fresh.FriendlyName, flow = fresh.DataFlow.ToString(), format = client.MixFormat.ToString() }));
            }
            // Open only a silent capture stream: verify the actual recording format, no tone emitted.
            using var recording = new WasapiCapture(input, false, 100);
            Require(recording.WaveFormat.SampleRate == 44100, "WASAPI capture still uses another sample rate.");
            recording.StartRecording();
            Thread.Sleep(250);
            recording.StopRecording();
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "fasttrack-record-44100-hardware-pass", actualCaptureSampleRate = recording.WaveFormat.SampleRate, playbackSettingsUnchanged = true }));
        }
        finally
        {
            foreach (var device in history.Concat(active)) device.Dispose();
        }
    }

    public static void Ui(string testDirectory, string dependencyDirectory)
    {
        Directory.CreateDirectory(testDirectory);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", testDirectory);
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(dependencyDirectory, name.Name + ".dll");
            return File.Exists(path) ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            AudioRouting? routing = null;
            try
            {
                var app = new App();
                app.InitializeComponent();
                string configPath = (string)typeof(AudioRouting).GetMethod("GetRoutingConfigPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                AtomicFile.WriteAllText(configPath, JsonSerializer.Serialize(new AppConfig
                {
                    UsbPlaybackDeviceId = "missing-old-usb-port-output", RecordingDeviceId = "missing-old-usb-port-input",
                    UsbPlaybackDeviceName = "Analog Connector 1/2 (FastTrack Pro)", RecordingDeviceName = "Analog Connector (FastTrack Pro)",
                    LastPlaybackChannel = 1, LastRecordingChannel = 2, HeadroomEnabled = false
                }));
                using var engine = new AudioEngine();
                routing = new AudioRouting();
                routing.InitializeRouting(engine, new TestRunner(engine));
                Require(routing.SelectedPlaybackDevice?.FriendlyName.Contains(FastTrackDeviceSetup.Playback12Name) == true, "Did not reassociate playback after endpoint GUID/USB prefix changed.");
                Require(routing.SelectedRecordingDevice != null && FastTrackDeviceSetup.IsFastTrack(routing.SelectedRecordingDevice), "Did not reassociate FastTrack recording.");
                Require(engine.PlaybackChannel == 1 && engine.RecordingChannel == 2, "Changed the saved physical channel selection.");
                var saved = AtomicFile.ReadJson<AppConfig>(configPath)!;
                Require(saved.UsbPlaybackDeviceId == routing.SelectedPlaybackDevice!.ID && saved.RecordingDeviceId == routing.SelectedRecordingDevice!.ID
                    && saved.FastTrackPlayback12Identity.Length > 0, "Did not persist new endpoint GUIDs and stable connector identity.");
                string formatText = ((System.Windows.Controls.TextBlock)routing.FindName("TxtRecordingFormat")).Text;
                Require(formatText.Contains("44.1 kHz"), "Did not display actual Windows capture rate.");
                using var enumerator = new MMDeviceEnumerator();
                var playback = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
                try
                {
                    var resolver = typeof(AudioRouting).GetMethod("ResolvePlaybackDevice", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var selected = (MMDevice?)resolver.Invoke(routing, new object[] { "Analog 1/2", "Analog Connector 1/2", playback, true });
                    Require(selected?.ID == routing.SelectedPlaybackDevice.ID, "Auto Test resolves a different output than the recovered 1/2 endpoint.");
                }
                finally { foreach (var device in playback) device.Dispose(); }
                var before = routing.SelectedPlaybackDevice!;
                var workflow = typeof(AudioRouting).GetField("_routingWorkflowActive", BindingFlags.Instance | BindingFlags.NonPublic)!;
                workflow.SetValue(routing, true);
                routing.RefreshDevicesAfterConnection();
                Require(ReferenceEquals(before, routing.SelectedPlaybackDevice), "Hotplug refresh replaced endpoint wrappers during an active measurement.");
                workflow.SetValue(routing, false);
                routing.RefreshDevicesAfterConnection();
                Require(routing.SelectedPlaybackDevice?.ID == before.ID, "Idle hotplug refresh lost the recovered route.");
                typeof(AudioRouting).GetField("_preferredUsbPlaybackDeviceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(routing, "removed-port-output");
                typeof(AudioRouting).GetField("_preferredPlaybackDeviceName", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(routing, "Analog Connector (2- FastTrack Pro)");
                routing.RefreshDevicesAfterConnection();
                Require(routing.SelectedPlaybackDevice?.ID == before.ID, "A generic saved name reassociated to the other Analog output instead of 1/2.");
                Console.WriteLine(JsonSerializer.Serialize(new { stage = "fasttrack-ui-reassociation-busy-guard-pass", recordingFormat = formatText, persistedOutput = saved.UsbPlaybackDeviceId, persistedInput = saved.RecordingDeviceId }));
            }
            catch (Exception ex) { failure = ex; }
            finally { routing?.ReleaseDeviceItems(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("FastTrack UI check failed.", failure);
    }
}
