using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public sealed record ChirpImpulsiveSettings(double HighPassMultiple = 20, double CrestThresholdDb = 13,
    double PeakAboveBackgroundDb = 6, double MinimumSensitivityDb = 30)
{
    public void Validate()
    {
        if (!double.IsFinite(HighPassMultiple) || HighPassMultiple is < 5 or > 40
            || !double.IsFinite(CrestThresholdDb) || CrestThresholdDb is < 12 or > 30
            || !double.IsFinite(PeakAboveBackgroundDb) || PeakAboveBackgroundDb is < 3 or > 30
            || !double.IsFinite(MinimumSensitivityDb) || MinimumSensitivityDb is < 10 or > 100)
            throw new ArgumentException("High-pass bám tần số: bội 5–40, crest 12–30 dB, vượt nền 3–30 dB, độ nhạy 10–100 dB.");
    }
}

public sealed record ChirpImpulsiveCurve(double[] PeakDbFs, double[] RmsDbFs, double[] CrestDb,
    double[] PeakRelativeFundamentalDb, double[] PeakRelativeBackgroundDb);

public sealed record ChirpImpulsiveResult
{
    public string Version { get; init; } = ChirpImpulsiveDistortion.Version;
    public ChirpImpulsiveSettings Settings { get; init; } = new();
    public string Filter { get; init; } = "High-pass bám tần số kiểu IEC 60268-21 / Klippel; Butterworth bậc 8 hai chiều; không khẳng định tương đương/tuân thủ.";
    public string Verdict { get; init; } = "không đủ độ nhạy";
    public bool HasSensitivity { get; init; }
    public double SensitivityDb { get; init; }
    public double[] FrequenciesHz { get; init; } = [];
    public ChirpImpulsiveCurve[] RepeatMetrics { get; init; } = [];
    public ChirpImpulsiveCurve? Median { get; init; }
    public ChirpImpulsiveCurve? Minimum { get; init; }
    public int[] FlaggedRepeats { get; init; } = [];
    public double? WorstFrequencyHz { get; init; }
    public double? WorstRepeatableCrestDb { get; init; }
    public double[]? StandardHohdDeltaDb { get; init; }
    [JsonIgnore] public float[][] Residuals { get; init; } = [];
    public ChirpImpulsiveEvidence ToDiagnosisEvidence() => new(HasSensitivity, WorstRepeatableCrestDb,
        WorstFrequencyHz, FlaggedRepeats.Any(n => n >= 2), Settings.CrestThresholdDb);
}

/// <summary>
/// Additive diagnostic only. Never feeds ChirpRubBuzzStandard or its verdict. The eighth-order Butterworth
/// is four RBJ bilinear-transform high-pass sections, Q=1/(2*cos((2*k+1)*pi/16)), run forward then backward
/// with the SAME absolute-time cutoff trajectory. Coefficients are calculated every 0.5 ms and linearly
/// interpolated EACH SAMPLE (avoids steps producing fake impulses). Reverse filtering reverses the
/// coefficient trajectory too. Forward/backward gives a nominal zero-phase, sixteenth-order magnitude;
/// an LTV filter is not mathematically exactly zero phase. Fifty milliseconds reflected padding and a
/// 50 ms analysis guard remove initial/end transients. No 42 ms FFT averaging: each grid cell uses the
/// raw sample maximum and RMS over its 1/12-octave time span. Peaks are unweighted sample peaks, not SPL.
/// </summary>
public static class ChirpImpulsiveDistortion
{
    public const string Version = "rubbuzz-diagnostics-1.0.0";
    private static double Db(double v) => 20 * Math.Log10(Math.Max(1e-15, v));
    private static double MedianValue(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 0 ? (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2 : a[a.Length / 2];
    }

    public static ChirpImpulsiveResult Analyze(ChirpRubBuzzRun run, ChirpImpulsiveSettings? settings = null)
        => Analyze(run.Captures, run.Background, run.SampleRate, run.Settings, run.Metrics, settings, run.StandardMetrics);

    public static ChirpImpulsiveResult Analyze(IReadOnlyList<float[]> captures, float[] background, int rate,
        ChirpRubBuzzSettings chirp, ChirpRubBuzzMetrics alignment, ChirpImpulsiveSettings? settings = null,
        ChirpRubBuzzMetrics? standard = null)
    {
        settings ??= new(); settings.Validate(); chirp.Validate();
        var empty = new ChirpImpulsiveResult { Settings = settings };
        if (rate is < 32000 or > 192000 || captures.Count < 2 || background.Length < rate
            || alignment.CaptureStartSeconds.Length != captures.Count || alignment.CaptureStartSeconds.Any(t => !double.IsFinite(t) || t < 0)
            || captures.Append(background).Any(x => x.Any(v => !float.IsFinite(v)))) return empty;
        double k = Math.Log(chirp.F2Hz / chirp.F1Hz), duration = chirp.DurationSeconds;
        double stop = Math.Min(chirp.F2Hz, Math.Min(1800, .45 * rate / settings.HighPassMultiple));
        // Same origin, spacing and support as ChirpRubBuzzAnalysis; only truncate at the tracking limit.
        var grid = new List<double>();
        for (double f = chirp.F1Hz * 1.15; f * Math.Pow(2, 1 / 24.0) <= stop; f *= Math.Pow(2, 1 / 12.0))
        {
            double t = duration * Math.Log(f / chirp.F1Hz) / k;
            if (t > .05 && t < duration - .05) grid.Add(f);
        }
        if (grid.Count == 0) return empty;
        int length = (int)Math.Round(duration * rate), skip = (int)(.3 * rate);
        var floor = new float[length];
        // Mirroring conserves the measured noise amplitude and avoids a discontinuity on wrap for slow sweeps.
        int bgLength = background.Length - skip;
        if (bgLength < 2) return empty;
        for (int i = 0; i < floor.Length; i++)
        {
            int at = i % (2 * bgLength - 2); if (at >= bgLength) at = 2 * bgLength - 2 - at;
            floor[i] = background[skip + at];
        }
        var backgroundFiltered = Filter(floor, rate, chirp, settings.HighPassMultiple);
        var residuals = new List<float[]>(); var curves = new List<ChirpImpulsiveCurve>();
        var sensitivity = new List<double>();
        foreach (var (capture, repeat) in captures.Select((x, i) => (x, i)))
        {
            int lag = (int)Math.Round(alignment.CaptureStartSeconds[repeat] * rate);
            if (lag < 0 || capture.Length - lag < length) return empty;
            var aligned = capture.AsSpan(lag, length).ToArray();
            var residual = Filter(aligned, rate, chirp, settings.HighPassMultiple); residuals.Add(residual);
            var peak = new double[grid.Count]; var rms = new double[grid.Count]; var crest = new double[grid.Count];
            var relFund = new double[grid.Count]; var relBg = new double[grid.Count];
            for (int g = 0; g < grid.Count; g++)
            {
                double center = duration * Math.Log(grid[g] / chirp.F1Hz) / k;
                double half = duration * Math.Log(2) / (24 * k);
                int first = Math.Clamp((int)Math.Ceiling((center - half) * rate), 0, length - 1);
                int last = Math.Clamp((int)Math.Ceiling((center + half) * rate), first + 1, length);
                double maximum = 0, power = 0, bgMaximum = 0;
                double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
                for (int i = first; i < last; i++)
                {
                    double r = residual[i]; maximum = Math.Max(maximum, Math.Abs(r)); power += r * r;
                    bgMaximum = Math.Max(bgMaximum, Math.Abs(backgroundFiltered[i]));
                    // Local sweep demodulation: LS sinusoid amplitude, not a sample max contaminated by HF.
                    double phase = 2 * Math.PI * chirp.F1Hz * duration / k * (Math.Exp(i / (double)rate / duration * k) - 1);
                    double s = Math.Sin(phase), c = Math.Cos(phase);
                    ss += s * s; cc += c * c; sc += s * c; ys += aligned[i] * s; yc += aligned[i] * c;
                }
                double determinant = Math.Max(1e-20, ss * cc - sc * sc);
                double a = (ys * cc - yc * sc) / determinant, b = (yc * ss - ys * sc) / determinant;
                double fundamental = Math.Sqrt(a * a + b * b);
                peak[g] = Db(maximum); rms[g] = Db(Math.Sqrt(power / (last - first)));
                crest[g] = peak[g] - rms[g]; relFund[g] = peak[g] - Db(fundamental); relBg[g] = peak[g] - Db(bgMaximum);
                sensitivity.Add(Db(fundamental) - Db(bgMaximum));
            }
            curves.Add(new(peak, rms, crest, relFund, relBg));
        }
        ChirpImpulsiveCurve Consensus(bool minimum)
        {
            double[] Select(Func<ChirpImpulsiveCurve, double[]> metric) => Enumerable.Range(0, grid.Count)
                .Select(i => minimum ? curves.Min(c => metric(c)[i]) : MedianValue(curves.Select(c => metric(c)[i]))).ToArray();
            return new(Select(c => c.PeakDbFs), Select(c => c.RmsDbFs), Select(c => c.CrestDb),
                Select(c => c.PeakRelativeFundamentalDb), Select(c => c.PeakRelativeBackgroundDb));
        }
        var median = Consensus(false); var min = Consensus(true);
        var flagged = Enumerable.Range(0, grid.Count).Select(g => curves.Count(c =>
            c.CrestDb[g] >= settings.CrestThresholdDb && c.PeakRelativeBackgroundDb[g] >= settings.PeakAboveBackgroundDb)).ToArray();
        int[] repeated = Enumerable.Range(0, grid.Count).Where(g => flagged[g] >= 2).ToArray();
        int worst = repeated.OrderByDescending(g => median.PeakRelativeFundamentalDb[g]).FirstOrDefault(-1);
        double sensitivityDb = MedianValue(sensitivity); bool sensitive = alignment.IsValid && sensitivityDb >= settings.MinimumSensitivityDb;
        double[]? delta = null;
        if (standard != null)
        {
            var pairs = grid.Select(f => (At: Array.FindIndex(alignment.FrequenciesHz, v => Math.Abs(Math.Log2(v / f)) < .001),
                StandardAt: Array.FindIndex(standard.FrequenciesHz, v => Math.Abs(Math.Log2(v / f)) < .001))).ToArray();
            if (pairs.All(p => p.At >= 0 && p.StandardAt >= 0 && p.At < alignment.HohdDb.Length && p.StandardAt < standard.HohdDb.Length))
                delta = pairs.Select(p => alignment.HohdDb[p.At] - standard.HohdDb[p.StandardAt]).ToArray();
        }
        return new()
        {
            Settings = settings, FrequenciesHz = grid.ToArray(), RepeatMetrics = curves.ToArray(), Median = median, Minimum = min,
            FlaggedRepeats = flagged, WorstFrequencyHz = worst >= 0 ? grid[worst] : null,
            WorstRepeatableCrestDb = worst >= 0 ? median.CrestDb[worst] : null,
            HasSensitivity = sensitive, SensitivityDb = sensitivityDb,
            Verdict = !sensitive ? "không đủ độ nhạy" : worst >= 0 ? $"CÓ MÉO XUNG LẶP LẠI tại {grid[worst]:0} Hz" : "chưa thấy",
            Residuals = residuals.ToArray(), StandardHohdDeltaDb = delta
        };
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        public static Coefficients Lerp(Coefficients a, Coefficients b, double t) => new(
            a.B0 + (b.B0 - a.B0) * t, a.B1 + (b.B1 - a.B1) * t, a.B2 + (b.B2 - a.B2) * t,
            a.A1 + (b.A1 - a.A1) * t, a.A2 + (b.A2 - a.A2) * t);
    }
    public static float[] Filter(float[] samples, int rate, ChirpRubBuzzSettings chirp, double multiple = 20)
    {
        chirp.Validate();
        if (rate is < 32000 or > 192000 || multiple is < 5 or > 40 || !double.IsFinite(multiple))
            throw new ArgumentOutOfRangeException(nameof(multiple));
        if (samples.Length < 2) return samples.ToArray();
        int pad = Math.Min(samples.Length - 1, (int)(.05 * rate)), count = samples.Length + 2 * pad;
        var y = new double[count];
        for (int i = 0; i < count; i++)
        {
            int at = i - pad; if (at < 0) at = -at; if (at >= samples.Length) at = 2 * samples.Length - 2 - at;
            y[i] = samples[at];
        }
        int step = Math.Max(1, rate / 2000), knots = (count - 1) / step + 2;
        var coefficients = new Coefficients[4][];
        for (int section = 0; section < 4; section++)
        {
            coefficients[section] = new Coefficients[knots];
            double q = 1 / (2 * Math.Cos((2 * section + 1) * Math.PI / 16));
            for (int j = 0; j < knots; j++)
            {
                double t = Math.Clamp((j * step - pad) / (double)rate, 0, chirp.DurationSeconds);
                double f = chirp.F1Hz * Math.Exp(t / chirp.DurationSeconds * Math.Log(chirp.F2Hz / chirp.F1Hz));
                double cutoff = Math.Clamp(multiple * f, 1000, .45 * rate);
                double w = 2 * Math.PI * cutoff / rate, c = Math.Cos(w), a = Math.Sin(w) / (2 * q), norm = 1 / (1 + a);
                coefficients[section][j] = new((1 + c) * .5 * norm, -(1 + c) * norm, (1 + c) * .5 * norm, -2 * c * norm, (1 - a) * norm);
            }
        }
        for (int pass = 0; pass < 2; pass++)
            for (int section = 0; section < 4; section++)
            {
                double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                for (int j = 0; j < count; j++)
                {
                    int i = pass == 0 ? j : count - 1 - j;
                    var c = Coefficients.Lerp(coefficients[section][i / step], coefficients[section][i / step + 1], (i % step) / (double)step);
                    double x = y[i], value = c.B0 * x + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
                    x2 = x1; x1 = x; y2 = y1; y1 = value; y[i] = value;
                }
            }
        var result = new float[samples.Length];
        for (int i = 0; i < result.Length; i++) result[i] = (float)y[i + pad];
        return result;
    }
}
