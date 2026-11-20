using System.Text.Json;
using System.Text.Json.Serialization;
using SoncaAudioInspector;

internal static class SignalDiagnosticChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        void Check(bool good, string message) { if (!good) throw new Exception(message); }
        void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid recipe accepted."); }
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, WriteIndented = true };
        void Save(string file, object data) => File.WriteAllText(Path.Combine(output, file + ".json"), JsonSerializer.Serialize(data, options));
        foreach (int rate in new[] { 44100, 48000 })
        {
            var request = new SignalDiagnosticRequest(SignalDiagnosticKind.Imd);
            var background = Enumerable.Range(0, rate / 2).Select(i => (float)(1e-7 * Math.Sin(i * .39))).ToArray();
            float[] capture = SignalDiagnostics.Generate(request, rate);
            double max = capture.Max(v => Math.Abs((double)v));
            Check(max <= Math.Pow(10, request.LevelDbFs / 20) + 1e-8 && capture[0] == 0 && capture[^1] == 0, "Stimulus exceeds peak ceiling or lacks fades.");
            var clean = SignalDiagnostics.AnalyzeImd(capture, background, rate, request);
            Console.WriteLine($"{rate} Hz clean IMD = {clean.Value:0.000}% [{clean.Status}]");
            Check(clean.IsValid && clean.Value < .15, "Linear dual tone falsely fails IMD.");
            // Independently inject 10% AM on the high carrier: two sidebands of
            // 5% each, so RSS = sqrt(2)*5 = 7.071...%, relative to that carrier.
            var am = new float[rate * 2];
            for (int i = 0; i < am.Length; i++)
            {
                double t = i / (double)rate;
                am[i] = (float)(.08 * Math.Sin(2 * Math.PI * 60 * t)
                    + .02 * (1 + .1 * Math.Cos(2 * Math.PI * 60 * t)) * Math.Sin(2 * Math.PI * 7000 * t));
            }
            var distorted = SignalDiagnostics.AnalyzeImd(am, background, rate, request);
            Console.WriteLine($"{rate} Hz injected AM IMD = {distorted.Value:0.000}% (expected 7.071%)");
            Check(distorted.IsValid && Math.Abs(distorted.Value - Math.Sqrt(2) * 5) < .02 && distorted.Status.Contains("VƯỢT"), "AM sideband RSS or threshold is wrong.");
            Save("imd-" + rate, distorted);
            var shifted = new float[am.Length];
            for (int i = 0; i < shifted.Length; i++)
            {
                double t = i / (double)rate;
                shifted[i] = (float)(.08 * Math.Sin(2 * Math.PI * 60.4 * t)
                    + .02 * (1 + .1 * Math.Cos(2 * Math.PI * 60.4 * t)) * Math.Sin(2 * Math.PI * 6997.25 * t));
            }
            var offBin = SignalDiagnostics.AnalyzeImd(shifted, background, rate, request);
            Check(offBin.IsValid && Math.Abs(offBin.Value - Math.Sqrt(2) * 5) < .02, "Off-bin carrier positions biased integrated IMD.");
            var single = Enumerable.Range(0, rate * 2).Select(i => (float)(.1 * Math.Sin(2 * Math.PI * 7000 * i / rate))).ToArray();
            Check(!SignalDiagnostics.AnalyzeImd(single, background, rate, request).IsValid, "Single carrier became valid IMD.");
            Check(!SignalDiagnostics.AnalyzeImd(new float[rate * 2], background, rate, request).IsValid, "Silence became valid IMD.");
            var random = new Random(19);
            var noiseOnly = Enumerable.Range(0, rate * 2).Select(_ => (float)((random.NextDouble() - .5) * .1)).ToArray();
            Check(!SignalDiagnostics.AnalyzeImd(noiseOnly, background, rate, request).IsValid, "Unrelated noise became a dual-tone result.");
            var insufficientSnr = Enumerable.Range(0, rate / 2).Select(i => (float)(.0002 * Math.Sin(i * .39))).ToArray();
            Check(!SignalDiagnostics.AnalyzeImd(capture, insufficientSnr, rate, request).IsValid, "IMD gate ignored the noise resolution needed for its limit.");
            var corrupt = (float[])capture.Clone(); corrupt[10] = float.NaN;
            Check(!SignalDiagnostics.AnalyzeImd(corrupt, background, rate, request).IsValid, "Nonfinite IMD capture accepted.");
            corrupt[10] = 1;
            Check(!SignalDiagnostics.AnalyzeImd(corrupt, background, rate, request).IsValid, "Clipped IMD capture accepted.");
            Check(!SignalDiagnostics.AnalyzeImd(capture[..100], background, rate, request).IsValid, "Short IMD capture accepted.");
            var noisyBackground = Enumerable.Repeat(.02f, rate / 2).ToArray();
            Check(!SignalDiagnostics.AnalyzeImd(capture, noisyBackground, rate, request).IsValid, "High background accepted.");

            var comp = new SignalDiagnosticRequest(SignalDiagnosticKind.Compression);
            var lowerSamples = SignalDiagnostics.Generate(comp, rate);
            var upperSamples = SignalDiagnostics.Generate(comp, rate, upperLevel: true);
            var lower = SignalDiagnostics.MeasureTone(lowerSamples, background, rate, comp);
            var upper = SignalDiagnostics.MeasureTone(upperSamples, background, rate, comp);
            var linear = SignalDiagnostics.Compression(lower, upper, comp);
            Check(linear.IsValid && Math.Abs(linear.Value) < .02, "Linear 6 dB gain became compression.");
            var attenuated = upperSamples.Select(v => (float)(v * Math.Pow(10, -3.0 / 20))).ToArray();
            var compressed = SignalDiagnostics.Compression(lower, SignalDiagnostics.MeasureTone(attenuated, background, rate, comp), comp);
            Console.WriteLine($"{rate} Hz injected compression = {compressed.Value:0.000} dB (expected 3 dB)");
            Check(compressed.IsValid && Math.Abs(compressed.Value - 3) < .02 && compressed.Status.Contains("CÓ NÉN"), "3 dB compression was lost.");
            Save("compression-" + rate, compressed);
            var delayed = new float[lowerSamples.Length + rate / 4]; lowerSamples.CopyTo(delayed, rate / 8);
            var delayedTone = SignalDiagnostics.MeasureTone(delayed, background, rate, comp);
            Check(delayedTone.IsValid && Math.Abs(delayedTone.LevelDbFs - lower.LevelDbFs) < .02, "Driver lead/tail biased the steady tone level.");
            Check(!SignalDiagnostics.MeasureTone(single, background, rate, comp).IsValid, "Wrong compression frequency accepted.");
            var missing = SignalDiagnostics.MeasureTone(new float[rate * 2], background, rate, comp);
            Check(!missing.IsValid && !SignalDiagnostics.Compression(lower, missing, comp).IsValid, "Missing upper level became a valid result.");
            upperSamples[0] = 1;
            Check(!SignalDiagnostics.MeasureTone(upperSamples, background, rate, comp).IsValid, "Clipped compression capture accepted.");
            Reject(() => (comp with { LevelDbFs = -6, IncreaseDb = 6 }).Validate(rate));
            Reject(() => (request with { HighToneHz = rate / 2.0 }).Validate(rate));
            Reject(() => (request with { LowToneHz = 20, HighToneHz = 18000 }).Validate(rate));
            Reject(() => (request with { Limit = double.NaN }).Validate(rate));
            Check(!SignalDiagnostics.QuietBackground(new float[2], rate), "Truncated background passed preflight.");
            // Rè background (3 s): a stream-start click inside the skipped 0.3 s must not refuse an otherwise quiet floor,
            // but the default gate (other diagnostics) keeps rejecting it, and a click after the skip still refuses.
            var floor = Enumerable.Range(0, rate * 3).Select(i => (float)(3e-4 * Math.Sin(i * .71))).ToArray();
            Check(SignalDiagnostics.CheckBackground(floor, rate, .3).IsQuiet && SignalDiagnostics.QuietBackground(floor, rate), "Quiet floor refused.");
            var onsetClick = (float[])floor.Clone(); onsetClick[rate / 20] = .3f;
            Check(!SignalDiagnostics.QuietBackground(onsetClick, rate), "Default gate stopped rejecting a click.");
            Check(SignalDiagnostics.CheckBackground(onsetClick, rate, .3).IsQuiet, "Stream-start click inside the skipped span refused the background.");
            var lateClick = (float[])floor.Clone(); lateClick[rate] = .3f;
            var late = SignalDiagnostics.CheckBackground(lateClick, rate, .3);
            Check(!late.IsQuiet && late.Reason.Contains("đỉnh") && late.Reason.Contains("xung"), "Click after the skip passed or lost its reason: " + late.Reason);
            var noisy = floor.Select(v => v * 30).ToArray(); // ≈ −40 dBFS RMS
            var loud = SignalDiagnostics.CheckBackground(noisy, rate, .3);
            Check(!loud.IsQuiet && loud.RmsDbFs > -45 && loud.Reason.Contains("RMS -4"), "Noisy floor passed or lost RMS: " + loud.Reason);
            var offset = floor.Select(v => v + .01f).ToArray(); // −40 dBFS DC
            var dc = SignalDiagnostics.CheckBackground(offset, rate, .3);
            Check(!dc.IsQuiet && dc.Reason.Contains("lệch DC"), "DC-dominated floor lost its DC hint: " + dc.Reason);
        }
        // Real analyzer produces the sweep metadata; classification fixtures
        // exercise every reference/acquisition gate independently of the view.
        var settings = new LogSweepSettings(SampleRate: 48000, StartFrequencyHz: 30, EndFrequencyHz: 18000,
            DurationSeconds: 1, Amplitude: .2, DecayAnalysisSeconds: 1.2);
        var sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var recorded = new float[sweep.Length + 2400]; sweep.CopyTo(recorded, 1200);
        var measured = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, recorded, settings);
        Check(SweepPolarityReference.Reliable(measured), "Valid fixture cannot provide a polarity reference.");
        var reference = SweepPolarityReference.From(measured, "route-a")!;
        Check(PolarityDiagnosticResult.Compare(measured, "route-a", reference).IsValid, "Same-route reference did not match.");
        var opposite = measured with { Polarity = measured.Polarity == "NORMAL" ? "INVERTED" : "NORMAL" };
        var inverted = PolarityDiagnosticResult.Compare(opposite, "route-a", reference);
        Check(inverted.IsValid && inverted.Status.Contains("KHÁC"), "Reversed polarity did not differ from golden.");
        Check(!PolarityDiagnosticResult.Compare(measured, "route-b", reference).IsValid
            && !PolarityDiagnosticResult.Compare(measured, "route-a", null).IsValid
            && !PolarityDiagnosticResult.Compare(measured, "route-a", reference with { SampleRate = 44100 }).IsValid, "Missing/stale route/rate reference produced a verdict.");
        Check(!PolarityDiagnosticResult.Compare(measured with { IsClipped = true }, "route-a", reference).IsValid
            && !PolarityDiagnosticResult.Compare(measured with { EstimatedSnrDb = 5 }, "route-a", reference).IsValid
            && !PolarityDiagnosticResult.Compare(measured with { HasPlausibleDirectArrival = false }, "route-a", reference).IsValid,
            "Unreliable sweep produced a polarity verdict.");
        Check(SweepPolarityReference.Combine(reference, reference) == reference
            && SweepPolarityReference.Combine(reference, reference with { Polarity = opposite.Polarity }) == null
            && SweepPolarityReference.Combine(reference, reference with { ContextKey = "route-b" }) == null,
            "Conflicting reference runs were silently combined.");
        Save("polarity-sweep", measured); Save("polarity", inverted);
        Check(JsonSerializer.Deserialize<SweepPolarityReference>(JsonSerializer.Serialize(reference)) == reference, "Polarity reference did not round trip.");
        Console.WriteLine("PASS IMD AM/clean/missing/clipped/noise/short, compression linear/3dB/wrong/missing/latency, peak limits and sweep polarity golden/context/quality/combination/persistence at 44.1/48 kHz.");
    }
}
