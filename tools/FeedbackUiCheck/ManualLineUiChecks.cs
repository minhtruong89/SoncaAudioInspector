using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

internal static class ManualLineUiChecks
{
    public static void Run(Assembly assembly, string appDirectory, string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "data"));
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var engineType = assembly.GetType("SoncaAudioInspector.AudioEngine")!;
        var runnerType = assembly.GetType("SoncaAudioInspector.TestRunner")!;
        var routingType = assembly.GetType("SoncaAudioInspector.AudioRouting")!;
        using var engine = (IDisposable)Activator.CreateInstance(engineType)!;
        var runner = Activator.CreateInstance(runnerType, engine)!;
        var noiseGate = runnerType.GetMethod("MustStopAtNoisePreflight", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Block(double rms, double peak, bool strict) => (bool)noiseGate.Invoke(null, new object[] { rms, peak, strict })!;
        if (Block(-45, .02, false) || !Block(-45, .02, true) || !Block(-80, .999, false)
            || !Block(double.NaN, .01, false) || !Block(-80, double.NaN, false) || Block(-80, .01, true))
            throw new Exception("Manual diagnostic/strict production noise preflight boundaries changed.");
        var routing = (UserControl)Activator.CreateInstance(routingType)!;
        routingType.GetField("_audioEngine", hidden)!.SetValue(routing, engine);
        routingType.GetField("_testRunner", hidden)!.SetValue(routing, runner);
        var verdict = routingType.GetMethod("SetFinalVerdict", hidden)!;
        var label = (TextBlock)routing.FindName("LblVerdict");
        if (!double.IsNaN((double)runnerType.GetProperty("LastMeasuredThdPercent")!.GetValue(runner)!)) throw new Exception("Unmeasured THD has a numeric value.");
        verdict.Invoke(routing, new object[] { false });
        if (label.Text == "KHÔNG ĐẠT") throw new Exception("No measurement became a product FAIL.");
        runnerType.GetProperty("LastMeasuredThdPercent")!.SetValue(runner, 0.0);
        runnerType.GetProperty("ThdAcquisitionInvalid")!.SetValue(runner, true);
        runnerType.GetProperty("LastFeqInvalidReason")!.SetValue(runner, "Noise preflight blocked before sweep");
        verdict.Invoke(routing, new object[] { false });
        if (label.Text == "KHÔNG ĐẠT" || !((TextBlock)routing.FindName("TxtFreqStatus")).Text.Contains("Noise preflight")) throw new Exception("Preflight failure hid its cause or became product FAIL.");
        runnerType.GetProperty("ThdAcquisitionInvalid")!.SetValue(runner, false);
        runnerType.GetProperty("LastFeqInvalidReason")!.SetValue(runner, "");
        var steps = ((System.Collections.IEnumerable)runnerType.GetProperty("Steps")!.GetValue(runner)!).Cast<object>().ToArray();
        steps[0].GetType().GetProperty("Status")!.SetValue(steps[0], "Pass");
        routingType.GetField("_freqs", hidden)!.SetValue(routing, new List<double> { 50, 1000, 18000 });
        runnerType.GetProperty("LastMeasuredThdPercent")!.SetValue(runner, 1.0);
        verdict.Invoke(routing, new object[] { false });
        if (label.Text != "KHÔNG ĐẠT") throw new Exception("Valid out-of-limit measurement no longer fails.");
        var summary = (FrameworkElement)routing.FindName("TxtMeasurementSummary");
        if (((FrameworkElement)summary.Parent).Visibility != Visibility.Collapsed || routingType.GetField("_measurementDetailsWindow", hidden)!.GetValue(routing) != null)
            throw new Exception("Measurement details show before the shortcut.");
        int opens = 0; Action action = () => opens++;
        var shortcut = routingType.GetMethod("TryHandleMeasurementShortcut", BindingFlags.Static | BindingFlags.NonPublic)!;
        shortcut.Invoke(null, new object[] { Key.I, ModifierKeys.Control | ModifierKeys.Shift, false, action });
        shortcut.Invoke(null, new object[] { Key.I, ModifierKeys.Control, false, action });
        shortcut.Invoke(null, new object[] { Key.I, ModifierKeys.Control | ModifierKeys.Shift, true, action });
        if (opens != 1) throw new Exception("Ctrl+Shift+I or repeat guard failed.");
        Console.WriteLine("PASS no data/noise/invalid acquisition never becomes product FAIL; real limit failure retained; details hidden and Ctrl+Shift+I only.");
        var mixer = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.SoncaBleMixerWindow")!, new object?[] { "TEST MODEL" })!;
        mixer.Close();
        var help = (Window)Activator.CreateInstance(assembly.GetType("SoncaAudioInspector.ShortcutsHelpWindow")!)!;
        help.Close();
        Console.WriteLine("PASS combined user BLE mixer and shortcut-help XAML initialization (no scanning or device commands).");
        string oldDirectory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(output, "empty"));
        try
        {
            Directory.SetCurrentDirectory(Path.Combine(output, "empty"));
            var method = assembly.GetType("SoncaAudioInspector.MicrophoneCalibration")!.GetMethod("ForKtUsbDevice")!;
            var profile = method.Invoke(null, new object[] { "Microphone (KT USB Audio)" }) ?? throw new Exception("KT calibration unavailable away from source/Downloads.");
            string path = (string)profile.GetType().GetProperty("FilePath")!.GetValue(profile)!;
            if (!Path.GetFullPath(path).Equals(Path.Combine(appDirectory, "calibrations", "99-00192.txt"), StringComparison.OrdinalIgnoreCase)) throw new Exception("KT calibration came from a machine-local path.");
            if (method.Invoke(null, new object[] { "Analog Connector (FastTrack Pro)" }) != null) throw new Exception("Electrical loopback got microphone correction.");
            Console.WriteLine("PASS bundled KT 99-00192 calibration loads from clean CWD; no microphone correction on FastTrack.");
        }
        finally { Directory.SetCurrentDirectory(oldDirectory); }
    }
}
