using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoncaAudioInspector;

internal static class SweepPerformanceChecks
{
    public static void Run()
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        foreach (double seconds in new[] { 65536.0 / 44100, 3.0 })
        {
            var settings = new LogSweepSettings(44100, 18, 20000, seconds, .4);
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[sweep.Length + 4410];
            for (int index = 0; index < sweep.Length; index++) capture[index + 128] = sweep[index] * .25f;
            _ = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
            var timings = new List<double>(); var allocations = new List<long>(); string? hash = null;
            for (int run = 0; run < 5; run++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
                watch.Stop();
                allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocated);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                if (hash == null) hash = Digest(result, options);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { seconds, medianMs = timings.Order().ElementAt(2),
                medianAllocatedBytes = allocations.Order().ElementAt(2), fullResultSha256 = hash, timings }));
        }
        var checks = new LogSweepSettings(48000, 20, 20000, 1.5, .4);
        float[] stimulus = StandardAcousticMeasurement.GenerateLogSweep(checks);
        foreach (string scenario in new[] { "clipped", "clock-drift", "silent", "unrelated-noise" })
        {
            var capture = new float[stimulus.Length + 4800];
            var random = new Random(42);
            for (int index = 0; index < capture.Length; index++)
            {
                double position = (index - 128) * (scenario == "clock-drift" ? 1.0004 : 1);
                int first = (int)Math.Floor(position);
                double value = first >= 0 && first + 1 < stimulus.Length
                    ? stimulus[first] + (stimulus[first + 1] - stimulus[first]) * (position - first) : 0;
                capture[index] = scenario switch
                {
                    "clipped" => (float)Math.Clamp(value * 5, -1, 1),
                    "clock-drift" => (float)(value * .25),
                    "unrelated-noise" => (float)((random.NextDouble() - .5) * .1),
                    _ => 0
                };
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(stimulus, capture, checks);
            string hash = Digest(result, options);
            Console.WriteLine(JsonSerializer.Serialize(new { scenario, fullResultSha256 = hash,
                result.Validity, result.ClockDriftCorrectionApplied, result.EstimatedClockDriftPpm }));
        }
    }

    private static string Digest(StandardAcousticResult result, JsonSerializerOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Complex arrays dominate size. Hash their exact real/imaginary bits;
        // serialize every other result field, including all validity/diagnostic text.
        var rest = result with { AlignedTransferFunction = Array.Empty<Complex>(),
            HarmonicTransferFunctions = Array.Empty<Complex[]>() };
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(rest, options));
        void Add(Complex[]? values)
        {
            hash.AppendData(BitConverter.GetBytes(values?.Length ?? -1));
            if (values != null) hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        Add(result.AlignedTransferFunction);
        hash.AppendData(BitConverter.GetBytes(result.HarmonicTransferFunctions.Count));
        foreach (var values in result.HarmonicTransferFunctions) Add(values);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void CheckClockCorrection()
    {
        var settings = new LogSweepSettings(48000, 30, 18000, 1, .2, DecayAnalysisSeconds: 1.2);
        float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
        const double scale = 1.0004;
        int length = (int)Math.Ceiling(sweep.Length * scale);
        var capture = new float[length + 3600];
        double ratio = settings.EndFrequencyHz / settings.StartFrequencyHz;
        double logRatio = Math.Log(ratio);
        int cycles = Math.Max(1, (int)Math.Round(settings.StartFrequencyHz * settings.DurationSeconds / logRatio * (ratio - 1)));
        double phaseScale = 2 * Math.PI * cycles / (ratio - 1);
        double octaves = logRatio / Math.Log(2);
        int fadeIn = Math.Max(16, (int)Math.Round(settings.DurationSeconds / octaves * settings.SampleRate));
        int fadeOut = Math.Max(16, (int)Math.Round(settings.DurationSeconds / (12 * octaves) * settings.SampleRate));
        for (int index = 0; index < length; index++)
        {
            double position = index / scale;
            if (position >= sweep.Length) continue;
            double fade = position < fadeIn ? .5 - .5 * Math.Cos(Math.PI * position / fadeIn)
                : position >= sweep.Length - fadeOut ? .5 - .5 * Math.Cos(Math.PI * (sweep.Length - 1 - position) / fadeOut) : 1;
            capture[1200 + index] = (float)(settings.Amplitude * fade * Math.Sin(phaseScale * (Math.Pow(ratio, position / (sweep.Length - 1)) - 1)));
        }
        var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
        if (!result.ClockDriftCorrectionApplied) throw new InvalidOperationException("Clock test did not enter the correction branch.");
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        Console.WriteLine(JsonSerializer.Serialize(new { scenario = "clock-corrected", fullResultSha256 = Digest(result, options),
            result.ClockDriftCorrectionApplied, result.EstimatedClockDriftPpm }));
    }
}
