using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;
using SoncaAudioInspector;

internal static class ImpulseEffectsChecks
{
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        foreach (int rate in new[] { 44100, 48000 })
        foreach (var state in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            double[] ir = Impulse(rate, state.Item1, state.Item2);
            var config = new ImpulseEffectSettings { ExpectEcho = state.Item1, ExpectReverb = state.Item2, ExpectedEchoDelayMs = 200 };
            var result = ImpulseEffects.Analyze(ir, rate, 128, true, config);
            Console.WriteLine($"{rate} Hz {state}: echo={result.EchoDetected}, reverb={result.ReverbDetected}, delay={result.EchoDelayMs:F3}, occupancy={result.TailOccupancy:F3}, wet T60={result.TailDecay60Seconds:F3}, {result.Verdict}");
            Require(result.IsValid && result.EchoDetected == state.Item1 && result.ReverbDetected == state.Item2 && result.Verdict == "PASS", "Four effect states were not separated.");
            if (state.Item1) Require(Math.Abs(result.EchoDelayMs - 200) < 1, "Echo delay mismatch.");
            if (state.Item2) Require(result.TailDecay60Seconds > 2.5 && result.TailDecay60Seconds < 3.5, "Wet-tail decay mismatch.");
            var quiet = ImpulseEffects.Analyze(ir.Select(v => v * .1).ToArray(), rate, 128, true, config);
            Require(quiet.EchoDetected == result.EchoDetected && quiet.ReverbDetected == result.ReverbDetected && Math.Abs(quiet.LateEnergyDb - result.LateEnergyDb) < 1e-7, "Effect ratios changed with overall gain.");
        }
        var dry = Impulse(48000, false, false);
        var missing = ImpulseEffects.Analyze(dry, 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true });
        Require(missing.Verdict == "FAIL" && missing.Findings.Any(f => f.StartsWith("ECHO_MISSING")) && missing.Findings.Any(f => f.StartsWith("REVERB_MISSING")), "Missing-effect requirements ignored.");
        Require(ImpulseEffects.Analyze(dry, 48000, 128, true).Verdict == "OBSERVED", "Unconfigured effects became an acceptance verdict.");
        var baselineSettings = new ImpulseEffectSettings();
        var baseline = ImpulseEffectReference.From(ImpulseEffects.Analyze(Impulse(48000, true, true), 48000, 128, true), baselineSettings)!;
        Require(baseline.Compare(ImpulseEffects.Analyze(dry, 48000, 128, true), baselineSettings).Count(p => p.Contains("MISSING_REFERENCE")) == 2,
            "Saved golden effect baseline did not expose missing echo and reverb.");
        Require(!ImpulseEffects.Analyze(dry, 48000, 128, false).IsValid, "Invalid capture produced an effect verdict.");
        Require(ImpulseEffects.Analyze(dry.Take(24000).ToArray(), 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true }).Verdict == "INCONCLUSIVE", "Short capture was called missing effects.");
        var noise = new Random(12); var noisy = dry.Select(v => v + (noise.NextDouble() - .5)).ToArray();
        Require(!ImpulseEffects.Analyze(noisy, 48000, 128, true).IsValid, "Noisy IR allowed absence diagnosis.");
        var moderateNoise = dry.Select(v => v + .01 * (noise.NextDouble() - .5)).ToArray();
        var uncertain = ImpulseEffects.Analyze(moderateNoise, 48000, 128, true, new() { ExpectEcho = true, ExpectReverb = true });
        Require(uncertain.Verdict == "INCONCLUSIVE" && !uncertain.TailTruncated
            && uncertain.Findings.All(f => !f.Contains("MISSING")), "IR noise was mislabeled as missing effects or a truncated tail.");
        Console.WriteLine("PASS invalid/noisy/short IR, missing requirements, gain invariance and no unconfigured product verdict.");

        var source = Enumerable.Range(0, 2200).ToDictionary(i => 20 * Math.Pow(2, i / 192.0), _ => -20.0);
        source[1000] = 0;
        var centers = Enumerable.Range(0, 205).Select(i => 50 * Math.Pow(2, i / 24.0)).Where(f => f <= 18000).Append(1000).Append(18000).ToArray();
        var smooth = ResponseSmoothing.Octave24(source, centers);
        Require(source[1000] == 0 && smooth[1000] > -20 && smooth[1000] < 0, "Raw curve lost its unsmoothed peak or smoothing is not 1/24 averaging.");
        var quieter = ResponseSmoothing.Octave24(source.ToDictionary(p => p.Key, p => p.Value - 6.020599913), centers);
        Require(smooth.All(p => Math.Abs(quieter[p.Key] - p.Value + 6.020599913) < 1e-8), "Smoothing normalized away level changes.");
        Console.WriteLine("PASS 1/24 power averaging, untouched source peak and retained -6.0206 dB offset.");

        const int fs = 48000;
        var settings = new LogSweepSettings(fs, 18, 22000, 131072.0 / fs, .2, 192, 3,
            EffectsSettings: new() { ExpectEcho = true, ExpectReverb = true, ExpectedEchoDelayMs = 200 });
        var sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        var impulse = Impulse(fs, true, true);
        var capture = Convolve(sweep, impulse).Select(v => v * .4f).ToArray();
        var measured = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings, measuredPreSweepNoiseFloorDbFs: -100);
        Console.WriteLine("End-to-end " + measured.ImpulseEffects?.Describe());
        Require(measured.Validity != "INVALID" && measured.ImpulseEffects is { IsValid: true, EchoDetected: true, ReverbDetected: true }, "Same-sweep end-to-end effect analysis failed.");
        var saved = measured with { AlignedTransferFunction = Array.Empty<Complex>(), HarmonicTransferFunctions = Array.Empty<Complex[]>() };
        File.WriteAllText(Path.Combine(output, "effect-example.json"), JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        File.WriteAllText(Path.Combine(output, "effect-config-example.json"), JsonSerializer.Serialize(settings.EffectsSettings, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS echo + reverb extracted from one deconvolved sweep, with UI fixture saved.");
    }

    private static double[] Impulse(int rate, bool echo, bool reverb)
    {
        var samples = new double[rate * 3 + 129]; samples[128] = 1;
        if (echo) for (int n = 1; n <= 10; n++) samples[128 + (int)Math.Round(rate * .2 * n)] += Math.Pow(.25, n);
        if (reverb)
        {
            var random = new Random(94);
            for (int i = 128 + rate / 50; i < samples.Length; i++)
                samples[i] += .03 * (random.NextDouble() - .5) * Math.Exp(-Math.Log(1000) * (i - 128) / (3.0 * rate));
        }
        return samples;
    }

    private static float[] Convolve(float[] signal, double[] impulse)
    {
        int size = 1; while (size < signal.Length + impulse.Length - 1) size *= 2;
        var a = new Complex[size]; var b = new Complex[size];
        for (int i = 0; i < signal.Length; i++) a[i] = signal[i];
        for (int i = 0; i < impulse.Length; i++) b[i] = impulse[i];
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < size; i++) a[i] *= b[i];
        Fourier.Inverse(a, FourierOptions.Matlab);
        return a.Take(signal.Length + impulse.Length - 1).Select(v => (float)v.Real).ToArray();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
