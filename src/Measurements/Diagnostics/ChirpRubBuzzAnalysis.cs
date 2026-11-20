using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

/// <summary>chirp-rattle-1.0: industry-style R&amp;B chirp (log sine sweep over the bass–mid range) played N times.
/// For each instant the energy above 10× the sweep frequency (higher-order harmonics + buzz) is tracked; normal
/// driver distortion gives a smooth trend over frequency, a loose part resonating gives a LOCAL BUMP. A defect must
/// repeat, so the bump is the minimum over repeats. Validated live 2026-11-06 (artifact 20261106-085907-845Z):
/// rattling speaker +28 dB @232 Hz (10 s) / +23 dB @246 Hz (3 s), same unit below threshold 0 dB.</summary>
public sealed record ChirpRubBuzzSettings(double F1Hz = 20, double F2Hz = 2000, double DurationSeconds = 3, double PeakDbFs = -12, int Repeats = 3)
{
    public void Validate()
    {
        static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
        // f2 up to 20 kHz so a full line sweep can be analysed too; the rattle grid itself stops at 1.8 kHz (10·f ≤ 18 kHz).
        if (!In(F1Hz, 10, 100) || !In(F2Hz, 500, 20000) || F2Hz < 4 * F1Hz || !In(DurationSeconds, 1, 15) || !In(PeakDbFs, -40, -6) || Repeats is < 2 or > 5)
            throw new ArgumentException("Chirp Rè: f1 10–100 Hz, f2 500–20000 Hz, 1–15 s, đỉnh −40…−6 dBFS, 2–5 lượt.");
    }
}

public static class ChirpRubBuzzStimulus
{
    public static double[] Unit(ChirpRubBuzzSettings s, int rate)
    {
        double k = Math.Log(s.F2Hz / s.F1Hz); int n = (int)Math.Round(s.DurationSeconds * rate);
        var x = new double[n];
        for (int i = 0; i < n; i++) { double t = i / (double)rate; x[i] = Math.Sin(2 * Math.PI * s.F1Hz * s.DurationSeconds / k * (Math.Exp(t / s.DurationSeconds * k) - 1)); }
        return x;
    }
    public static float[] Generate(ChirpRubBuzzSettings settings, int sampleRate)
    {
        settings.Validate();
        if (!PinkRubBuzzStimulus.IsSupportedRate(sampleRate)) throw new ArgumentException("Chirp Rè phát ở 44100 hoặc 48000 Hz.");
        var unit = Unit(settings, sampleRate); double peak = Math.Pow(10, settings.PeakDbFs / 20); int fade = (int)(0.05 * sampleRate);
        var result = new float[unit.Length];
        for (int i = 0; i < unit.Length; i++)
        {
            double g = Math.Min(1, Math.Min(i, unit.Length - 1 - i) / (double)fade);
            result[i] = (float)(peak * unit[i] * 0.5 * (1 - Math.Cos(Math.PI * g)));
        }
        return result;
    }
    public static string Hash(ChirpRubBuzzSettings settings)
        => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(Generate(settings, PinkRubBuzzStimulus.SampleRate).AsSpan())));
    public static string Key(ChirpRubBuzzSettings settings, string routeContext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { algorithm = ChirpRubBuzzAnalysis.Version, stimulus = Hash(settings), settings, routeContext }))));
}

public sealed record ChirpRubBuzzMetrics(bool IsValid, string Reason)
{
    /// <summary>DriveDbFs is a relative level (sum of FFT power), only for comparing lượt and standard. "Đỉnh rè": largest consistent (min over repeats) local bump of the &gt;10·f distortion over its smooth trend (dB).</summary>
    public double PeakBumpDb { get; init; }
    public double PeakBumpHz { get; init; }
    public double ExcessAtPeakDb { get; init; }
    public double MaxExcessDb { get; init; }
    public double DriveDbFs { get; init; }
    public double DriveSpreadDb { get; init; }
    public double BackgroundRmsDbFs { get; init; }
    public double BackgroundDriftDb { get; init; }
    public int Repeats { get; init; }
    public double[] FrequenciesHz { get; init; } = [];
    public double[] BumpDb { get; init; } = [];
    public double[] HohdDb { get; init; } = [];
    public double[] ExcessDb { get; init; } = [];
    public double[] CaptureStartSeconds { get; init; } = [];
    public double[][] RepeatBumpDb { get; init; } = [];
    public double[][] RepeatExcessDb { get; init; } = [];
    [JsonIgnore] public bool HasSensitiveBins => IsValid && FrequenciesHz.Length > 0
        && ExcessDb.Length == FrequenciesHz.Length && ExcessDb.Any(x => x >= 3);
    [JsonIgnore] public bool HasMeasuredPeak => HasSensitiveBins && PeakBumpDb > 0;
    [JsonIgnore] public string GraphStatus => FrequenciesHz.Length == 0 || !IsValid
        ? "KHÔNG CÓ TÍN HIỆU ĐO HỢP LỆ" : "KHÔNG ĐỦ ĐỘ NHẠY";
}

public sealed record ChirpRubBuzzRun(ChirpRubBuzzSettings Settings, string ContextKey, string Route, int SampleRate, ChirpRubBuzzMetrics Metrics)
{
    [JsonIgnore] public bool PersistenceAllowed { get; init; } = ResultPersistencePolicy.CanPersist;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string StimulusHash { get; init; } = ChirpRubBuzzStimulus.Hash(Settings);
    public string Algorithm { get; init; } = ChirpRubBuzzAnalysis.Version;
    public string CaptureMode { get; init; } = "WASAPI Exclusive";
    public string Verdict { get; set; } = "REVIEW";
    public string Message { get; set; } = "Chưa so sánh.";
    public ChirpRubBuzzLimits? Limits { get; set; }
    public ChirpRubBuzzMetrics? StandardMetrics { get; set; }
    public DateTimeOffset? StandardCreatedUtc { get; set; }
    public double? DeltaBumpDb { get; set; }
    public double? DeltaBumpHz { get; set; }
    public bool SavedAsStandard { get; set; }
    public int PlaybackSampleRate { get; init; } = PinkRubBuzzStimulus.SampleRate;
    [JsonIgnore] public float[][] Captures { get; init; } = [];
    [JsonIgnore] public float[] Background { get; init; } = [];
    public bool IsValid => Metrics.IsValid;
    public string Summary => $"{Verdict}: {Message}" + (Metrics.HasMeasuredPeak
        ? $"{Environment.NewLine}Đỉnh rè {Metrics.PeakBumpDb:0.0} dB tại {Metrics.PeakBumpHz:0} Hz · cao tần trên 10·f vượt nền {Metrics.ExcessAtPeakDb:0.0} dB · mức cơ bản {Metrics.DriveDbFs:0.0} dB (tương đối)"
        : "");
}

public static class ChirpRubBuzzAnalysis
{
    public const string Version = "chirp-rattle-1.0.0";
    private static double PowerDb(double p) => 10 * Math.Log10(Math.Max(1e-30, p));
    private static double MeanSquare(ReadOnlySpan<float> x) { double s = 0; foreach (float v in x) s += (double)v * v; return x.Length == 0 ? 0 : s / x.Length; }

    private static int Lag(float[] capture, double[] reference)
    {
        int n = 1; while (n < capture.Length + reference.Length) n <<= 1;
        var a = new Complex[n]; var b = new Complex[n];
        for (int i = 0; i < capture.Length; i++) a[i] = capture[i];
        for (int i = 0; i < reference.Length; i++) b[i] = reference[i];
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < n; i++) a[i] *= Complex.Conjugate(b[i]);
        Fourier.Inverse(a, FourierOptions.Matlab);
        int best = 0; double value = double.NegativeInfinity;
        for (int i = 0; i < capture.Length; i++) { double v = Math.Abs(a[i].Real); if (v > value) { value = v; best = i; } }
        return best;
    }
    private sealed class Spectra(int rate)
    {
        public readonly int N = rate >= 64000 ? 4096 : 2048;
        public int Hop => N / 8;
        private double[]? _window;
        public double[] Window => _window ??= Enumerable.Range(0, N).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)).ToArray();
        public double[] Power(float[] x, int start)
        {
            var c = new Complex[N]; var w = Window;
            for (int i = 0; i < N; i++) c[i] = x[start + i] * w[i];
            Fourier.Forward(c, FourierOptions.Matlab);
            var p = new double[N / 2 + 1]; for (int i = 0; i < p.Length; i++) p[i] = c[i].Real * c[i].Real + c[i].Imaginary * c[i].Imaginary;
            return p;
        }
    }
    private static double Median(IEnumerable<double> values) { var a = values.Where(double.IsFinite).Order().ToArray(); return a.Length == 0 ? double.NaN : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }

    public static ChirpRubBuzzMetrics Analyze(IReadOnlyList<float[]> captures, float[] background, int rate, ChirpRubBuzzSettings settings,
        double backgroundRmsLimitDbFs = -50)
    {
        settings.Validate();
        ChirpRubBuzzMetrics Invalid(string reason) => new(false, reason) { Repeats = captures.Count };
        if (rate < 32000 || rate > 192000) return Invalid("Ngõ thu cần 32–192 kHz để phân tích tới 18 kHz.");
        if (captures.Count < 2) return Invalid("Cần ít nhất 2 lượt chirp để kiểm tra rè lặp lại.");
        if (captures.Any(c => c.Length < settings.DurationSeconds * rate)) return Invalid("Lượt thu chirp quá ngắn.");
        if (captures.Append(background).Any(c => c.Any(v => !float.IsFinite(v)))) return Invalid("Bản thu có NaN/Infinity.");
        if (captures.Any(c => c.Any(v => Math.Abs(v) >= .98f))) return Invalid("Clipping ngõ thu; giảm âm lượng hoặc mức chirp.");
        var reasons = new List<string>();
        int skip = Math.Min(background.Length, (int)(0.3 * rate));
        float[] bg = background[skip..];
        if (bg.Length < rate) return Invalid("Thu nền quá ngắn (cần ≥ 1,3 s).");
        double bgRms = PowerDb(MeanSquare(bg));
        double drift = bg.Length >= 2.2 * rate ? Math.Abs(PowerDb(MeanSquare(bg.AsSpan(0, rate))) - PowerDb(MeanSquare(bg.AsSpan(bg.Length - rate)))) : 0;
        if (bgRms < -100 || bgRms > backgroundRmsLimitDbFs || drift > 6 || bg.Count(v => v == 0) > bg.Length / 100)
            reasons.Add($"Nền thu bất thường (RMS {bgRms:0.0} dBFS, lệch đầu/cuối {drift:0.0} dB): đường thu bị Windows xử lý hoặc phòng ồn.");

        var sp = new Spectra(rate); double binHz = rate / (double)sp.N;
        var bgPower = new double[sp.N / 2 + 1]; int bgFrames = 0;
        for (int s = 0; s + sp.N <= bg.Length; s += sp.Hop) { var p = sp.Power(bg, s); for (int i = 0; i < p.Length; i++) bgPower[i] += p[i]; bgFrames++; }
        for (int i = 0; i < bgPower.Length; i++) bgPower[i] /= Math.Max(1, bgFrames);

        double T = settings.DurationSeconds, k = Math.Log(settings.F2Hz / settings.F1Hz), hfTop = Math.Min(18000, 0.45 * rate);
        double fMax = Math.Min(settings.F2Hz, Math.Min(1800, hfTop / 10));
        var grid = new List<double>(); for (double g = settings.F1Hz * 1.15; g <= fMax; g *= Math.Pow(2, 1 / 12.0)) grid.Add(g);
        var reference = ChirpRubBuzzStimulus.Unit(settings, rate);
        var hohd = new List<double[]>(); var excess = new List<double[]>(); var drive = new List<double>(); var snr = new List<double>();
        var captureStarts = new List<double>();
        foreach (var capture in captures)
        {
            int lag = Lag(capture, reference);
            captureStarts.Add(lag / (double)rate);
            var fr = new List<(double F, double Hi, double Fund, double Floor, double FundFloor)>();
            for (int s = 0; s + sp.N <= capture.Length; s += sp.Hop)
            {
                double tc = (s + sp.N / 2.0 - lag) / rate;
                if (tc <= 0.05 || tc >= T - 0.05) continue;
                double f = settings.F1Hz * Math.Exp(tc / T * k);
                int h0 = (int)Math.Ceiling(Math.Max(10 * f, 1000) / binHz), h1 = (int)Math.Floor(hfTop / binHz);
                int u0 = (int)Math.Ceiling(f / 1.26 / binHz), u1 = (int)Math.Floor(f * 1.26 / binHz);
                if (h0 > h1 || u1 < Math.Max(1, u0)) continue;
                var p = sp.Power(capture, s);
                double hi = 0, floor = 0, fund = 0, fundFloor = 0;
                for (int i = h0; i <= h1; i++) { hi += p[i]; floor += bgPower[i]; }
                for (int i = Math.Max(1, u0); i <= u1; i++) { fund += p[i]; fundFloor += bgPower[i]; }
                fr.Add((f, hi, fund, floor, fundFloor));
            }
            if (fr.Count < 20) return Invalid("Không căn được chirp trong bản thu; kiểm tra route/loa/mic.") with { BackgroundRmsDbFs = bgRms };
            var h = new double[grid.Count]; var e = new double[grid.Count];
            for (int g = 0; g < grid.Count; g++)
            {
                double lo = grid[g] * Math.Pow(2, -1 / 24.0), up = grid[g] * Math.Pow(2, 1 / 24.0);
                var m = fr.Where(x => x.F >= lo && x.F < up).ToArray();
                if (m.Length == 0) { h[g] = e[g] = double.NaN; continue; }
                h[g] = PowerDb(m.Average(x => x.Hi)) - PowerDb(m.Average(x => x.Fund));
                e[g] = PowerDb(m.Average(x => x.Hi)) - PowerDb(m.Average(x => x.Floor));
            }
            hohd.Add(h); excess.Add(e);
            drive.Add(PowerDb(fr.Average(x => x.Fund)));
            snr.Add(PowerDb(fr.Average(x => x.Fund)) - PowerDb(fr.Average(x => x.FundFloor)));
        }
        if (snr.Min() < 20) reasons.Add($"Chirp tại mic yếu (SNR {snr.Min():0.0} dB < 20); kiểm tra route/âm lượng/mic.");
        if (drive.Max() - drive.Min() > 1.5) reasons.Add($"Mức chirp giữa các lượt lệch {drive.Max() - drive.Min():0.0} dB > 1,5 dB; loa/mic bị xê dịch.");
        // Bump per repeat over its own trend (running median ±1/2 octave) where HF is measurably above the room;
        // a defect must repeat → consistent bump = minimum over repeats (room events appear in one lượt only).
        double[] Bump(double[] h, double[] e) => Enumerable.Range(0, h.Length).Select(i =>
        {
            double trend = Median(h.Skip(Math.Max(0, i - 6)).Take(Math.Min(h.Length, i + 7) - Math.Max(0, i - 6)));
            return double.IsFinite(h[i]) && double.IsFinite(trend) && e[i] >= 3 ? h[i] - trend : 0;
        }).ToArray();
        var bumps = hohd.Select((h, i) => Bump(h, excess[i])).ToArray();
        var bump = Enumerable.Range(0, grid.Count).Select(i => bumps.Min(b => b[i])).ToArray();
        var minExcess = Enumerable.Range(0, grid.Count).Select(i => excess.Select(e => e[i]).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min()).ToArray();
        var meanHohd = Enumerable.Range(0, grid.Count).Select(i => { var v = hohd.Select(h => h[i]).Where(double.IsFinite).ToArray(); return v.Length == 0 ? double.NaN : PowerDb(v.Average(x => Math.Pow(10, x / 10))); }).ToArray();
        int peak = Array.IndexOf(bump, bump.Max());
        double Clean(double v) => double.IsFinite(v) ? v : 0;
        return new(reasons.Count == 0, reasons.Count == 0 ? "Bản thu hợp lệ." : string.Join(" ", reasons))
        {
            PeakBumpDb = bump[peak], PeakBumpHz = grid[peak], ExcessAtPeakDb = Clean(minExcess[peak]),
            MaxExcessDb = minExcess.Where(double.IsFinite).DefaultIfEmpty(0).Max(), DriveDbFs = PowerDb(drive.Average(d => Math.Pow(10, d / 10))),
            DriveSpreadDb = drive.Max() - drive.Min(), BackgroundRmsDbFs = bgRms, BackgroundDriftDb = drift, Repeats = captures.Count,
            FrequenciesHz = grid.ToArray(), BumpDb = bump, HohdDb = meanHohd.Select(Clean).ToArray(), ExcessDb = minExcess.Select(Clean).ToArray(),
            CaptureStartSeconds = captureStarts.ToArray(), RepeatBumpDb = bumps,
            RepeatExcessDb = excess.Select(e => e.Select(Clean).ToArray()).ToArray()
        };
    }
}

/// <summary>Allowed ratios for the chirp rattle test (checking_config RubBuzzChirpLimits).</summary>
public sealed record ChirpRubBuzzLimits(
    double BumpMarginDb = 6,      // with a line standard: FAIL when the bump exceeds the standard's at the same frequency by more
    double AbsolutePassDb = 6,    // without a standard: PASS at or below
    double AbsoluteFailDb = 10,   // without a standard: FAIL at or above
    double ReviewFraction = 0,    // 0 = strict PASS/FAIL
    double DriveMatchDb = 3)      // validity: chirp level at the mic vs the standard
{
    public void Validate()
    {
        static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
        if (!In(BumpMarginDb, 1, 40) || !In(AbsolutePassDb, 0, 40) || !In(AbsoluteFailDb, 0, 60) || AbsolutePassDb > AbsoluteFailDb
            || !In(ReviewFraction, 0, .9) || !In(DriveMatchDb, .5, 10))
            throw new ArgumentException("Ngưỡng Chirp Rè: đỉnh rè cho phép so với chuẩn 1–40 dB; ngưỡng tuyệt đối PASS ≤ FAIL; vùng duyệt 0–0,9; khớp mức 0,5–10 dB.");
    }
}

public sealed class ChirpRubBuzzStandard
{
    public string ContextKey { get; set; } = "";
    public string Algorithm { get; set; } = ChirpRubBuzzAnalysis.Version;
    public string StimulusHash { get; set; } = "";
    public ChirpRubBuzzSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; }
    public string Route { get; set; } = "";
    public string Source { get; set; } = "";
    public ChirpRubBuzzMetrics Metrics { get; set; } = new(false, "");
    public static string PathFor(string directory, string key)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Context key lỗi.");
        return Path.Combine(directory, key + ".chirp-standard.json");
    }
    public static ChirpRubBuzzStandard? Load(string directory, string key)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        string path = PathFor(directory, key);
        if (!File.Exists(path)) return null;
        var s = JsonSerializer.Deserialize<ChirpRubBuzzStandard>(File.ReadAllText(path)) ?? throw new InvalidDataException("File line chuẩn chirp trống.");
        if (s.ContextKey != key || s.Algorithm != ChirpRubBuzzAnalysis.Version || !s.Metrics.IsValid || s.Metrics.BumpDb.Length == 0)
            throw new InvalidDataException("Line chuẩn chirp sai context/thuật toán; lưu lại line chuẩn.");
        return s;
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathFor(directory, ContextKey), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static ChirpRubBuzzRun Compare(ChirpRubBuzzRun run, ChirpRubBuzzStandard? standard, ChirpRubBuzzLimits limits)
    {
        run.Limits = limits; var m = run.Metrics;
        if (!m.IsValid) { run.Verdict = "INVALID"; run.Message = m.Reason; return run; }
        if (standard != null && (standard.StimulusHash != run.StimulusHash || standard.Settings != run.Settings || standard.Metrics.BumpDb.Length != m.BumpDb.Length)) standard = null;
        if (standard != null)
        {
            run.StandardMetrics = standard.Metrics; run.StandardCreatedUtc = standard.CreatedUtc;
            double driveDelta = m.DriveDbFs - standard.Metrics.DriveDbFs;
            if (Math.Abs(driveDelta) > limits.DriveMatchDb)
            { run.Verdict = "INVALID"; run.Message = $"Mức chirp tại mic lệch line chuẩn {driveDelta:+0.0;-0.0} dB (> ±{limits.DriveMatchDb:0.#}): kiểm tra vị trí mic/loa, âm lượng."; return run; }
            int worst = 0; double delta = double.NegativeInfinity;
            for (int i = 0; i < m.BumpDb.Length; i++) { double d = m.BumpDb[i] - standard.Metrics.BumpDb[i]; if (d > delta) { delta = d; worst = i; } }
            run.DeltaBumpDb = delta; run.DeltaBumpHz = m.FrequenciesHz[worst];
            bool fail = delta > limits.BumpMarginDb, near = limits.ReviewFraction > 0 && delta > limits.BumpMarginDb * (1 - limits.ReviewFraction);
            run.Verdict = fail ? "FAIL" : near ? "REVIEW" : "PASS";
            run.Message = (fail ? "RÈ CHIRP VƯỢT CHO PHÉP · " : near ? "SÁT GIỚI HẠN, CẦN DUYỆT · " : "TRONG GIỚI HẠN · ")
                + $"đỉnh rè cao hơn chuẩn {delta:+0.0;-0.0} dB tại {m.FrequenciesHz[worst]:0} Hz (cho phép +{limits.BumpMarginDb:0.#} dB).";
            return run;
        }
        run.Verdict = m.PeakBumpDb >= limits.AbsoluteFailDb ? "FAIL" : m.PeakBumpDb <= limits.AbsolutePassDb ? "PASS" : "REVIEW";
        run.Message = $"Chưa có line chuẩn chirp: PASS ≤ {limits.AbsolutePassDb:0.#} dB, FAIL ≥ {limits.AbsoluteFailDb:0.#} dB · đỉnh rè {m.PeakBumpDb:0.0} dB tại {m.PeakBumpHz:0} Hz.";
        return run;
    }
    public static ChirpRubBuzzRun SaveAsStandard(ChirpRubBuzzRun run, ChirpRubBuzzLimits limits, string directory, string source)
    {
        run.Limits = limits;
        if (!run.IsValid) { run.Verdict = "INVALID"; run.Message = "Chưa lưu line chuẩn chirp: " + run.Metrics.Reason; return run; }
        var s = new ChirpRubBuzzStandard { ContextKey = run.ContextKey, Algorithm = run.Algorithm, StimulusHash = run.StimulusHash, Settings = run.Settings,
            CreatedUtc = DateTimeOffset.UtcNow, Route = run.Route, Source = source, Metrics = run.Metrics };
        s.Save(directory);
        run.SavedAsStandard = true; run.StandardMetrics = s.Metrics; run.StandardCreatedUtc = s.CreatedUtc; run.DeltaBumpDb = 0; run.Verdict = "PASS";
        run.Message = $"ĐÃ LƯU LINE CHUẨN RÈ CHIRP · đỉnh rè {run.Metrics.PeakBumpDb:0.0} dB tại {run.Metrics.PeakBumpHz:0} Hz. Chỉ lưu chuẩn trên loa tốt đã nghe kiểm tra.";
        return run;
    }
    public static string Export(ChirpRubBuzzRun run, string directory)
    {
        using var persistence = ResultPersistencePolicy.BeginResult(run.PersistenceAllowed);
        string folder = Path.Combine(directory, "captures", "chirp-" + run.CreatedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        ResultPersistencePolicy.CreateDirectory(folder);
        ResultPersistencePolicy.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, "background.wav"), run.Background, run.SampleRate);
        for (int i = 0; i < run.Captures.Length; i++) PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, $"mic-{i + 1}.wav"), run.Captures[i], run.SampleRate);
        PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, "stimulus.wav"), ChirpRubBuzzStimulus.Generate(run.Settings, PinkRubBuzzStimulus.SampleRate), PinkRubBuzzStimulus.SampleRate);
        return folder;
    }
}
