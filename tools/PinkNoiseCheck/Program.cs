using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;
using NAudio.Wave;

static class Program
{
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
    [STAThread]
    static void Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        foreach (int rate in new[] { 44100, 48000, 96000 })
        foreach (var band in new[] { (20d, 20000d), (40d, 200d), (500d, 2000d) })
        {
            var settings = new PinkNoiseSettings(band.Item1, band.Item2, -18);
            var samples = PinkNoiseSignal.CreatePeriod(settings, rate);
            double rms = Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Length);
            Check(Math.Abs(20 * Math.Log10(rms) + 18) < .0001, "RMS");
            Check(samples.Max(v => Math.Abs(v)) < .98, "headroom");
            Check(Math.Abs(samples.Average(v => (double)v)) < 1e-8, "DC");
            var spectrum = samples.Select(v => new Complex(v, 0)).ToArray(); Fourier.Forward(spectrum, FourierOptions.Matlab);
            var powers = new List<double>(); double outside = 0, inside = 0;
            for (int k = 1; k < samples.Length / 2; k++)
            {
                double hz = k * (double)rate / samples.Length, power = spectrum[k].Magnitude * spectrum[k].Magnitude;
                if (hz >= band.Item1 && hz <= band.Item2) { inside += power; powers.Add(power * hz); }
                else outside += power;
            }
            Check(10 * Math.Log10(outside / inside) < -120, "out of band");
            Check(10 * Math.Log10(powers.Max() / powers.Min()) < .001, "1/f PSD");
            var provider = new PinkNoiseLoopProvider(samples, rate, 2, 1);
            int frames = rate + samples.Length * 2;
            var actual = Enumerable.Repeat(99f, frames * 2 + 10).ToArray();
            provider.Read(actual, 5, frames * 2);
            Check(actual[0] == 99 && actual[^1] == 99, "offset guards");
            for (int i = rate; i < frames; i++)
                Check(actual[5 + i * 2] == 0 && actual[6 + i * 2] == samples[i % samples.Length], "channel/period seam");
            Console.WriteLine($"PASS {rate} Hz {band}: RMS=-18 dBFS, peak={samples.Max(v => Math.Abs(v)):F4}, PSD 1/f, no DC, channel/loop continuity");
        }
        foreach (var settings in new[] { new PinkNoiseSettings(LevelDbFs: 0), new PinkNoiseSettings(double.NaN, 20000), new PinkNoiseSettings(200, 40) })
        {
            try { PinkNoiseSignal.CreatePeriod(settings, 48000); throw new Exception("Invalid settings accepted"); }
            catch (ArgumentException) { }
        }
        foreach (var item in new[] { ("fullrange", 20d, 20000d), ("bass", 40d, 200d), ("speaker-level", 500d, 2000d) })
        {
            string path = Path.Combine(output, $"pink-noise-v1.1-{item.Item1}-44100Hz-minus18dBFS.wav");
            PinkNoiseSignal.SaveWave(path, new(item.Item2, item.Item3, -18));
            using var reader = new WaveFileReader(path);
            Check(reader.WaveFormat.BitsPerSample == 24 && reader.WaveFormat.SampleRate == 44100 && reader.WaveFormat.Channels == 1, "WAV format");
            var decoded = reader.ToSampleProvider(); var block = new float[4096]; long count = 0; double squares = 0;
            int read;
            while ((read = decoded.Read(block, 0, block.Length)) > 0)
            { for (int i = 0; i < read; i++) { Check(Math.Abs(block[i]) < .98, "WAV clipping"); squares += block[i] * (double)block[i]; } count += read; }
            Check(count == PinkNoiseSignal.PeriodSamples * 12 && Math.Abs(20 * Math.Log10(Math.Sqrt(squares / count)) + 18) < .001, "WAV duration/RMS");
            Console.WriteLine($"PASS decoded PCM24 WAV {item.Item1}: 35.666 seconds, -18 dBFS RMS, no clipping");
        }
        var randomSettings = new PinkNoiseSettings(30, 80, -18, true);
        string randomPath = Path.Combine(output, "pink-noise-v1.1-subwoofer-random-30-80Hz-44100Hz-minus18dBFS.wav");
        PinkNoiseSignal.SaveWave(randomPath, randomSettings);
        using (var reader = new WaveFileReader(randomPath))
        {
            Check(reader.WaveFormat.SampleRate == 44100 && reader.WaveFormat.BitsPerSample == 24, "random WAV format");
            var values = new float[PinkNoiseSignal.PeriodSamples * 12]; var source = reader.ToSampleProvider();
            int count = source.Read(values, 0, values.Length);
            Check(count == values.Length, "random WAV duration");
            Check(Math.Abs(20 * Math.Log10(Math.Sqrt(values.Sum(v => (double)v * v) / count)) + 18) < .001, "random exported RMS");
            Check(values.Max(v => Math.Abs(v)) < .98 && Math.Abs(values.Average(v => (double)v)) < .0001, "random headroom/DC");
            Check(!values.Take(PinkNoiseSignal.PeriodSamples).SequenceEqual(values.Skip(PinkNoiseSignal.PeriodSamples).Take(PinkNoiseSignal.PeriodSamples)), "random must not repeat PN");
            var spectrum = values.Take(1048576).Select(v => new Complex(v, 0)).ToArray(); Fourier.Forward(spectrum, FourierOptions.Matlab);
            double bass = 0, treble = 0;
            for (int k = 1; k < spectrum.Length / 2; k++)
            { double hz = k * 44100d / spectrum.Length, power = spectrum[k].Magnitude * spectrum[k].Magnitude; if (hz >= 30 && hz <= 80) bass += power; if (hz >= 300 && hz <= 10000) treble += power; }
            Check(10 * Math.Log10(bass / treble) > 25, "random subwoofer filtering");
        }
        var live = new PinkNoiseRandomProvider(randomSettings, 44100, 2, 0);
        var liveValues = new float[44100 * 30 * 2]; live.Read(liveValues, 0, liveValues.Length);
        Check(liveValues.Where((_, index) => index % 2 == 1).All(v => v == 0), "random live channel isolation");
        Check(Math.Abs(20 * Math.Log10(Math.Sqrt(liveValues.Sum(v => (double)v * v) / (liveValues.Length / 2))) + 18) < .1, "random live RMS");
        Console.WriteLine("PASS random subwoofer 30-80 Hz: PCM24/44100, RMS, DC/headroom, no PN repeat, bass filtering and live channel isolation");
        var app = new App(); app.InitializeComponent();
        typeof(Application).GetField("_startupUri", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var window = new MeasurementDetailsWindow();
        app.MainWindow = window;
        var layout = (DockPanel)window.Content;
        var tabs = layout.Children.OfType<TabControl>().Single();
        // Since 2026-11-17 the listening pink sits in Rè (Rub & Buzz) → Chẩn đoán nâng cao.
        var rattle = tabs.Items.OfType<TabItem>().Single(t => t.Header.ToString() == MeasurementDetailsWindow.RubBuzzTabHeader);
        var tools = (TabControl)window.RubBuzz.AdvancedTools.Children[0];
        var tab = tools.Items.OfType<TabItem>().Single(t => t.Header.ToString() == "Pink nghe thử · v1.1");
        Check(!tabs.Items.OfType<TabItem>().Any(t => t.Header.ToString()!.StartsWith("Pink noise")), "listening pink is no longer a separate top-level tab");
        Check(!window.IsDiagnosticRunning, "Opening tab must not start sound");
        tabs.SelectedItem = rattle; ((Expander)LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent(window.RubBuzz.AdvancedTools))).IsExpanded = true; tab.IsSelected = true;
        var view = (PinkNoiseView)((ScrollViewer)tab.Content).Content;
        Check(view.Settings() == new PinkNoiseSettings(30, 80, -24, true), "safe default");
        // Pink noise shipped in v1.1; the header shows whatever the assembly really is (a pinned "v1.1" broke at v1.2).
        var built = typeof(AppVersion).Assembly.GetName().Version!;
        Check(built >= new Version(1, 1) && AppVersion.Display == "v" + (built.Revision > 0 ? built.ToString(4) : built.Build > 0 ? built.ToString(3) : built.ToString(2)), "release version");
        window.WindowState = WindowState.Normal; window.Width = 1200; window.Height = 820;
        window.Show(); window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "pink-noise-ui.png"))) encoder.Save(file);
        var buttons = ((StackPanel)view.Content).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
        int calls = 0;
        window.PinkNoiseRunner = async (_, token) => { calls++; await Task.Delay(Timeout.Infinite, token); };
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.IsDiagnosticRunning && !buttons[0].IsEnabled && calls == 1, "run owns diagnostic lock");
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !window.IsDiagnosticRunning);
        Check(buttons[0].IsEnabled, "stop releases controls");
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Close();
        PumpUntil(() => !window.IsDiagnosticRunning);
        Check(calls == 2, "close cancels playback");
        Console.WriteLine("PASS WPF tab renders, separate controls, no autoplay, WAV export; no physical playback tested.");
    }
    static void PumpUntil(Func<bool> ready)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame); timer.Stop(); Check(ready(), "UI cancellation timeout");
    }
}
