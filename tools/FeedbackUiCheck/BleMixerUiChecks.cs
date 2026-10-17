using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static class BleMixerUiChecks
{
    public static void Live(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        var window = (Window)Activator.CreateInstance(type, new object?[] { null })!;
        var manager = assembly.GetType("SoncaAudioInspector.SoncaBleManager")!.GetProperty("Instance")!.GetValue(null)!;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(40);
        bool ready = false;
        timer.Tick += (_, _) =>
        {
            ready = ((Button)window.FindName("BtnMuteMusic")).IsEnabled;
            if (ready || DateTime.UtcNow > deadline) { timer.Stop(); frame.Continue = false; }
        };
        try
        {
            window.Show(); timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, "ble-mixer-live.png"))) png.Save(stream);
            var values = new[] { "TxtStatus", "TxtMicVal", "TxtEchoVal", "TxtReverbVal", "TxtMusicVal", "TxtMasterVal", "TxtLiveVal" }
                .ToDictionary(n => n, n => ((TextBlock)window.FindName(n)).Text);
            File.WriteAllText(Path.Combine(output, "ble-mixer-live.json"), System.Text.Json.JsonSerializer.Serialize(new { ready, values }));
            if (!ready) throw new Exception("Live mixer did not auto-connect and read speaker state; see screenshot/log.");
            Console.WriteLine("PASS live WPF mixer auto-associated Windows audio with Sonca BLE, completed INFO/config and enabled actual readback controls. No control values changed.");
        }
        finally { timer.Stop(); window.Close(); manager.GetType().GetMethod("Disconnect")!.Invoke(manager, null); }
    }

    public static void Run(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var writer = (MixerWriterProxy)DispatchProxy.Create(assembly.GetType("SoncaAudioInspector.ISoncaMixerWriter")!, typeof(MixerWriterProxy));
        var type = assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!;
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = type.GetConstructors(hidden).Single(c => c.GetParameters().Length == 2);
        var window = (Window)constructor.Invoke(new object[] { "TEST MODEL", writer });
        var mute = (Button)window.FindName("BtnMuteMusic");
        var update = type.GetMethod("UpdateConnectionUi", hidden)!;
        var click = type.GetMethod("BtnMuteChannel_Click", hidden)!;
        var states = (Dictionary<string, bool>)type.GetField("_channelMuted", hidden)!.GetValue(window)!;
        if (writer.Packets.Count != 0 || mute.IsEnabled) throw new Exception("Mixer initialization wrote hardware or enabled offline controls.");
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (writer.Packets.Count != 0 || states["Music"]) throw new Exception("Offline mute changed state or sent a packet.");
        writer.Connected = true; update.Invoke(window, new object[] { true, "Fake GATT ready" });
        var readback = type.GetMethod("ApplyParameterReadback", hidden)!;
        if (mute.IsEnabled) throw new Exception("Mute enabled before the speaker state was read.");
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        string? before = mute.Content.ToString();
        writer.Success = false; click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (mute.Content.ToString() != before || states["Music"] || !mute.IsEnabled) throw new Exception("Failed GATT write falsely changed mute UI.");
        writer.Success = true; click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (!states["Music"] || mute.Content.ToString() == before) throw new Exception("Successful mute request did not update its requested state.");
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (states["Music"]) throw new Exception("Successful unmute request did not clear its requested state.");

        var echo = (Button)window.FindName("BtnMuteEcho");
        if (echo.IsEnabled) throw new Exception("An unknown effect state can be overwritten.");
        readback.Invoke(window, new object[] { (ushort)0x6404, (ushort)1 });
        click.Invoke(window, new object[] { echo, new RoutedEventArgs() });
        click.Invoke(window, new object[] { echo, new RoutedEventArgs() });
        byte[] restored = writer.Packets.Last();
        if (restored[4] != 0x64 || restored[5] != 0x04 || restored[6] != 0 || restored[7] != 1)
            throw new Exception("Echo unmute did not use the dedicated enable register; level must stay untouched.");
        readback.Invoke(window, new object[] { (ushort)0x6400, (ushort)4096 });
        var slider = (Slider)window.FindName("SliderMicVol");
        if (slider.Value != 100 || slider.Maximum != 200) throw new Exception("MIC Q12 volume readback scaled incorrectly.");
        type.GetMethod("QueueCommand", hidden)!.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)1515, "Mic" });
        writer.Connected = false; update.Invoke(window, new object[] { false, "Disconnected" });
        if (((System.Collections.IDictionary)type.GetField("_pendingCommands", hidden)!.GetValue(window)!).Count != 0)
            throw new Exception("Disconnect retained a pending command for the next speaker.");

        writer.Connected = true; update.Invoke(window, new object[] { true, "Reconnected" });
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        writer.Pending = new TaskCompletionSource<bool>();
        click.Invoke(window, new object[] { mute, new RoutedEventArgs() });
        if (mute.IsEnabled || states["Music"]) throw new Exception("Pending command updated or re-enabled mute.");
        writer.Connected = false; update.Invoke(window, new object[] { false, "Disconnected while awaiting write" });
        writer.Connected = true; update.Invoke(window, new object[] { true, "Another connection" });
        readback.Invoke(window, new object[] { (ushort)0x6448, (ushort)0 });
        writer.Pending.SetResult(true); writer.Pending = null;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (states["Music"]) throw new Exception("Old completion changed the next connection's mute state.");

        readback.Invoke(window, new object[] { (ushort)0x6400, (ushort)4096 });
        var queue = type.GetMethod("QueueCommand", hidden)!;
        var tick = type.GetMethod("OnSliderDebounceTick", hidden)!;
        int sentBeforeDrag = writer.Packets.Count;
        writer.Pending = new TaskCompletionSource<bool>();
        queue.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)3000, "Mic" });
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        queue.Invoke(window, new object[] { "MicVol", (byte)2, (ushort)0x6400, (ushort)3500, "Mic" });
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        if (writer.Packets.Count != sentBeforeDrag + 1) throw new Exception("Dragging queued concurrent slider transactions.");
        writer.Pending.SetResult(true); writer.Pending = null;
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        tick.Invoke(window, new object?[] { null, EventArgs.Empty });
        if (writer.Packets.Count != sentBeforeDrag + 2 || writer.Packets.Last()[6] != 0x0D || writer.Packets.Last()[7] != 0xAC)
            throw new Exception("Latest slider value was lost instead of coalesced.");

        foreach (string name in new[] { "BtnSaveSpeakerConfig", "BtnResetSpeakerConfig", "ChkBassBoost", "SliderMusicBass" })
            if (((FrameworkElement)window.FindName(name)).IsEnabled) throw new Exception("Unverified firmware control was enabled: " + name);

        var infoType = assembly.GetType("SoncaAudioInspector.BleDeviceInfo")!;
        object Info(ulong address, short rssi) => Activator.CreateInstance(infoType, address, "Sonca test", rssi, true, DateTime.Now)!;
        var discovered = type.GetMethod("OnDeviceDiscovered", hidden)!;
        discovered.Invoke(window, new[] { Info(1, -20) }); discovered.Invoke(window, new[] { Info(2, -60) });
        var combo = (ComboBox)window.FindName("CmbDevices");
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(i => (ulong)i.Tag == 2);
        discovered.Invoke(window, new[] { Info(3, -10) });
        if ((ulong)((ComboBoxItem)combo.SelectedItem).Tag != 2) throw new Exception("An advertisement changed the operator's selected device.");
        window.Close();
        Console.WriteLine("PASS BLE mixer: no initialization/offline write, readback required, failed mute unchanged, dedicated effect enable, Q12 volume, stale completion rejected, queue cleared and stable device selection.");
    }
}

public class MixerWriterProxy : DispatchProxy
{
    public bool Connected, Success;
    public TaskCompletionSource<bool>? Pending;
    public List<byte[]> Packets { get; } = new();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "get_IsConnected") return Connected;
        if (targetMethod?.Name == "get_SupportsMixerControls") return true;
        if (targetMethod?.Name == "SendPacketAsync") { Packets.Add(((byte[])args![0]!).ToArray()); return Pending?.Task ?? Task.FromResult(Success); }
        throw new NotSupportedException(targetMethod?.Name);
    }
}
