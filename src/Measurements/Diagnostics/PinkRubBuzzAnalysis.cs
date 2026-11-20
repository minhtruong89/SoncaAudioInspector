using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace SoncaAudioInspector;

/// <summary>pink-rattle-2.0: band-limited pink 25–150 Hz played N times with the identical waveform.
/// Mechanical rattle/buzz (e.g. a loose screw) produces 4–12 kHz energy that repeats with the bass on every
/// repeat; room noise and amplifier hiss do not. Validated live 2026-11-06 (artifact 20261106-070750-775Z).</summary>
public sealed record PinkRubBuzzSettings(double LevelRmsDbFs = -18, double DurationSeconds = 6, int Repeats = 3)
{
    public const double LowHz = 25, HighHz = 150;
    /// <summary>Above −18 the period is the −18 waveform scaled up (peak −1.8 dBFS at −12), so lower levels keep their
    /// exact samples, hashes and saved line standards. Bench 2026-11-16: clicks start at −18 and are clear at −15/−12.</summary>
    public const double MaxLevelRmsDbFs = -12, ShapeLevelRmsDbFs = -18;
    public void Validate()
    {
        if (!double.IsFinite(LevelRmsDbFs) || LevelRmsDbFs < -60 || LevelRmsDbFs > MaxLevelRmsDbFs
            || !double.IsFinite(DurationSeconds) || DurationSeconds < 4 || DurationSeconds > 15 || Repeats is < 2 or > 5)
            throw new ArgumentException("Pink Rè: mức −60…−12 dBFS RMS; 4–15 giây mỗi lượt; 2–5 lượt lặp (khuyến nghị 3).");
    }
}

public static class PinkRubBuzzStimulus
{
    public const int SampleRate = 44100;
    public static bool IsSupportedRate(int rate) => rate is 44100 or 48000;
    /// <summary>Deterministic band-limited pink (exact FFT spectrum, fixed seed), looped to the lượt length, 30 ms fades.
    /// Nothing above ~150 Hz is played, so any 4–12 kHz energy at the mic is produced by the speaker/room.</summary>
    public static float[] Generate(PinkRubBuzzSettings settings, int sampleRate)
    {
        settings.Validate();
        if (!IsSupportedRate(sampleRate)) throw new ArgumentException("Pink Rè phát ở 44100 hoặc 48000 Hz.");
        double shapeLevel = Math.Min(settings.LevelRmsDbFs, PinkRubBuzzSettings.ShapeLevelRmsDbFs);
        float[] period = PinkNoiseSignal.CreatePeriod(new PinkNoiseSettings(PinkRubBuzzSettings.LowHz, PinkRubBuzzSettings.HighHz, shapeLevel), sampleRate);
        if (settings.LevelRmsDbFs > shapeLevel)
        {
            float gain = (float)Math.Pow(10, (settings.LevelRmsDbFs - shapeLevel) / 20);
            period = period.Select(v => v * gain).ToArray();
            if (period.Any(v => Math.Abs(v) >= .98f)) throw new ArgumentException("Pink Rè không đủ headroom ở mức này; giảm mức RMS.");
        }
        var result = new float[(int)Math.Round(settings.DurationSeconds * sampleRate)];
        int fade = (int)(0.03 * sampleRate);
        for (int i = 0; i < result.Length; i++)
        {
            double gain = Math.Min(1, Math.Min(i, result.Length - 1 - i) / (double)fade);
            result[i] = (float)(period[i % period.Length] * 0.5 * (1 - Math.Cos(Math.PI * gain)));
        }
        return result;
    }
    public static string Hash(PinkRubBuzzSettings settings)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(Generate(settings, SampleRate).AsSpan())));
    public static string Key(PinkRubBuzzSettings settings, string routeContext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { algorithm = PinkRubBuzzAnalysis.Version, stimulus = Hash(settings), settings, routeContext }))));
    public static void WriteWave(string path, float[] samples, int sampleRate)
    {
        using var writer = ResultPersistencePolicy.CreateWave(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}

public sealed record PinkRubBuzzEnvelope(double Seconds, double LevelDb, bool Foreign);
public sealed record PinkRubBuzzMetrics(bool IsValid, string Reason)
{
    /// <summary>"Tỷ lệ rè lặp lại": mean pairwise Pearson correlation (%) of the 4–12 kHz envelopes between repeats.</summary>
    public double RepeatCoherencePct { get; init; }
    public double[] PairCoherencePct { get; init; } = [];
    /// <summary>Mean 4–12 kHz power on frames kept after foreign-noise removal (dBFS).</summary>
    public double HfDbFs { get; init; }
    /// <summary>Mean 4–12 kHz power over all frames incl. removed ones (catches dense non-repeating rattle).</summary>
    public double HfAllDbFs { get; init; }
    public double KeptPct { get; init; }
    /// <summary>Mean 25–150 Hz level at the mic (dBFS) and its spread between repeats (dB).</summary>
    public double DriveDbFs { get; init; }
    public double DriveSpreadDb { get; init; }
    public double BackgroundRmsDbFs { get; init; }
    public double BackgroundHfDbFs { get; init; }
    public double BackgroundLfDbFs { get; init; }
    public double BackgroundDriftDb { get; init; }
    /// <summary>Drive at mic minus background 4–12 kHz (dB): how far above the room a rattle can be seen.</summary>
    public double SensitivityDb { get; init; }
    public int Repeats { get; init; }
    public double AnalyzedSeconds { get; init; }
    public PinkRubBuzzEnvelope[] Envelope { get; init; } = [];
    public double[] CaptureLagSeconds { get; init; } = [];
    public double[] CaptureStartSeconds { get; init; } = [];
    public PinkRubBuzzEnvelope[][] RepeatEnvelopes { get; init; } = [];
}

public sealed record PinkRubBuzzRun(PinkRubBuzzSettings Settings, string ContextKey, string Route, int SampleRate, PinkRubBuzzMetrics Metrics)
{
    [JsonIgnore] public bool PersistenceAllowed { get; init; } = ResultPersistencePolicy.CanPersist;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string StimulusHash { get; init; } = PinkRubBuzzStimulus.Hash(Settings);
    public string Algorithm { get; init; } = PinkRubBuzzAnalysis.Version;
    public string CaptureMode { get; init; } = "WASAPI Exclusive";
    public string Verdict { get; set; } = "REVIEW";
    public string Message { get; set; } = "Chưa so sánh.";
    public PinkRubBuzzLimits? Limits { get; set; }
    public PinkRubBuzzMetrics? StandardMetrics { get; set; }
    public DateTimeOffset? StandardCreatedUtc { get; set; }
    public double? DeltaRepeatPct { get; set; }
    public double? DeltaHfDb { get; set; }
    public bool SavedAsStandard { get; set; }
    public int PlaybackSampleRate { get; init; } = PinkRubBuzzStimulus.SampleRate;
    [JsonIgnore] public float[][] Captures { get; init; } = [];
    [JsonIgnore] public float[] Background { get; init; } = [];
    public bool IsValid => Metrics.IsValid;
    public string Summary => $"{Verdict}: {Message}" + (Metrics.IsValid
        ? $"{Environment.NewLine}Rè lặp lại {Metrics.RepeatCoherencePct:0.0}% · cao tần 4–12 kHz {Metrics.HfDbFs:0.0} dBFS · bass mic {Metrics.DriveDbFs:0.0} dBFS · độ nhạy {Metrics.SensitivityDb:0.0} dB · giữ {Metrics.KeptPct:0}% khung"
        : "");
}

public static class PinkRubBuzzAnalysis
{
    public const string Version = "pink-rattle-2.0.0";
    public const double HfLowHz = 4000, HfHighHz = 12000;
    public static double Db(double amplitude) => 20 * Math.Log10(Math.Max(1e-12, amplitude));
    private static double PowerDb(double power) => 10 * Math.Log10(Math.Max(1e-30, power));

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2; private double _z1, _z2;
        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        { _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0; }
        public static Biquad HighPass(int rate, double f, double q)
        {
            double w = 2 * Math.PI * f / rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            return new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }
        public static Biquad LowPass(int rate, double f, double q)
        {
            double w = 2 * Math.PI * f / rate, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            return new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }
        public double Process(double x) { double y = _b0 * x + _z1; _z1 = _b1 * x - _a1 * y + _z2; _z2 = _b2 * x - _a2 * y; return y; }
    }
    private static double[] ButterworthQ(int order) => Enumerable.Range(1, order / 2).Select(k => 1 / (2 * Math.Cos((2 * k - 1) * Math.PI / (2 * order)))).ToArray();
    /// <summary>Butterworth band-pass (high-pass + low-pass of the given order each), double precision.</summary>
    public static double[] BandPass(float[] samples, int rate, double low, double high, int order)
    {
        var stages = ButterworthQ(order).Select(q => Biquad.HighPass(rate, low, q)).Concat(ButterworthQ(order).Select(q => Biquad.LowPass(rate, high, q))).ToArray();
        var output = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++) { double x = samples[i]; foreach (var s in stages) x = s.Process(x); output[i] = x; }
        return output;
    }
    private static double MeanSquare(ReadOnlySpan<double> x) { double s = 0; foreach (double v in x) s += v * v; return x.Length == 0 ? 0 : s / x.Length; }
    private static double MeanSquare(ReadOnlySpan<float> x) { double s = 0; foreach (float v in x) s += (double)v * v; return x.Length == 0 ? 0 : s / x.Length; }

    /// <summary>Lag (samples) that aligns <paramref name="other"/> to <paramref name="reference"/> (aligned[t] = other[t − lag]),
    /// from the LF cross-correlation on a decimated copy (LF content is below 150 Hz).</summary>
    private static int Lag(double[] reference, double[] other, int rate)
    {
        int step = Math.Max(1, rate / 6000);
        double[] a = reference.Where((_, i) => i % step == 0).ToArray(), b = other.Where((_, i) => i % step == 0).ToArray();
        int n = 1; while (n < a.Length + b.Length) n <<= 1;
        var fa = new Complex[n]; var fb = new Complex[n];
        for (int i = 0; i < a.Length; i++) fa[i] = a[i];
        for (int i = 0; i < b.Length; i++) fb[i] = b[i];
        Fourier.Forward(fa, FourierOptions.Matlab); Fourier.Forward(fb, FourierOptions.Matlab);
        for (int i = 0; i < n; i++) fa[i] *= Complex.Conjugate(fb[i]);
        Fourier.Inverse(fa, FourierOptions.Matlab);
        int maxLag = Math.Max(1, rate / 2 / step), best = 0; double bestValue = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double v = fa[(lag + n) % n].Real;
            if (v > bestValue) { bestValue = v; best = lag; }
        }
        return best * step;
    }
    private static double Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++) { double dx = x[i] - mx, dy = y[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }
    private static double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }

    /// <summary>Chain-level analysis. Limit-dependent checks (sensitivity, foreign-noise share, match to the line
    /// standard) are applied by <see cref="PinkRubBuzzStandard.Compare"/>.</summary>
    public static PinkRubBuzzMetrics Analyze(IReadOnlyList<float[]> captures, float[] background, int rate, PinkRubBuzzSettings settings,
        double backgroundRmsLimitDbFs = -50)
    {
        settings.Validate();
        PinkRubBuzzMetrics Invalid(string reason) => new(false, reason) { Repeats = captures.Count };
        if (rate < 32000 || rate > 192000) return Invalid("Ngõ thu cần 32–192 kHz để phân tích 4–12 kHz.");
        if (captures.Count < 2) return Invalid("Cần ít nhất 2 lượt lặp cùng dạng sóng (khuyến nghị 3).");
        if (captures.Any(c => c.Length < rate * 3)) return Invalid("Lượt thu quá ngắn.");
        if (captures.Append(background).Any(c => c.Any(v => !float.IsFinite(v)))) return Invalid("Bản thu có NaN/Infinity.");
        if (captures.Any(c => c.Any(v => Math.Abs(v) >= .98f))) return Invalid("Clipping ngõ thu; giảm gain mic hoặc mức phát.");
        var reasons = new List<string>();

        // Background: a real microphone always has room/self noise. Digital silence or a decaying floor means a
        // Windows effect (noise suppression/AGC) is processing the capture path — the v1.2 failure mode.
        int skip = Math.Min(background.Length, (int)(0.3 * rate));
        float[] bg = background[skip..];
        if (bg.Length < rate) return Invalid("Thu nền quá ngắn (cần ≥ 1,3 s).");
        double bgRms = PowerDb(MeanSquare(bg));
        double drift = bg.Length >= 2.2 * rate ? Math.Abs(PowerDb(MeanSquare(bg.AsSpan(0, rate))) - PowerDb(MeanSquare(bg.AsSpan(bg.Length - rate)))) : 0;
        double zeroFraction = bg.Count(v => v == 0) / (double)bg.Length;
        if (bgRms < -100 || bgRms > backgroundRmsLimitDbFs || drift > 6 || zeroFraction > .01)
            reasons.Add($"Nền thu bất thường (RMS {bgRms:0.0} dBFS, lệch đầu/cuối {drift:0.0} dB): đường thu đang bị Windows xử lý (nền gần 0/tắt dần) hoặc phòng ồn.");
        double bgLf = PowerDb(MeanSquare(BandPass(bg, rate, PinkRubBuzzSettings.LowHz, PinkRubBuzzSettings.HighHz, 4)));
        double bgHf = PowerDb(MeanSquare(BandPass(bg, rate, HfLowHz, HfHighHz, 8)));

        // Analysis window from the reference LF (skip onset 0.4 s and tail 0.3 s).
        var lf = captures.Select(c => BandPass(c, rate, PinkRubBuzzSettings.LowHz, PinkRubBuzzSettings.HighHz, 4)).ToArray();
        int frame = rate / 20;
        var frames = Enumerable.Range(0, lf[0].Length / frame).Select(f => Math.Sqrt(MeanSquare(lf[0].AsSpan(f * frame, frame)))).ToArray();
        double peak = frames.DefaultIfEmpty().Max();
        var on = frames.Select((v, i) => (v, i)).Where(p => p.v > peak * .1).Select(p => p.i).ToArray();
        if (peak <= 0 || on.Length == 0) return Invalid("Không thấy bass 25–150 Hz tại mic; kiểm tra route/loa/mic.") with { BackgroundRmsDbFs = bgRms };
        int s0 = on[0] * frame + (int)(0.4 * rate), s1 = (on[^1] + 1) * frame - (int)(0.3 * rate);
        int[] lags = lf.Select((x, i) => i == 0 ? 0 : Lag(lf[0], x, rate)).ToArray();
        for (int i = 0; i < captures.Count; i++) { s0 = Math.Max(s0, lags[i]); s1 = Math.Min(s1, captures[i].Length + lags[i]); }
        if (s1 - s0 < 2 * rate) return Invalid("Vùng bass ổn định chung giữa các lượt < 2 s; kiểm tra thời lượng/độ trễ.");

        double[] drive = lf.Select((x, i) => PowerDb(MeanSquare(x.AsSpan(s0 - lags[i], s1 - s0)))).ToArray();
        int k = Math.Max(2, (int)Math.Round(0.002 * rate)), step = k / 2;
        int count = (s1 - s0) / step;
        var envelopes = new double[captures.Count][];
        for (int i = 0; i < captures.Count; i++)
        {
            var h = BandPass(captures[i], rate, HfLowHz, HfHighHz, 8);
            var prefix = new double[h.Length + 1];
            for (int t = 0; t < h.Length; t++) prefix[t + 1] = prefix[t] + h[t] * h[t];
            var e = new double[count];
            for (int j = 0; j < count; j++)
            {
                int center = s0 + j * step - lags[i], a = Math.Clamp(center - k / 2, 0, h.Length), b = Math.Clamp(a + k, 0, h.Length);
                e[j] = b > a ? (prefix[b] - prefix[a]) / (b - a) : 0;
            }
            envelopes[i] = e;
        }
        // Foreign noise (one repeat > median of the others by 10 dB) is removed, dilated ±10 frames (±10 ms).
        var foreign = new bool[count];
        for (int j = 0; j < count; j++)
            for (int i = 0; i < captures.Count && !foreign[j]; i++)
            {
                double self = PowerDb(envelopes[i][j]);
                double others = Median(Enumerable.Range(0, captures.Count).Where(o => o != i).Select(o => PowerDb(envelopes[o][j])));
                foreign[j] = self - others > 10;
            }
        var excluded = new bool[count];
        for (int j = 0; j < count; j++) if (foreign[j]) for (int d = Math.Max(0, j - 10); d <= Math.Min(count - 1, j + 10); d++) excluded[d] = true;
        int[] kept = Enumerable.Range(0, count).Where(j => !excluded[j]).ToArray();
        double keptPct = 100.0 * kept.Length / count;
        // Too few kept frames: no repeat-coherence. Compare decides between dense chaotic rattle (HF of all frames
        // above the standard) and a noisy room (INVALID).
        var pairs = new List<double>();
        for (int i = 0; i < captures.Count; i++)
            for (int o = i + 1; o < captures.Count; o++)
                pairs.Add(kept.Length < 200 ? 0 : 100 * Pearson(kept.Select(j => envelopes[i][j]).ToArray(), kept.Select(j => envelopes[o][j]).ToArray()));
        double hf = PowerDb(kept.Length == 0 ? 0 : kept.Average(j => envelopes.Average(e => e[j])));
        double hfAll = PowerDb(envelopes.Average(e => e.Average()));
        double driveMean = PowerDb(drive.Average(d => Math.Pow(10, d / 10)));
        if (drive.Min() - bgLf < 20) reasons.Add($"Bass tại mic yếu (SNR {drive.Min() - bgLf:0.0} dB < 20); kiểm tra route/mức phát/mic.");
        if (drive.Max() - drive.Min() > 1) reasons.Add($"Bass giữa các lượt lệch {drive.Max() - drive.Min():0.0} dB > 1 dB; loa/mic bị xê dịch hoặc mức phát đổi.");
        var envelope = new List<PinkRubBuzzEnvelope>();
        for (int j = 0; j < count; j += 5)
            envelope.Add(new((s0 + j * step) / (double)rate, PowerDb(envelopes.Average(e => e[j])), excluded[j]));
        return new(reasons.Count == 0, reasons.Count == 0 ? "Bản thu hợp lệ." : string.Join(" ", reasons))
        {
            RepeatCoherencePct = pairs.Average(), PairCoherencePct = pairs.ToArray(), HfDbFs = hf, HfAllDbFs = hfAll, KeptPct = keptPct,
            DriveDbFs = driveMean, DriveSpreadDb = drive.Max() - drive.Min(), BackgroundRmsDbFs = bgRms, BackgroundHfDbFs = bgHf,
            BackgroundLfDbFs = bgLf, BackgroundDriftDb = drift, SensitivityDb = driveMean - bgHf, Repeats = captures.Count,
            AnalyzedSeconds = (s1 - s0) / (double)rate, Envelope = envelope.ToArray(),
            CaptureLagSeconds = lags.Select(lag => lag / (double)rate).ToArray(),
            CaptureStartSeconds = lags.Select(lag => (on[0] * frame - lag) / (double)rate).ToArray(),
            RepeatEnvelopes = envelopes.Select(e => Enumerable.Range(0, count).Where(j => j % 5 == 0)
                .Select(j => new PinkRubBuzzEnvelope((s0 + j * step) / (double)rate, PowerDb(e[j]), excluded[j])).ToArray()).ToArray()
        };
    }
}
