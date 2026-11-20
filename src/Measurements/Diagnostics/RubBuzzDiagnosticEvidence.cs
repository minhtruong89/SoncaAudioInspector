using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record ChirpImpulsiveEvidence(bool IsSensitive, double? ConsensusCrestDb, double? WorstFrequencyHz,
    bool RepeatableImpulsive, double? CrestThresholdDb = 13);
public sealed record BroadbandDistortionEvidence(bool IsSensitive, double? HighNlRelativeDb, double? MidNlRelativeDb,
    double? HighNlKurtosis, double? HighBandSnrDb);
public sealed record PinkDiagnosticRepeat(int Repeat, double EventsPct, double? EventsAtDrivePeaksPct,
    double HfDbFs, double DriveDbFs, double QuietHfExcessDb, double LoudHfExcessDb, double Kurtosis,
    double? TailReturnMilliseconds, double AlignmentCorrelation);
public sealed record PinkDiagnosticEvidence(PinkDiagnosticRepeat[] Repeats, double BackgroundHfDbFs)
{
    public double DriveSpreadDb => Repeats.Length == 0 ? 0 : Repeats.Max(x => x.DriveDbFs) - Repeats.Min(x => x.DriveDbFs);
    public double QuietLoudDifferenceDb => Repeats.Length == 0 ? 0 : Repeats.Average(x => x.LoudHfExcessDb - x.QuietHfExcessDb);
}
public sealed record ChirpDiagnosticEvidence(double? NarrowFrequencyHz, double? NarrowBumpDb, double? NarrowWidthOctaves,
    double BroadStartHz, double BroadEndHz, double BroadSpanOctaves, double BroadMinExcessDb);
public sealed record RubBuzzLevelRow(double StimulusRmsDbFs, double? BassDbFs, double? HfExcessDb, double? SpeakerHfDbFs,
    double? HfBassDb, double? RepeatCoherencePct, double? EventsPct, double[] RepeatEventsPct)
{
    public bool IsUsable { get; init; } = true;
    public string? ValidityReason { get; init; }
}
public sealed record RubBuzzLevelStep(double FromLevelDbFs, double ToLevelDbFs, double ExpectedGainDb, double BassGainDb,
    double? HfSlopeDbPerDb, bool Compressed);
public sealed record RubBuzzLevelDiagnostics(RubBuzzLevelRow[] Rows, RubBuzzLevelStep[] Steps, double? SpeakerHfSlopeDbPerDb,
    double? OnsetLevelDbFs, double? LimiterKneeLevelDbFs)
{
    [System.Text.Json.Serialization.JsonIgnore] public PinkRubBuzzRun[] Runs { get; init; } = [];
    /// <summary>All rows are diagnostic. This method never compares or persists station standards.</summary>
    public static RubBuzzLevelDiagnostics Analyze(IReadOnlyList<PinkRubBuzzRun> runs,
        RubBuzzDiagnosisSettings? settings = null)
    {
        settings ??= new();
        var rows = runs.OrderBy(r => r.Settings.LevelRmsDbFs).Select(r =>
        {
            string? invalid = RubBuzzDiagnosticEvidence.PinkAcquisitionProblem(r);
            if (invalid != null || !r.Metrics.IsValid)
                return new RubBuzzLevelRow(r.Settings.LevelRmsDbFs, null, null, null, null, null, null, [])
                    { IsUsable = false, ValidityReason = invalid ?? r.Metrics.Reason };
            var evidence = RubBuzzDiagnosticEvidence.AnalyzePink(r, settings);
            double excess = r.Metrics.HfDbFs - r.Metrics.BackgroundHfDbFs;
            double power = Math.Pow(10, r.Metrics.HfDbFs / 10) - Math.Pow(10, r.Metrics.BackgroundHfDbFs / 10);
            double? speaker = power > 0 ? 10 * Math.Log10(power) : null;
            return new RubBuzzLevelRow(r.Settings.LevelRmsDbFs, r.Metrics.DriveDbFs, excess, speaker,
                speaker - r.Metrics.DriveDbFs, r.Metrics.RepeatCoherencePct,
                evidence?.Repeats.Average(x => x.EventsPct) ?? 0, evidence?.Repeats.Select(x => x.EventsPct).ToArray() ?? []);
        }).ToArray();
        var steps = rows.Zip(rows.Skip(1), (a, b) => (a, b)).Where(p => p.a.IsUsable && p.b.IsUsable).Select(pair =>
        {
            var (a, b) = pair;
            double expected = b.StimulusRmsDbFs - a.StimulusRmsDbFs, bass = b.BassDbFs!.Value - a.BassDbFs!.Value;
            return new RubBuzzLevelStep(a.StimulusRmsDbFs, b.StimulusRmsDbFs, expected, bass,
                bass > .1 && a.SpeakerHfDbFs.HasValue && b.SpeakerHfDbFs.HasValue ? (b.SpeakerHfDbFs - a.SpeakerHfDbFs) / bass : null,
                expected > 0 && bass < settings.CompressionGainFraction * expected);
        }).ToArray();
        var finite = rows.Where(r => r.IsUsable && r.BassDbFs.HasValue && r.SpeakerHfDbFs.HasValue).ToArray();
        double? slope = null;
        if (finite.Length >= 2)
        {
            double x = finite.Average(r => r.BassDbFs!.Value), y = finite.Average(r => r.SpeakerHfDbFs!.Value);
            double denominator = finite.Sum(r => Math.Pow(r.BassDbFs!.Value - x, 2));
            if (denominator > .01) slope = finite.Sum(r => (r.BassDbFs!.Value - x) * (r.SpeakerHfDbFs!.Value - y)) / denominator;
        }
        double? onset = rows.FirstOrDefault(r => r.RepeatCoherencePct >= settings.LevelOnsetRcPct)?.StimulusRmsDbFs;
        double? knee = steps.FirstOrDefault(s => s.Compressed)?.FromLevelDbFs;
        return new(rows, steps, slope, onset, knee) { Runs = runs.ToArray() };
    }
}

/// <summary>Independent, additive waveform observations. Legacy Pink/Chirp numbers and verdicts are never modified.</summary>
public static class RubBuzzDiagnosticEvidence
{
    public static string? PinkAcquisitionProblem(PinkRubBuzzRun run)
    {
        if (run.SampleRate is < 32000 or > 192000) return "Sample rate thu ngoài dải chẩn đoán 32–192 kHz.";
        if (run.Captures.Length < 2 || run.Captures.Any(c => c.Length < 3 * run.SampleRate)) return "Thiếu lượt hoặc thời lượng thu Pink.";
        return WaveformProblem(run.Background, run.Captures, run.SampleRate);
    }
    public static string? ChirpAcquisitionProblem(ChirpRubBuzzRun run)
    {
        if (run.SampleRate is < 32000 or > 192000) return "Sample rate thu ngoài dải chẩn đoán 32–192 kHz.";
        if (run.Captures.Length < 2 || run.Captures.Any(c => c.Length < run.Settings.DurationSeconds * run.SampleRate)) return "Thiếu lượt hoặc thời lượng thu Chirp.";
        return WaveformProblem(run.Background, run.Captures, run.SampleRate);
    }
    private static string? WaveformProblem(float[] background, float[][] captures, int rate)
    {
        if (background.Length < 1.3 * rate) return "Thiếu nền thu ≥1,3 giây.";
        if (captures.Prepend(background).Any(c => c.Any(v => !float.IsFinite(v)))) return "Bản thu có NaN/Infinity.";
        if (captures.Prepend(background).Any(c => c.Any(v => Math.Abs(v) >= .98f))) return "Clipping ngõ thu; không suy luận nguyên nhân từ bản thu này.";
        if (captures.Any(c => c.All(v => Math.Abs(v) < 1e-10f))) return "Lượt thu gần im lặng số; chưa có tín hiệu âm học.";
        int skip = (int)(.3 * rate), count = background.Length - skip; double power = 0; int zero = 0;
        for (int i = skip; i < background.Length; i++) { power += (double)background[i] * background[i]; if (background[i] == 0) zero++; }
        if (Db(power / Math.Max(1, count)) < -100 || zero > .01 * count) return "Nền gần im lặng số hoặc bị gate; đường thu chưa đủ tin cậy.";
        return null;
    }
    private static double Db(double p) => 10 * Math.Log10(Math.Max(1e-30, p));
    private static double Power(double[] x, int start, int length)
    { double sum = 0; int end = Math.Min(x.Length, start + length); for (int i = Math.Max(0, start); i < end; i++) sum += x[i] * x[i]; return sum / Math.Max(1, end - Math.Max(0, start)); }
    private static double Kurtosis(double[] x, int start, int count)
    {
        double mean = 0; for (int i = start; i < start + count; i++) mean += x[i]; mean /= count;
        double p2 = 0, p4 = 0; for (int i = start; i < start + count; i++) { double d = x[i] - mean, p = d * d; p2 += p; p4 += p * p; }
        return p2 <= 0 ? 0 : count * p4 / (p2 * p2);
    }
    private static (int Lag, double Correlation) Align(double[] capture, double[] reference)
    {
        int n = 1; while (n < capture.Length + reference.Length) n <<= 1;
        var a = new Complex[n]; var b = new Complex[n];
        for (int i = 0; i < capture.Length; i++) a[i] = capture[i];
        for (int i = 0; i < reference.Length; i++) b[i] = reference[i];
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < n; i++) a[i] *= Complex.Conjugate(b[i]);
        Fourier.Inverse(a, FourierOptions.Matlab);
        int best = 0; double peak = double.NegativeInfinity;
        for (int i = 0; i < Math.Min(capture.Length, Math.Max(1, capture.Length - reference.Length / 2)); i++)
            if (Math.Abs(a[i].Real) > peak) { peak = Math.Abs(a[i].Real); best = i; }
        int length = Math.Min(reference.Length, capture.Length - best); double xy = 0, xx = 0, yy = 0;
        for (int i = 0; i < length; i++) { double x = capture[best + i], y = reference[i]; xy += x * y; xx += x * x; yy += y * y; }
        return (best, xx * yy <= 0 ? 0 : Math.Abs(xy) / Math.Sqrt(xx * yy));
    }
    private static float[] NominalStimulus(PinkRubBuzzRun run)
    {
        var original = PinkRubBuzzStimulus.Generate(run.Settings, run.PlaybackSampleRate);
        if (run.PlaybackSampleRate == run.SampleRate) return original;
        // S1 uses bass-only amplitude, so linear interpolation at >=32 kHz has negligible LF amplitude error.
        // This is not clock correction or waveform subtraction. Long-capture drift is reported as a limitation.
        var x = new float[(int)Math.Round(original.Length * (double)run.SampleRate / run.PlaybackSampleRate)];
        for (int i = 0; i < x.Length; i++)
        { double at = i * (double)run.PlaybackSampleRate / run.SampleRate; int p = Math.Min(original.Length - 1, (int)at); x[i] = (float)(original[p] + (original[Math.Min(p + 1, original.Length - 1)] - original[p]) * (at - p)); }
        return x;
    }

    public static PinkDiagnosticEvidence? AnalyzePink(PinkRubBuzzRun run, RubBuzzDiagnosisSettings? settings = null)
    {
        settings ??= new(); int rate = run.SampleRate;
        if (PinkAcquisitionProblem(run) != null) return null;
        int skip = Math.Min(run.Background.Length / 2, (int)(.3 * rate));
        double[] bg = PinkRubBuzzAnalysis.BandPass(run.Background, rate, 4000, 12000, 8);
        double bgPower = Power(bg, skip, bg.Length - skip);
        float[] stimulus = NominalStimulus(run);
        double[] reference = PinkRubBuzzAnalysis.BandPass(stimulus, rate, 25, 150, 4);
        double stimulusPeak = stimulus.Max(x => Math.Abs((double)x));
        int hop = Math.Max(1, rate / 1000), window = Math.Max(2, (int)Math.Round(.002 * rate)), frame = rate / 20;
        var repeats = new List<PinkDiagnosticRepeat>();
        for (int repeat = 0; repeat < run.Captures.Length; repeat++)
        {
            float[] capture = run.Captures[repeat];
            double[] lf = PinkRubBuzzAnalysis.BandPass(capture, rate, 25, 150, 4), hf = PinkRubBuzzAnalysis.BandPass(capture, rate, 4000, 12000, 8);
            var alignment = Align(lf, reference); int lag = alignment.Lag;
            int start = lag + (int)(.5 * rate), end = Math.Min(capture.Length, lag + stimulus.Length - (int)(.3 * rate));
            if (end - start < rate) continue;
            int events = 0, peakEvents = 0, count = 0;
            for (int i = start; i + window <= end; i += hop)
            {
                double amplitude = 0; int s = i - lag;
                for (int j = 0; j < window && s + j < stimulus.Length; j++) amplitude = Math.Max(amplitude, Math.Abs(stimulus[s + j]));
                if (Db(Power(hf, i, window) / Math.Max(1e-30, bgPower)) > settings.EventAboveRoomDb)
                { events++; if (amplitude > settings.DrivePeakFraction * stimulusPeak) peakEvents++; }
                count++;
            }
            var frames = new List<(double Lf, double Hf)>();
            for (int i = start; i + frame <= end; i += frame) frames.Add((Power(lf, i, frame), Db(Power(hf, i, frame) / Math.Max(1e-30, bgPower))));
            int decileCount = Math.Max(1, (int)Math.Ceiling(frames.Count * .1));
            var byLf = frames.OrderBy(f => f.Lf).ToArray();
            double? tail = null; int stop = lag + stimulus.Length;
            // Require two consecutive quiet 50-ms frames; one random low frame must not end a long tail.
            for (int i = stop; i + 2 * frame <= hf.Length; i += frame)
                if (Db(Power(hf, i, frame) / Math.Max(1e-30, bgPower)) <= settings.TailWithinRoomDb
                    && Db(Power(hf, i + frame, frame) / Math.Max(1e-30, bgPower)) <= settings.TailWithinRoomDb)
                { tail = 1000.0 * (i - stop + frame) / rate; break; }
            repeats.Add(new(repeat + 1, 100.0 * events / Math.Max(1, count), events == 0 ? null : 100.0 * peakEvents / events,
                Db(Power(hf, start, end - start)), Db(Power(lf, start, end - start)), byLf.Take(decileCount).Average(f => f.Hf),
                byLf.TakeLast(decileCount).Average(f => f.Hf), Kurtosis(hf, start, end - start), tail, alignment.Correlation));
        }
        return repeats.Count == 0 ? null : new(repeats.ToArray(), Db(bgPower));
    }

    public static ChirpDiagnosticEvidence? AnalyzeChirp(ChirpRubBuzzRun run, RubBuzzDiagnosisSettings? settings = null)
    {
        settings ??= new(); int rate = run.SampleRate; var m = run.Metrics;
        if (ChirpAcquisitionProblem(run) != null) return null;
        double? narrowFrequency = null, narrowBump = null, width = null;
        if (m.FrequenciesHz.Length == m.BumpDb.Length && m.BumpDb.Length > 0)
        {
            int peak = Array.IndexOf(m.BumpDb, m.BumpDb.Max()), left = peak, right = peak;
            if (m.BumpDb[peak] >= settings.NarrowBumpDb)
            {
                while (left > 0 && m.BumpDb[left - 1] >= settings.NarrowBumpDb) left--;
                while (right + 1 < m.BumpDb.Length && m.BumpDb[right + 1] >= settings.NarrowBumpDb) right++;
                width = Math.Log2(m.FrequenciesHz[right] / m.FrequenciesHz[left]) + 1 / 12.0;
                if (width <= settings.NarrowWidthOctaves + 1e-9) { narrowFrequency = m.FrequenciesHz[peak]; narrowBump = m.BumpDb[peak]; }
            }
        }
        if (run.Captures.Length == 0 || run.Background.Length < rate || m.CaptureStartSeconds.Length != run.Captures.Length)
            return narrowFrequency.HasValue ? new(narrowFrequency, narrowBump, width, 0, 0, 0, 0) : null;
        double[] bg = PinkRubBuzzAnalysis.BandPass(run.Background, rate, 4000, 12000, 8);
        double floor = Power(bg, (int)(.3 * rate), bg.Length - (int)(.3 * rate));
        double k = Math.Log(run.Settings.F2Hz / run.Settings.F1Hz), duration = run.Settings.DurationSeconds;
        var grid = new List<double>(); for (double f = run.Settings.F1Hz * 1.15; f < run.Settings.F2Hz; f *= Math.Pow(2, 1 / 12.0)) grid.Add(f);
        var values = new double[run.Captures.Length][];
        for (int repeat = 0; repeat < values.Length; repeat++)
        {
            double[] hf = PinkRubBuzzAnalysis.BandPass(run.Captures[repeat], rate, 4000, 12000, 8);
            values[repeat] = new double[grid.Count];
            for (int g = 0; g < grid.Count; g++)
            {
                double center = duration * Math.Log(grid[g] / run.Settings.F1Hz) / k;
                double half = duration * Math.Log(2) / (24 * k);
                int start = (int)Math.Round((m.CaptureStartSeconds[repeat] + Math.Max(.05, center - half)) * rate);
                int end = (int)Math.Round((m.CaptureStartSeconds[repeat] + Math.Min(duration - .05, center + half)) * rate);
                values[repeat][g] = end > start && end <= hf.Length ? Db(Power(hf, start, end - start) / Math.Max(1e-30, floor)) : double.NegativeInfinity;
            }
        }
        double[] consensus = Enumerable.Range(0, grid.Count).Select(i => values.Min(v => v[i])).ToArray();
        int bestStart = 0, bestEnd = -1, spanStart = -1;
        for (int i = 0; i <= grid.Count; i++)
        {
            if (i < grid.Count && consensus[i] > settings.BroadAboveRoomDb) { if (spanStart < 0) spanStart = i; }
            else if (spanStart >= 0)
            { if (i - spanStart > bestEnd - bestStart + 1) { bestStart = spanStart; bestEnd = i - 1; } spanStart = -1; }
        }
        return new(narrowFrequency, narrowBump, width, bestEnd >= bestStart ? grid[bestStart] : 0,
            bestEnd >= bestStart ? grid[bestEnd] : 0, bestEnd >= bestStart ? (bestEnd - bestStart + 1) / 12.0 : 0,
            bestEnd >= bestStart ? consensus[bestStart..(bestEnd + 1)].Min() : 0);
    }
}
