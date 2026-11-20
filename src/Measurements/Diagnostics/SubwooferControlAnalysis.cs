using System.Numerics;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

/// <summary>Subwoofer rear-panel test (Ctrl+Shift+T). One exponential sweep per lượt; a knob or button is judged by
/// comparing two positions measured on the same route (Y_b / Y_a), never by an absolute room response.</summary>
public sealed record SubwooferSweepSettings(double StartHz = 10, double EndHz = 1000, double DurationSeconds = 2.5,
    double PeakDbFs = -15, int Repeats = 3)
{
    public const double LeadSeconds = 0.15, TailSeconds = 0.45;
    public double TotalSeconds => LeadSeconds + DurationSeconds + TailSeconds;
    public void Validate()
    {
        if (!double.IsFinite(StartHz) || !double.IsFinite(EndHz) || StartHz < 5 || StartHz > 40 || EndHz < 300 || EndHz > 4000
            || !double.IsFinite(DurationSeconds) || DurationSeconds < 1 || DurationSeconds > 8
            || !double.IsFinite(PeakDbFs) || PeakDbFs < -40 || PeakDbFs > -6 || Repeats is < 2 or > 5)
            throw new ArgumentException("Sweep subwoofer: 5–40 Hz → 300–4000 Hz, 1–8 s, −40…−6 dBFS đỉnh, 2–5 lượt.");
    }
}

public static class SubwooferSweep
{
    /// <summary>Same continuous-time sweep at any rate, so the capture-rate copy is the deconvolution reference.</summary>
    public static float[] Generate(SubwooferSweepSettings settings, int sampleRate)
    {
        settings.Validate();
        if (sampleRate < 8000 || sampleRate > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int lead = (int)Math.Round(SubwooferSweepSettings.LeadSeconds * sampleRate);
        int count = (int)Math.Round(settings.DurationSeconds * sampleRate);
        var result = new float[(int)Math.Round(settings.TotalSeconds * sampleRate)];
        double rate = settings.DurationSeconds / Math.Log(settings.EndHz / settings.StartHz);
        double amplitude = Math.Pow(10, settings.PeakDbFs / 20);
        int fadeIn = (int)(0.05 * sampleRate), fadeOut = (int)(0.01 * sampleRate);
        for (int i = 0; i < count; i++)
        {
            double t = i / (double)sampleRate;
            double gain = Math.Min(1, Math.Min(i / (double)fadeIn, (count - 1 - i) / (double)fadeOut));
            gain = 0.5 * (1 - Math.Cos(Math.PI * Math.Max(0, gain)));
            result[lead + i] = (float)(amplitude * gain * Math.Sin(2 * Math.PI * settings.StartHz * rate * (Math.Exp(t / rate) - 1)));
        }
        return result;
    }
}

public sealed record SubwooferCapture(float[] Samples, int SampleRate);

/// <summary>One panel position: N lượt of the same sweep.</summary>
public sealed record SubwooferResponse(bool IsValid, string Reason, int SampleRate, int FftLength)
{
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string RouteKey { get; init; } = "";
    public string Route { get; init; } = "";
    /// <summary>1/24-octave grid 20–400 Hz; level is 1/6-octave mic/stimulus power (dB).</summary>
    public double[] FrequenciesHz { get; init; } = [];
    public double[] LevelDb { get; init; } = [];
    public double[][] RepeatLevelDb { get; init; } = [];
    public double PeakDbFs { get; init; } = double.NaN;
    public double SignalDbFs { get; init; } = double.NaN;
    public double NoiseDbFs { get; init; } = double.NaN;
    public double SnrDb { get; init; } = double.NaN;
    public double RepeatSpreadDb { get; init; } = double.NaN;
    public int Repeats { get; init; }
    /// <summary>Per-lượt FFT bins 0…MaxAnalysisHz of the capture (same length and rate for every lượt).</summary>
    [JsonIgnore] public Complex[][] Spectra { get; init; } = [];
    [JsonIgnore] public float[][] Captures { get; init; } = [];
}

/// <summary>Position b relative to position a: phase(Y_b/Y_a) ≈ φ0 − 2πfτ in the pass band.</summary>
public sealed record SubwooferRelation(bool IsValid, string Reason)
{
    public double DelayMs { get; init; } = double.NaN;
    /// <summary>Largest spread of the system latency between lượt of one position (jitter), ms.</summary>
    public double JitterMs { get; init; } = double.NaN;
    public double PhaseDeg { get; init; } = double.NaN;
    public double LevelChangeDb { get; init; } = double.NaN;
    public double FitResidualDeg { get; init; } = double.NaN;
    public double LowHz { get; init; }
    public double HighHz { get; init; }
    public int Pairs { get; init; }
    /// <summary>Phase of Y_b/Y_a per 1 Hz after removing the fitted delay (first lượt pair), centred on PhaseDeg:
    /// a clean polarity flip is a flat line at ±180°, a pure delay a flat line at 0°.</summary>
    public double[] PhaseCurveHz { get; init; } = [];
    public double[] PhaseCurveDeg { get; init; } = [];
    /// <summary>Level of Y_b/Y_a per 1 Hz (dB) on the same grid.</summary>
    public double[] LevelCurveDb { get; init; } = [];
}

public static class SubwooferControlAnalysis
{
    public const string Version = "subwoofer-controls-1.0";
    public const double MaxAnalysisHz = 600, GridLowHz = 20, GridHighHz = 400;
    public const double SilenceSeconds = 0.09;

    public static int FftLength(SubwooferSweepSettings settings, int sampleRate)
    {
        int n = 1, needed = (int)Math.Ceiling((settings.TotalSeconds + 1.5) * sampleRate);
        while (n < needed) n <<= 1;
        return n;
    }

    static Complex[] Spectrum(float[] samples, int n, int keep)
    {
        var buffer = new Complex[n];
        for (int i = 0; i < Math.Min(n, samples.Length); i++) buffer[i] = samples[i];
        Fourier.Forward(buffer, FourierOptions.Matlab);
        return buffer[..keep];
    }

    static double Db(double power) => 10 * Math.Log10(Math.Max(1e-30, power));
    static double Rms(float[] x, int start, int end)
    {
        double sum = 0; int n = 0;
        for (int i = Math.Max(0, start); i < Math.Min(x.Length, end); i++) { sum += x[i] * (double)x[i]; n++; }
        return n == 0 ? 0 : Math.Sqrt(sum / n);
    }

    public static double[] Grid()
    {
        var grid = new List<double>();
        for (int k = 0; GridLowHz * Math.Pow(2, k / 24.0) <= GridHighHz + 1e-9; k++) grid.Add(GridLowHz * Math.Pow(2, k / 24.0));
        return grid.ToArray();
    }

    static double[] Levels(Complex[] y, double[] stimulusPower, double binHz, double[] grid)
    {
        var levels = new double[grid.Length];
        for (int g = 0; g < grid.Length; g++)
        {
            int lo = Math.Max(1, (int)Math.Floor(grid[g] * Math.Pow(2, -1 / 12.0) / binHz));
            int hi = Math.Min(y.Length - 1, (int)Math.Ceiling(grid[g] * Math.Pow(2, 1 / 12.0) / binHz));
            double py = 0, px = 0;
            for (int k = lo; k <= hi; k++) { py += y[k].Real * y[k].Real + y[k].Imaginary * y[k].Imaginary; px += stimulusPower[k]; }
            levels[g] = Db(py / Math.Max(1e-30, px));
        }
        return levels;
    }

    public static SubwooferResponse Analyze(IReadOnlyList<SubwooferCapture> captures, SubwooferSweepSettings settings,
        string routeKey = "", string route = "")
    {
        settings.Validate();
        int rate = captures.Count > 0 ? captures[0].SampleRate : 0;
        SubwooferResponse Invalid(string reason) => new(false, reason, rate, 0) { RouteKey = routeKey, Route = route, Repeats = captures.Count };
        if (captures.Count < 2) return Invalid("Cần ít nhất 2 lượt thu.");
        if (rate < 8000 || captures.Any(c => c.SampleRate != rate)) return Invalid("Rate thu đổi giữa các lượt; không dùng dữ liệu dở dang.");
        if (captures.Any(c => c.Samples.Length < (int)(settings.TotalSeconds * rate) || c.Samples.Any(v => !float.IsFinite(v))))
            return Invalid("Bản thu thiếu hoặc lỗi; kiểm tra ngõ thu rồi đo lại.");
        int n = FftLength(settings, rate);
        double binHz = rate / (double)n;
        int keep = Math.Min(n / 2, (int)Math.Ceiling(MaxAnalysisHz / binHz) + 2);
        var stimulus = Spectrum(SubwooferSweep.Generate(settings, rate), n, keep);
        double[] stimulusPower = stimulus.Select(c => c.Real * c.Real + c.Imaginary * c.Imaginary).ToArray();
        var grid = Grid();
        var spectra = new Complex[captures.Count][]; var repeatLevels = new double[captures.Count][];
        double peak = 0, signal = 0, noise = 0;
        int silence = (int)(SilenceSeconds * rate);
        for (int r = 0; r < captures.Count; r++)
        {
            var x = captures[r].Samples;
            peak = Math.Max(peak, x.Max(v => Math.Abs((double)v)));
            noise = Math.Max(noise, Rms(x, 0, silence));
            signal += Rms(x, silence, x.Length) / captures.Count;
            spectra[r] = Spectrum(x, n, keep);
            repeatLevels[r] = Levels(spectra[r], stimulusPower, binHz, grid);
        }
        var mean = new double[grid.Length];
        for (int g = 0; g < grid.Length; g++) mean[g] = Db(repeatLevels.Average(l => Math.Pow(10, l[g] / 10)));
        double spread = Math.Sqrt(Enumerable.Range(0, grid.Length).Where(g => grid[g] >= 30 && grid[g] <= 150)
            .SelectMany(g => repeatLevels.Select(l => Math.Pow(l[g] - mean[g], 2))).DefaultIfEmpty(0).Average());
        double peakDb = 20 * Math.Log10(Math.Max(1e-12, peak)), signalDb = 20 * Math.Log10(Math.Max(1e-12, signal));
        double noiseDb = 20 * Math.Log10(Math.Max(1e-12, noise)), snr = signalDb - noiseDb;
        string reason = peak >= 0.98 ? $"Mic quá mức (đỉnh {peakDb:0.0} dBFS); giảm âm lượng sub hoặc dời mic ra xa."
            : signalDb < -65 || snr < 15 ? $"Không thấy sweep tại mic (mức {signalDb:0.0} dBFS, nền {noiseDb:0.0} dBFS). Kiểm tra nguồn sub, dây vào sub, mic."
            : spread > 2 ? $"Các lượt lệch nhau {spread:0.0} dB (ồn/rung/mic xê dịch); giữ yên rồi đo lại."
            : "";
        return new(reason.Length == 0, reason.Length == 0 ? $"Hợp lệ · SNR {snr:0} dB · lệch lượt {spread:0.0} dB" : reason, rate, n)
        {
            RouteKey = routeKey, Route = route, FrequenciesHz = grid, LevelDb = mean, RepeatLevelDb = repeatLevels,
            PeakDbFs = peakDb, SignalDbFs = signalDb, NoiseDbFs = noiseDb, SnrDb = snr, RepeatSpreadDb = spread,
            Repeats = captures.Count, Spectra = spectra, Captures = captures.Select(c => c.Samples).ToArray()
        };
    }

    public static double LevelAt(SubwooferResponse response, double hz)
    {
        var f = response.FrequenciesHz; var l = response.LevelDb;
        if (f.Length == 0 || hz < f[0] || hz > f[^1]) return double.NaN;
        int i = Array.FindIndex(f, v => v >= hz);
        if (i <= 0) return l[0];
        double w = Math.Log(hz / f[i - 1]) / Math.Log(f[i] / f[i - 1]);
        return l[i - 1] + w * (l[i] - l[i - 1]);
    }

    /// <summary>First frequency above the response maximum (25–400 Hz) where the level falls dropDb below it.</summary>
    public static double UpperCornerHz(SubwooferResponse response, double dropDb = 6)
    {
        var f = response.FrequenciesHz; var l = response.LevelDb;
        int start = Array.FindIndex(f, v => v >= 25);
        if (start < 0) return double.NaN;
        int top = start;
        for (int i = start; i < f.Length; i++) if (l[i] > l[top]) top = i;
        double target = l[top] - dropDb;
        for (int i = top + 1; i < f.Length; i++)
            if (l[i] < target)
            {
                double w = (l[i - 1] - target) / Math.Max(1e-9, l[i - 1] - l[i]);
                return f[i - 1] * Math.Pow(f[i] / f[i - 1], w);
            }
        return double.NaN;
    }

    /// <summary>Corner of the narrow crossover setting relative to the wide one: first frequency (two grid points in a row)
    /// where narrow − wide ≤ −dropDb. The sub's own high-pass and driver response are common to both and cancel.</summary>
    public static double RelativeCornerHz(SubwooferResponse wide, SubwooferResponse narrow, double dropDb = 6)
    {
        var f = wide.FrequenciesHz;
        if (f.Length == 0 || narrow.LevelDb.Length != f.Length) return double.NaN;
        double D(int i) => narrow.LevelDb[i] - wide.LevelDb[i];
        for (int i = 1; i + 1 < f.Length; i++)
            if (D(i) <= -dropDb && D(i + 1) <= -dropDb)
            {
                double w = (D(i - 1) + dropDb) / Math.Max(1e-9, D(i - 1) - D(i));
                return D(i - 1) <= -dropDb ? f[i - 1] : f[i - 1] * Math.Pow(f[i] / f[i - 1], Math.Clamp(w, 0, 1));
            }
        return double.NaN;
    }

    internal sealed record PairFit(double DelaySeconds, double PhaseRad, double ResidualRad, double LevelDb, int Points)
    {
        public double[] Hz { get; init; } = [];
        public double[] UnwrappedRad { get; init; } = [];
        public double[] LevelCurveDb { get; init; } = [];
    }

    /// <summary>Weighted linear fit of unwrapped phase(Y_b · conj Y_a) on a 1 Hz grid (cross-spectra summed per 1 Hz).</summary>
    internal static PairFit? Fit(Complex[] a, Complex[] b, double binHz, double lowHz, double highHz)
    {
        var points = new List<(double F, double Phase, double W, double Pa, double Pb)>();
        for (double f = lowHz; f <= highHz + 1e-9; f += 1)
        {
            int lo = Math.Max(1, (int)Math.Round((f - 0.5) / binHz)), hi = Math.Min(Math.Min(a.Length, b.Length) - 1, (int)Math.Round((f + 0.5) / binHz));
            Complex cross = Complex.Zero; double pa = 0, pb = 0;
            for (int k = lo; k <= hi; k++) { cross += b[k] * Complex.Conjugate(a[k]); pa += a[k].Magnitude * a[k].Magnitude; pb += b[k].Magnitude * b[k].Magnitude; }
            if (pa > 0 && pb > 0) points.Add((f, cross.Phase, cross.Magnitude, pa, pb));
        }
        if (points.Count < 10) return null;
        double maxA = points.Max(p => p.Pa), maxB = points.Max(p => p.Pb);
        // Within 20 dB of each band maximum: a crossover stop band or the sub's high-pass skirt has too little SNR for phase.
        points = points.Where(p => p.Pa > maxA * 1e-2 && p.Pb > maxB * 1e-2).ToList();
        if (points.Count < 10) return null;
        var phase = new double[points.Count]; phase[0] = points[0].Phase;
        for (int i = 1; i < points.Count; i++)
        {
            double step = points[i].Phase - points[i - 1].Phase;
            step -= 2 * Math.PI * Math.Round(step / (2 * Math.PI));
            phase[i] = phase[i - 1] + step;
        }
        double sw = 0, sf = 0, sp = 0;
        for (int i = 0; i < points.Count; i++) { sw += points[i].W; sf += points[i].W * points[i].F; sp += points[i].W * phase[i]; }
        double mf = sf / sw, mp = sp / sw, num = 0, den = 0;
        for (int i = 0; i < points.Count; i++) { num += points[i].W * (points[i].F - mf) * (phase[i] - mp); den += points[i].W * Math.Pow(points[i].F - mf, 2); }
        double slope = num / Math.Max(1e-30, den), intercept = mp - slope * mf, residual = 0;
        for (int i = 0; i < points.Count; i++) residual += points[i].W * Math.Pow(phase[i] - (intercept + slope * points[i].F), 2);
        double wrapped = intercept - 2 * Math.PI * Math.Round(intercept / (2 * Math.PI));
        return new(-slope / (2 * Math.PI), wrapped, Math.Sqrt(residual / sw),
            Db(points.Sum(p => p.Pb) / points.Sum(p => p.Pa)), points.Count)
        {
            Hz = points.Select(p => p.F).ToArray(), UnwrappedRad = phase,
            LevelCurveDb = points.Select(p => Db(p.Pb / p.Pa)).ToArray()
        };
    }

    /// <summary>Phase after removing the fitted delay, folded into ±180° around the intercept (degrees).</summary>
    internal static double[] AlignedPhaseDeg(PairFit fit)
    {
        double slope = -2 * Math.PI * fit.DelaySeconds;
        return fit.Hz.Select((f, i) =>
        {
            double v = fit.UnwrappedRad[i] - slope * f - fit.PhaseRad;
            v -= 2 * Math.PI * Math.Round(v / (2 * Math.PI));
            return (v + fit.PhaseRad) * 180 / Math.PI;
        }).ToArray();
    }

    public static SubwooferRelation Relate(SubwooferResponse a, SubwooferResponse b, double lowHz = 30, double highHz = 150)
    {
        SubwooferRelation Invalid(string reason) => new(false, reason) { LowHz = lowHz, HighHz = highHz };
        if (!a.IsValid || !b.IsValid) return Invalid("Một trong hai vị trí chưa đo hợp lệ.");
        if (a.SampleRate != b.SampleRate || a.FftLength != b.FftLength || a.Spectra.Length < 2 || b.Spectra.Length < 2)
            return Invalid("Hai vị trí khác rate/độ dài thu; đo lại cả hai trên cùng route.");
        double binHz = a.SampleRate / (double)a.FftLength;
        PairFit? F(Complex[] x, Complex[] y) => Fit(x, y, binHz, lowHz, highHz);
        var pairs = (from bi in b.Spectra from aj in a.Spectra select F(aj, bi)).ToArray();
        if (pairs.Any(p => p == null)) return Invalid($"Không đủ tín hiệu {lowHz:0}–{highHz:0} Hz ở cả hai vị trí để so pha/trễ.");
        double Range(Complex[][] set) { var d = set.Skip(1).Select(s => F(set[0], s)?.DelaySeconds ?? double.NaN).Append(0).ToArray(); return (d.Max() - d.Min()) * 1000; }
        double jitter = Math.Max(Range(a.Spectra), Range(b.Spectra));
        var delays = pairs.Select(p => p!.DelaySeconds * 1000).Order().ToArray();
        double median = delays.Length % 2 == 1 ? delays[delays.Length / 2] : (delays[delays.Length / 2 - 1] + delays[delays.Length / 2]) / 2;
        var vector = new Complex(pairs.Sum(p => Math.Cos(p!.PhaseRad)), pairs.Sum(p => Math.Sin(p!.PhaseRad)));
        return new(true, "")
        {
            DelayMs = median, JitterMs = double.IsFinite(jitter) ? jitter : double.PositiveInfinity, PhaseDeg = vector.Phase * 180 / Math.PI,
            LevelChangeDb = pairs.Average(p => p!.LevelDb), FitResidualDeg = pairs.Max(p => p!.ResidualRad) * 180 / Math.PI,
            LowHz = lowHz, HighHz = highHz, Pairs = pairs.Length,
            PhaseCurveHz = pairs[0]!.Hz, PhaseCurveDeg = AlignedPhaseDeg(pairs[0]!), LevelCurveDb = pairs[0]!.LevelCurveDb
        };
    }
}

/// <summary>Defaults judge that each control works and matches its printed range; tune on a known-good unit.</summary>
public sealed record SubwooferControlLimits
{
    public double CrossoverMinLabelHz { get; init; } = 40;
    public double CrossoverMaxLabelHz { get; init; } = 180;
    /// <summary>The 40 Hz position's relative corner may differ from the label by this factor before REVIEW.</summary>
    public double CornerToleranceRatio { get; init; } = 1.6;
    public double RejectionProbeHz { get; init; } = 120;
    public double MinRejectionDb { get; init; } = 10;
    public double PassbandProbeHz { get; init; } = 30;
    public double MaxPassbandChangeDb { get; init; } = 6;
    public double DelayMaxLabelMs { get; init; } = 20;
    public double DelayToleranceMs { get; init; } = 3;
    public double MaxJitterMs { get; init; } = 1.5;
    public double MaxLevelChangeDb { get; init; } = 3;
    public double PhaseInvertedMinDeg { get; init; } = 135;
    public double PhaseSameMaxDeg { get; init; } = 45;
    public double MaxFitResidualDeg { get; init; } = 35;
    public double ReturnDelayToleranceMs { get; init; } = 2;
    public double ReturnLevelToleranceDb { get; init; } = 2;
}

public sealed record SubwooferCheck(string Control, string Verdict, string Message)
{
    public string Measured { get; init; } = "—";
    public string Expected { get; init; } = "—";
    public SubwooferRelation? Relation { get; init; }
}

public static class SubwooferControlEvaluation
{
    public const string Pending = "PENDING";
    public static string Combine(IEnumerable<string> verdicts)
    {
        var v = verdicts.ToArray();
        return v.Contains("FAIL") ? "FAIL" : v.Contains("INVALID") ? "INVALID" : v.Contains("REVIEW") ? "REVIEW"
            : v.Length == 0 || v.Contains(Pending) ? Pending : "PASS";
    }

    public static SubwooferCheck Crossover(SubwooferResponse max, SubwooferResponse min, SubwooferControlLimits limits)
    {
        const string name = "Crossover";
        double low = limits.CrossoverMinLabelHz / limits.CornerToleranceRatio, high = limits.CrossoverMinLabelHz * limits.CornerToleranceRatio;
        string expected = $"vị trí {limits.CrossoverMinLabelHz:0} Hz cắt {low:0}–{high:0} Hz (so với vị trí {limits.CrossoverMaxLabelHz:0} Hz) · chặn {limits.RejectionProbeHz:0} Hz ≥ {limits.MinRejectionDb:0} dB";
        if (!max.IsValid || !min.IsValid) return new(name, "INVALID", "Một vị trí crossover chưa đo hợp lệ.") { Expected = expected };
        double corner = SubwooferControlAnalysis.RelativeCornerHz(max, min), wideCorner = SubwooferControlAnalysis.UpperCornerHz(max);
        double rejection = SubwooferControlAnalysis.LevelAt(max, limits.RejectionProbeHz) - SubwooferControlAnalysis.LevelAt(min, limits.RejectionProbeHz);
        double passband = SubwooferControlAnalysis.LevelAt(min, limits.PassbandProbeHz) - SubwooferControlAnalysis.LevelAt(max, limits.PassbandProbeHz);
        string measured = $"vị trí {limits.CrossoverMinLabelHz:0} Hz cắt tại {Hz(corner)} · chặn {limits.RejectionProbeHz:0} Hz {rejection:0.0} dB · "
            + $"{limits.PassbandProbeHz:0} Hz lệch {passband:+0.0;-0.0} dB · vị trí {limits.CrossoverMaxLabelHz:0} Hz: −6 dB âm học tại {Hz(wideCorner)} (gồm loa/thùng)";
        SubwooferCheck R(string verdict, string message) => new(name, verdict, message) { Measured = measured, Expected = expected };
        if (!double.IsFinite(rejection) || rejection < limits.MinRejectionDb)
            return R("FAIL", $"Núm CROSSOVER không có tác dụng: vặn về {limits.CrossoverMinLabelHz:0} Hz chỉ chặn {limits.RejectionProbeHz:0} Hz {rejection:0.0} dB (cần ≥ {limits.MinRejectionDb:0}).");
        if (!double.IsFinite(passband) || Math.Abs(passband) > limits.MaxPassbandChangeDb)
            return R("FAIL", $"Mức {limits.PassbandProbeHz:0} Hz đổi {passband:+0.0;-0.0} dB: núm CROSSOVER đang làm giảm cả dải trầm như núm âm lượng.");
        if (!double.IsFinite(corner) || corner < low || corner > high)
            return R("REVIEW", $"Núm CROSSOVER có tác dụng nhưng vị trí {limits.CrossoverMinLabelHz:0} Hz cắt tại {Hz(corner)}, lệch nhãn quá {limits.CornerToleranceRatio:0.#}×; so với loa tốt cùng gá.");
        return R("PASS", $"Núm CROSSOVER đạt: vị trí {limits.CrossoverMinLabelHz:0} Hz cắt tại {Hz(corner)}, chặn {limits.RejectionProbeHz:0} Hz {rejection:0} dB.");
    }

    /// <summary>A step judged against step 1 must change only its own control; other controls left elsewhere show up as a
    /// level change (crossover) or an extra delay (delay knob). Those runs are INVALID with a fix-it message, never FAIL.</summary>
    static string? Misplaced(SubwooferRelation rel, SubwooferControlLimits limits, bool delayExpected, string step)
    {
        var notes = new List<string>();
        if (Math.Abs(rel.LevelChangeDb) > limits.MaxLevelChangeDb)
            notes.Add($"mức đổi {rel.LevelChangeDb:+0.0;-0.0} dB so với bước 1 → CROSSOVER chưa về {limits.CrossoverMaxLabelHz:0} Hz (hoặc mic bị xê dịch)");
        if (!delayExpected && Math.Abs(rel.DelayMs) > limits.DelayToleranceMs)
            notes.Add($"trễ {rel.DelayMs:+0.0;-0.0} ms so với bước 1 → DELAY chưa về 0 ms");
        return notes.Count == 0 ? null : $"Đo {step} khi núm khác chưa đúng vị trí: " + string.Join("; ", notes) + $". Chỉnh lại theo hình rồi đo lại {step}.";
    }

    public static SubwooferCheck Delay(SubwooferResponse zero, SubwooferResponse max, SubwooferControlLimits limits)
    {
        const string name = "Time aligning delay";
        string expected = $"{limits.DelayMaxLabelMs:0} ± {limits.DelayToleranceMs:0.#} ms · pha giữ nguyên";
        var rel = SubwooferControlAnalysis.Relate(zero, max);
        if (!rel.IsValid) return new(name, "INVALID", rel.Reason) { Expected = expected, Relation = rel };
        string measured = $"trễ thêm {rel.DelayMs:0.0} ms · dao động {rel.JitterMs:0.0} ms · pha {rel.PhaseDeg:+0;-0}° · mức {rel.LevelChangeDb:+0.0;-0.0} dB";
        SubwooferCheck R(string verdict, string message) => new(name, verdict, message) { Measured = measured, Expected = expected, Relation = rel };
        if (Misplaced(rel, limits, delayExpected: true, "bước 3") is { } misplaced) return R("INVALID", misplaced);
        if (Math.Abs(rel.PhaseDeg) > limits.PhaseSameMaxDeg)
            return R("INVALID", $"Pha cũng đổi {rel.PhaseDeg:0}°: kiểm tra PHASE vẫn ở 0° (đèn 180° tắt) rồi đo lại bước 3.");
        if (rel.JitterMs > limits.MaxJitterMs)
            return R("INVALID", $"Trễ hệ thống dao động {rel.JitterMs:0.0} ms giữa các lượt (> {limits.MaxJitterMs:0.#} ms); chưa kết luận được delay. Đóng app khác dùng âm thanh rồi đo lại.");
        if (Math.Abs(rel.DelayMs - limits.DelayMaxLabelMs) <= limits.DelayToleranceMs)
            return rel.FitResidualDeg > limits.MaxFitResidualDeg
                ? R("REVIEW", $"Trễ đúng nhưng pha không tuyến tính (sai lệch {rel.FitResidualDeg:0}°): delay có thể không phải trễ thuần.")
                : R("PASS", "Núm DELAY trễ đúng dải nhãn.");
        return Math.Abs(rel.DelayMs) < 2
            ? R("FAIL", "Núm DELAY không có tác dụng (trễ không đổi).")
            : R("FAIL", $"Trễ thêm {rel.DelayMs:0.0} ms, khác nhãn {limits.DelayMaxLabelMs:0} ms quá {limits.DelayToleranceMs:0.#} ms.");
    }

    public static SubwooferCheck Phase(SubwooferResponse normal, SubwooferResponse inverted, bool? ledOn, SubwooferControlLimits limits)
    {
        const string name = "Phase 180°";
        string expected = $"pha đảo ≥ {limits.PhaseInvertedMinDeg:0}° · mức ±{limits.MaxLevelChangeDb:0} dB · đèn 180° sáng";
        var rel = SubwooferControlAnalysis.Relate(normal, inverted);
        if (!rel.IsValid) return new(name, "INVALID", rel.Reason) { Expected = expected, Relation = rel };
        string measured = $"pha {rel.PhaseDeg:+0;-0}° · mức {rel.LevelChangeDb:+0.0;-0.0} dB · trễ {rel.DelayMs:+0.0;-0.0} ms · đèn {Led(ledOn)}";
        SubwooferCheck R(string verdict, string message) => new(name, verdict, message) { Measured = measured, Expected = expected, Relation = rel };
        if (Misplaced(rel, limits, delayExpected: false, "bước 4") is { } misplaced) return R("INVALID", misplaced);
        double phase = Math.Abs(rel.PhaseDeg);
        if (phase <= limits.PhaseSameMaxDeg) return R("FAIL", "Nút PHASE không đảo pha.");
        if (phase < limits.PhaseInvertedMinDeg) return R("REVIEW", $"Pha lệch {rel.PhaseDeg:0}°: không phải 0° hay 180°; xem đồ thị pha, đo lại hoặc kiểm tra mạch pha.");
        if (ledOn == false) return R("FAIL", "Pha đã đảo nhưng đèn 180° không sáng.");
        return R("PASS", "Nút PHASE đảo pha 180°" + (ledOn == true ? ", đèn sáng." : "."));
    }

    public static SubwooferCheck Return(SubwooferResponse baseline, SubwooferResponse back, bool? ledOff, SubwooferControlLimits limits)
    {
        const string name = "Trả về ban đầu";
        string expected = $"giống bước 1: trễ ±{limits.ReturnDelayToleranceMs:0.#} ms · mức ±{limits.ReturnLevelToleranceDb:0.#} dB · đèn 180° tắt";
        var rel = SubwooferControlAnalysis.Relate(baseline, back);
        if (!rel.IsValid) return new(name, "INVALID", rel.Reason) { Expected = expected, Relation = rel };
        string measured = $"trễ {rel.DelayMs:+0.0;-0.0} ms · pha {rel.PhaseDeg:+0;-0}° · mức {rel.LevelChangeDb:+0.0;-0.0} dB · đèn {Led(ledOff == null ? null : !ledOff)}";
        SubwooferCheck R(string verdict, string message) => new(name, verdict, message) { Measured = measured, Expected = expected, Relation = rel };
        if (Math.Abs(rel.PhaseDeg) >= limits.PhaseInvertedMinDeg) return R("FAIL", "Bấm PHASE lần hai nhưng pha chưa trở về 0°.");
        if (ledOff == false) return R("FAIL", "Pha đã về 0° nhưng đèn 180° vẫn sáng.");
        var notes = new List<string>();
        if (Math.Abs(rel.PhaseDeg) > limits.PhaseSameMaxDeg) notes.Add($"pha lệch {rel.PhaseDeg:0}°");
        if (Math.Abs(rel.DelayMs) > limits.ReturnDelayToleranceMs) notes.Add($"trễ hệ thống trôi {rel.DelayMs:+0.0;-0.0} ms (kết quả delay chỉ tham khảo)");
        if (Math.Abs(rel.LevelChangeDb) > limits.ReturnLevelToleranceDb) notes.Add($"mức lệch {rel.LevelChangeDb:+0.0;-0.0} dB (núm chưa về đúng vị trí hoặc mic xê dịch)");
        return notes.Count == 0 ? R("PASS", "Trả về giống bước 1: núm/nút lặp lại được.")
            : R("REVIEW", "Chưa giống bước 1: " + string.Join("; ", notes) + ".");
    }

    static string Hz(double value) => double.IsFinite(value) ? $"{value:0} Hz" : "> 400 Hz";
    static string Led(bool? on) => on switch { true => "sáng", false => "tắt", _ => "không có" };
}
