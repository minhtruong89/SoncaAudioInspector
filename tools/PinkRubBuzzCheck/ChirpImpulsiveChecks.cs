using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

static partial class Program
{
    static void ImpulsiveChecks(string output)
    {
        var s = new ChirpRubBuzzSettings(); int length = (int)(s.DurationSeconds * Rate);
        var alignment = new ChirpRubBuzzMetrics(true, "synthetic aligned") { CaptureStartSeconds = [0, 0, 0] };
        float[] Harmonic(int order, double amplitude)
        {
            var x = new float[length];
            for (int i = 0; i < length; i++)
            {
                double phase = 2 * Math.PI * s.F1Hz * s.DurationSeconds / Math.Log(s.F2Hz / s.F1Hz)
                    * (Math.Exp(i / (double)Rate / s.DurationSeconds * Math.Log(s.F2Hz / s.F1Hz)) - 1);
                double g = Math.Min(1, Math.Min(i, length - 1 - i) / (Rate * .05));
                x[i] = (float)(amplitude * Math.Sin(order * phase) * .5 * (1 - Math.Cos(Math.PI * g)));
            }
            return x;
        }
        var suppressions = new List<double>();
        for (int order = 1; order <= 9; order++)
        {
            double amplitude = order == 1 ? .1 : .1 * Math.Pow(10, (-30 - (order - 2) * 20.0 / 7) / 20);
            var input = Harmonic(order, amplitude);
            var residual = ChirpImpulsiveDistortion.Filter(input, Rate, s);
            int a = (int)(.12 * Rate), b = (int)(2.5 * Rate);
            double gain = 10 * Math.Log10(residual[a..b].Average(v => (double)v * v) / input[a..b].Average(v => (double)v * v));
            suppressions.Add(gain);
            Check(gain <= -80, $"tracking HP H{order}: {gain:0.0} dB (≥80 dB suppression; H2–9 −30…−50 dBc)");
        }
        var composite = Harmonic(1, .1);
        for (int order = 2; order <= 9; order++)
        {
            var h = Harmonic(order, .1 * Math.Pow(10, (-30 - (order - 2) * 20.0 / 7) / 20));
            for (int i = 0; i < length; i++) composite[i] += h[i];
        }
        var floor = Noise(length, 1e-6, 361);
        var noisy = composite.Zip(Noise(length, 1e-6, 362), (a, b) => a + b).ToArray();
        var filtered = ChirpImpulsiveDistortion.Filter(noisy, Rate, s);
        var filteredFloor = ChirpImpulsiveDistortion.Filter(floor, Rate, s);
        int first = (int)(.12 * Rate), last = (int)(2.5 * Rate);
        double residualVsFloor = 10 * Math.Log10(filtered[first..last].Average(v => (double)v * v) / filteredFloor[first..last].Average(v => (double)v * v));
        Check(Math.Abs(residualVsFloor) < 1, $"sweep + H2–H9 residual at noise floor ({residualVsFloor:+0.00;-0.00} dB)");
        var gaussian = Enumerable.Range(0, 3).Select(i => Noise(length, 1e-6, 370 + i)).ToArray();
        var noise = ChirpImpulsiveDistortion.Analyze(gaussian, floor, Rate, s, alignment);
        double noiseCrest = noise.Median!.CrestDb.Average();
        Check(noiseCrest is >= 10 and <= 12.5 && !noise.FlaggedRepeats.Any(n => n >= 2),
            $"Gaussian 1/12-oct crest {noiseCrest:0.00} dB (max median {noise.Median.CrestDb.Max():0.00}); 13 dB + background gate rejects noise");
        var captures = Enumerable.Range(0, 3).Select(i => composite.Zip(Noise(length, 1e-6, 380 + i), (a, b) => a + b).ToArray()).ToArray();
        double[] injectedFrequencies = [40, 100, 240];
        foreach (double f in injectedFrequencies)
        {
            int at = (int)Math.Round(s.DurationSeconds * Math.Log(f / s.F1Hz) / Math.Log(s.F2Hz / s.F1Hz) * Rate);
            // 1 ms Hann burst, 14 kHz carrier. Its passband peak is known, unlike a DC/rectangular click.
            for (int r = 0; r < 2; r++)
                for (int j = -24; j <= 24; j++) captures[r][at + j] += (float)(.01 * Math.Cos(2 * Math.PI * 14000 * j / Rate) * .5 * (1 + Math.Cos(Math.PI * j / 24)));
        }
        var watch = Stopwatch.StartNew();
        var clicks = ChirpImpulsiveDistortion.Analyze(captures, floor, Rate, s, alignment); watch.Stop();
        foreach (double f in injectedFrequencies)
        {
            int at = Enumerable.Range(0, clicks.FrequenciesHz.Length).MinBy(i => Math.Abs(Math.Log(clicks.FrequenciesHz[i] / f)));
            double error = clicks.RepeatMetrics[0].PeakDbFs[at] - (-40);
            int injectedAt = (int)Math.Round(s.DurationSeconds * Math.Log(f / s.F1Hz) / Math.Log(s.F2Hz / s.F1Hz) * Rate);
            int recoveredAt = Enumerable.Range(injectedAt - 24, 49).MaxBy(i => Math.Abs(clicks.Residuals[0][i]));
            Check(Math.Abs(Math.Log2(clicks.FrequenciesHz[at] / f)) <= 1.0 / 24
                && Math.Abs(error) <= 1 && Math.Abs(injectedAt - recoveredAt) <= 1 && clicks.FlaggedRepeats[at] == 2 && clicks.Median!.CrestDb[at] >= 12,
                $"1 ms click @{f:0} Hz → {clicks.FrequenciesHz[at]:0.0} Hz, peak error {error:+0.00;-0.00} dB, crest {clicks.Median!.CrestDb[at]:0.0} dB, 2/3 repeats");
        }
        Check(clicks.FrequenciesHz.Max() * clicks.Settings.HighPassMultiple <= .45 * Rate,
            "tracking analysis stops before N·f exceeds 0.45 sample rate");
        Reject(() => new ChirpImpulsiveSettings(HighPassMultiple: 4).Validate(), "tracking multiple below 5 rejected");
        Reject(() => new ChirpImpulsiveSettings(HighPassMultiple: 41).Validate(), "tracking multiple above 40 rejected");
        Check(ChirpImpulsiveDistortion.Analyze(captures.Select(c => c[..Rate]).ToArray(), floor, Rate, s, alignment).Median == null,
            "short capture returns no invented impulsive metrics");
        Check(ChirpImpulsiveDistortion.Analyze(captures, floor, Rate, s,
            alignment with { CaptureStartSeconds = [double.NaN, 0, 0] }).Median == null,
            "nonfinite lag returns no invented impulsive metrics");
        Check(ChirpRubBuzzAnalysis.Version == "chirp-rattle-1.0.0" && PinkRubBuzzAnalysis.Version == "pink-rattle-2.0.0",
            "additive diagnostics keep existing algorithm versions");
        File.WriteAllText(Path.Combine(output, "impulsive-synthetic.json"), JsonSerializer.Serialize(new
        {
            suppressionsDb = suppressions, residualVsFloorDb = residualVsFloor, noiseCrestMeanDb = noiseCrest,
            noiseCrestMaxMedianDb = noise.Median.CrestDb.Max(), analysisSeconds = watch.Elapsed.TotalSeconds, clicks
        }, new JsonSerializerOptions { WriteIndented = true }));

        // Optional bench artifacts: no cleanup-sensitive fixture dependency, no new captured audio.
        static bool HasWave(string path) => File.Exists(path) || File.Exists(path + ".f32.gz");
        var replay = new List<object>();
        void Replay(string name, string folder, string bgName, params string[] captureNames)
        {
            if (!HasWave(Path.Combine(folder, bgName)) || captureNames.Any(f => !HasWave(Path.Combine(folder, f))))
            { Log.Add("SKIP tracking replay " + name + ": optional recordings absent"); return; }
            var caps = captureNames.Select(f => ReadWav(Path.Combine(folder, f))).ToArray(); var bg = ReadWav(Path.Combine(folder, bgName));
            var run = ChirpRun(caps, bg, s with { Repeats = caps.Length });
            var timer = Stopwatch.StartNew(); var result = ChirpImpulsiveDistortion.Analyze(run); timer.Stop();
            Check(result.FrequenciesHz.Length > 20 && result.RepeatMetrics.Length == caps.Length,
                $"tracking replay {name}: crest max median {result.Median!.CrestDb.Max():0.00} dB; {result.Verdict}");
            replay.Add(new { name, legacyBumpDb = run.Metrics.PeakBumpDb, legacyPeakHz = run.Metrics.PeakBumpHz,
                analysisSeconds = timer.Elapsed.TotalSeconds, maxMedianCrestDb = result.Median.CrestDb.Max(),
                maxMinimumCrestDb = result.Minimum!.CrestDb.Max(), result });
        }
        const string review = "artifacts/20261114-064502-633Z-rubbuzz-remeasure-review/captures";
        Replay("14/11 13:38", Path.Combine(review, "rubbuzz-20261114-064123-716-6b5ec339"), "chirp-background.wav",
            "chirp-microphone-1.wav", "chirp-microphone-2.wav", "chirp-microphone-3.wav");
        Replay("14/11 13:41", Path.Combine(review, "rubbuzz-20261114-064316-122-285a1a95"), "chirp-background.wav",
            "chirp-microphone-1.wav", "chirp-microphone-2.wav", "chirp-microphone-3.wav");
        string chirp06Dir = HasWave("artifacts/20261106-085907-845Z-rattle-listening/chirp-3s/background.wav")
            ? "artifacts/20261106-085907-845Z-rattle-listening/chirp-3s"
            : "tools/PinkRubBuzzCheck/Fixtures/chirp/chirp-3s";
        Replay("06/11 rattler", chirp06Dir, "background.wav", "capture-1.wav", "capture-2.wav");
        File.WriteAllText(Path.Combine(output, "impulsive-replay.json"), JsonSerializer.Serialize(replay, new JsonSerializerOptions { WriteIndented = true }));
    }
}
