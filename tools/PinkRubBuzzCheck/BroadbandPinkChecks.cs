using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

static partial class Program
{
    static void BroadbandPinkChecks(string output)
    {
        var settings = new BroadbandPinkSettings();
        var stimulus = BroadbandPinkStimulus.Generate(settings, 44100);
        var info = BroadbandPinkStimulus.Describe(stimulus, 44100, settings);
        Check(stimulus.Take(settings.PeriodSamples).SequenceEqual(stimulus.Skip(settings.PeriodSamples)), "broadband pink: two bit-identical periods");
        Check(Math.Abs(info.RmsDbFs + 20) < .001 && info.PeakDbFs < -.2 && info.AchievedCrestDb <= 12.1,
            $"broadband pink: RMS {info.RmsDbFs:0.00}, crest {info.AchievedCrestDb:0.00} dB, peak {info.PeakDbFs:0.00} dBFS");
        var midBands = info.ThirdOctaveLevels.Where(b => b.FrequencyHz > 100 && b.FrequencyHz < 10000).Select(b => b.LevelDbFs).ToArray();
        Check(midBands.Max() - midBands.Min() < .3, "broadband pink: 1/3-octave levels flat within 0.3 dB (−3 dB/oct PSD)");
        var lowCrest = settings with { CrestTargetDb = 6 };
        var six = BroadbandPinkStimulus.Describe(BroadbandPinkStimulus.Generate(lowCrest, 44100), 44100, lowCrest);
        Check(six.AchievedCrestDb <= 6.2, $"broadband pink: 6 dB target achieved {six.AchievedCrestDb:0.00} dB");
        Reject(() => BroadbandPinkStimulus.Generate(settings with { LevelRmsDbFs = -18, CrestTargetDb = 19 }, 44100), "broadband pink invalid crest/headroom rejected");
        float[] reference = BroadbandPinkDistortion.ResampleSinc(stimulus, 44100.0 / Rate, (int)Math.Round(stimulus.Length * (double)Rate / 44100.0));
        float[] BackgroundNoise() => Noise(Rate * 3, 5e-5, 910);
        float[][] Captures(float[] source, bool drift)
        {
            var result = new float[3][];
            for (int r = 0; r < result.Length; r++)
            {
                // Independent synthetic 4-tap linear path, fractional delay, and ADC clock offset.
                var filtered = new float[source.Length];
                for (int i = 3; i < source.Length; i++) filtered[i] = .6f * source[i] + .2f * source[i - 1] - .08f * source[i - 3];
                double ppm = drift ? 50 + r * 3 : 0, ratio = 1 + ppm * 1e-6, delay = Rate / 5.0 + .19 + r * .17;
                int n = filtered.Length + Rate / 2;
                var mapped = BroadbandPinkDistortion.ResampleSinc(filtered, 1 / ratio, n, -delay / ratio);
                var noise = Noise(n, 5e-5, 920 + r);
                for (int i = 0; i < n; i++) mapped[i] += noise[i];
                result[r] = mapped;
            }
            return result;
        }
        var watch = Stopwatch.StartNew();
        var clean = BroadbandPinkDistortion.Analyze(Captures(reference, true), BackgroundNoise(), Rate, stimulus, 44100, settings);
        watch.Stop();
        File.WriteAllText(Path.Combine(output, "broadband-linear-probe.json"), JsonSerializer.Serialize(clean, new JsonSerializerOptions { WriteIndented = true }));
        Check(clean.IsValid, "broadband linear + delay + clock: valid " + clean.Reason);
        Check(clean.Alignment.All(a => Math.Abs(a.ClockOffsetPpm - (50 + (a.Repeat - 1) * 3)) < 3),
            "broadband 50 ppm ratio recovered: " + string.Join(" / ", clean.Alignment.Select(a => $"{a.ClockOffsetPpm:0.00} ppm")));
        var sensitive = clean.Bands.Where(b => b.SnrDb > 30).ToArray();
        Check(sensitive.Length > 10 && sensitive.All(b => b.NonlinearDbFs <= b.NoiseDbFs + 3),
            $"broadband linear NL ≤ noise +3 dB in {sensitive.Length} bands with SNR >30 dB; worst {sensitive.Select(b => b.NonlinearDbFs - b.NoiseDbFs).DefaultIfEmpty().Max():0.00} dB");
        Check(!BroadbandPinkDistortion.Analyze([], BackgroundNoise(), Rate, stimulus, 44100, settings).IsValid,
            "broadband missing repeats rejected");
        Check(!BroadbandPinkDistortion.Analyze([new float[100], new float[100]], BackgroundNoise(), Rate, stimulus, 44100, settings).IsValid,
            "broadband short repeats rejected");
        var adcClipped = Captures(reference, false); adcClipped[0][Rate] = .99f;
        var invalidClip = BroadbandPinkDistortion.Analyze(adcClipped, BackgroundNoise(), Rate, stimulus, 44100, settings);
        Check(!invalidClip.IsValid && invalidClip.Clipped && !invalidClip.ToDiagnosisEvidence().IsSensitive
            && invalidClip.Captures.Length == 3, "broadband ADC clipping invalid; no S10 vote; raw captures preserved");
        var invalidNumbers = Captures(reference, false); invalidNumbers[0][Rate] = float.NaN;
        Check(!BroadbandPinkDistortion.Analyze(invalidNumbers, BackgroundNoise(), Rate, stimulus, 44100, settings).IsValid,
            "broadband nonfinite rejected");
        Check(!BroadbandPinkDistortion.Analyze(Captures(reference, false), new float[Rate], Rate, stimulus, 44100, settings).IsValid,
            "broadband zero background rejected");
        var noiseOnly = Enumerable.Range(0, 3).Select(r => Noise(reference.Length + Rate / 2, .001, 960 + r)).ToArray();
        Check(!BroadbandPinkDistortion.Analyze(noiseOnly, BackgroundNoise(), Rate, stimulus, 44100, settings).IsValid,
            "broadband noise-only capture fails alignment validity");
        var warped = Captures(reference, false); var old = warped[1]; warped[1] = old.ToArray();
        double centerAt = Rate / 5.0 + reference.Length / 2.0;
        for (int i = 1; i < old.Length - 5; i++)
        {
            double position = i + 3 * Math.Exp(-Math.Pow((i - centerAt) / (Rate * .15), 2));
            int at = (int)position; double fraction = position - at;
            warped[1][i] = (float)(old[at] * (1 - fraction) + old[at + 1] * fraction);
        }
        var badClock = BroadbandPinkDistortion.Analyze(warped, BackgroundNoise(), Rate, stimulus, 44100, settings);
        Check(!badClock.IsValid && !badClock.ToDiagnosisEvidence().IsSensitive,
            "broadband nonlinear clock drift fails middle-window residual validity");
        var clipped = reference.Select(v => Math.Clamp(v, -.075f, .075f)).ToArray();
        var clip = BroadbandPinkDistortion.Analyze(Captures(clipped, false), BackgroundNoise(), Rate, stimulus, 44100, settings);
        Check(clip.HighFrequency!.NonlinearDbFs > clean.HighFrequency!.NonlinearDbFs + 12,
            $"broadband hard clip raises HF NL {clip.HighFrequency.NonlinearDbFs - clean.HighFrequency.NonlinearDbFs:0.0} dB");
        var clicked = reference.ToArray(); double threshold = .29;
        for (int i = 1; i + Rate / 1000 < clicked.Length; i++)
            if (Math.Abs(reference[i]) > threshold && Math.Abs(reference[i - 1]) <= threshold)
                for (int j = 0; j < Rate / 1000; j++) clicked[i + j] += (float)(.07 * Math.Sin(2 * Math.PI * 8000 * j / Rate) * Math.Exp(-j / 8.0));
        var click = BroadbandPinkDistortion.Analyze(Captures(clicked, false), BackgroundNoise(), Rate, stimulus, 44100, settings);
        Check(click.HighFrequency!.NonlinearDbFs > clean.HighFrequency!.NonlinearDbFs + 12 && click.HighFrequencyNlKurtosis > 6,
            $"broadband threshold clicks raise HF NL; kurtosis {click.HighFrequencyNlKurtosis:0.0} >6");
        var otherSettings = settings with { Seed = 1115 };
        var otherStimulus = BroadbandPinkStimulus.Generate(otherSettings, 44100);
        var otherReference = BroadbandPinkDistortion.ResampleSinc(otherStimulus, 44100.0 / Rate, (int)Math.Round(otherStimulus.Length * (double)Rate / 44100.0));
        var other = BroadbandPinkDistortion.Analyze(Captures(otherReference, true), BackgroundNoise(), Rate, otherStimulus, 44100, otherSettings);
        Check(other.IsValid && Math.Abs(other.HighFrequency!.NonlinearRelativeDb - clean.HighFrequency!.NonlinearRelativeDb) < 2,
            $"broadband alternate seed: identical linear system HF residual within2 dB ({clean.HighFrequency.NonlinearRelativeDb:0.0}/{other.HighFrequency!.NonlinearRelativeDb:0.0} dBr)");
        Check(watch.Elapsed.TotalSeconds < 5, $"broadband analysis {watch.Elapsed.TotalSeconds:0.000} s <5 s (one acquisition, 3 repeats)");
        File.WriteAllText(Path.Combine(output, "broadband-synthetic.json"), JsonSerializer.Serialize(new { analysisSeconds = watch.Elapsed.TotalSeconds, clean, clip, click, other }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
