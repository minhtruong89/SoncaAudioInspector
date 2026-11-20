using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

/// <summary>Allowed ratios for PASS/FAIL (user tolerance). Stored per test in checking_config (RubBuzzPinkLimits).</summary>
public sealed record PinkRubBuzzLimits(
    double RepeatMarginPct = 15,   // allowed increase of "tỷ lệ rè lặp lại" over the line standard (percentage points)
    double HfMarginDb = 3,         // allowed increase of 4–12 kHz energy over the line standard (dB)
    double ReviewFraction = 0,     // 0 = strict PASS/FAIL; e.g. 0.2 = REVIEW within the last 20 % below a limit
    double AbsolutePassPct = 15,   // without a line standard: PASS at or below
    double AbsoluteFailPct = 30,   // without a line standard: FAIL at or above
    double DriveMatchDb = 3,       // validity: bass at mic must match the standard within ± dB
    double MinSensitivityDb = 45,  // validity: bass at mic minus background 4–12 kHz
    double MaxForeignPct = 35)     // validity: max share of time removed as foreign noise (live session 2: 24 %)
{
    public void Validate()
    {
        static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
        if (!In(RepeatMarginPct, 1, 100) || !In(HfMarginDb, .5, 30) || !In(ReviewFraction, 0, .9) || !In(AbsolutePassPct, 0, 100)
            || !In(AbsoluteFailPct, 0, 100) || AbsolutePassPct > AbsoluteFailPct || !In(DriveMatchDb, .5, 10)
            || !In(MinSensitivityDb, 20, 90) || !In(MaxForeignPct, 0, 60))
            throw new ArgumentException("Ngưỡng Pink Rè: rè lặp lại cho phép 1–100 điểm %; cao tần cho phép 0,5–30 dB; vùng duyệt 0–0,9; "
                + "ngưỡng tuyệt đối PASS ≤ FAIL trong 0–100 %; khớp bass 0,5–10 dB; độ nhạy 20–90 dB; tiếng động lạ ≤ 60 %.");
    }
}

/// <summary>"Line chuẩn rè": metrics of a known-good unit on the same route/jig, saved with the line standard.
/// Station data (standards/pink-rubbuzz), preserved by app updates.</summary>
public sealed class PinkRubBuzzStandard
{
    public string ContextKey { get; set; } = "";
    public string Algorithm { get; set; } = PinkRubBuzzAnalysis.Version;
    public string StimulusHash { get; set; } = "";
    public PinkRubBuzzSettings Settings { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; }
    public string Route { get; set; } = "";
    public string Source { get; set; } = "";
    public PinkRubBuzzMetrics Metrics { get; set; } = new(false, "");

    public static string PathFor(string directory, string key)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Context key lỗi.");
        return Path.Combine(directory, key + ".standard.json");
    }
    public static PinkRubBuzzStandard? Load(string directory, string key)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        string path = PathFor(directory, key);
        if (!File.Exists(path)) return null;
        var standard = JsonSerializer.Deserialize<PinkRubBuzzStandard>(File.ReadAllText(path)) ?? throw new InvalidDataException("File line chuẩn rè trống.");
        if (standard.ContextKey != key || standard.Algorithm != PinkRubBuzzAnalysis.Version || !standard.Metrics.IsValid
            || !double.IsFinite(standard.Metrics.RepeatCoherencePct) || !double.IsFinite(standard.Metrics.HfDbFs) || !double.IsFinite(standard.Metrics.DriveDbFs))
            throw new InvalidDataException("Line chuẩn rè sai context/thuật toán hoặc số liệu lỗi; lưu lại line chuẩn.");
        return standard;
    }
    public static PinkRubBuzzStandard FromRun(PinkRubBuzzRun run, string source)
    {
        if (!run.IsValid) throw new InvalidOperationException("Chỉ lưu line chuẩn rè từ bản thu hợp lệ.");
        return new() { ContextKey = run.ContextKey, Algorithm = run.Algorithm, StimulusHash = run.StimulusHash, Settings = run.Settings,
            CreatedUtc = DateTimeOffset.UtcNow, Route = run.Route, Source = source, Metrics = run.Metrics with { Envelope = [] } };
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(PathFor(directory, ContextKey), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Limit-dependent validity (shared by Compare and by saving a standard). Empty = acceptable.</summary>
    public static List<string> Validity(PinkRubBuzzMetrics m, PinkRubBuzzLimits limits, bool checkForeign = true)
    {
        var reasons = new List<string>();
        if (!m.IsValid) { reasons.Add(m.Reason); return reasons; }
        if (checkForeign && TooMuchForeign(m, limits)) reasons.Add($"Tiếng động lạ chiếm {100 - m.KeptPct:0}% thời gian (> {limits.MaxForeignPct:0}%); giữ yên và đo lại.");
        if (m.SensitivityDb < limits.MinSensitivityDb) reasons.Add($"Độ nhạy {m.SensitivityDb:0.0} dB < {limits.MinSensitivityDb:0} dB: phòng ồn hoặc mic xa loa.");
        return reasons;
    }
    private static bool TooMuchForeign(PinkRubBuzzMetrics m, PinkRubBuzzLimits limits) => 100 - m.KeptPct > limits.MaxForeignPct;

    public static PinkRubBuzzRun Compare(PinkRubBuzzRun run, PinkRubBuzzStandard? standard, PinkRubBuzzLimits limits)
    {
        run.Limits = limits;
        var m = run.Metrics;
        if (standard != null && (standard.StimulusHash != run.StimulusHash || standard.Settings != run.Settings)) standard = null;
        var reasons = Validity(m, limits, checkForeign: false);
        if (standard != null && m.IsValid)
        {
            run.StandardMetrics = standard.Metrics; run.StandardCreatedUtc = standard.CreatedUtc;
            double driveDelta = m.DriveDbFs - standard.Metrics.DriveDbFs, backgroundDelta = m.BackgroundHfDbFs - standard.Metrics.BackgroundHfDbFs;
            if (Math.Abs(driveDelta) > limits.DriveMatchDb)
                reasons.Add($"Bass tại mic lệch line chuẩn {driveDelta:+0.0;-0.0} dB (> ±{limits.DriveMatchDb:0.#}): kiểm tra vị trí mic/loa, gain, âm lượng.");
            if (backgroundDelta > 3) reasons.Add($"Nền cao tần cao hơn lúc lưu chuẩn {backgroundDelta:+0.0} dB: phòng ồn hơn, đo lại.");
        }
        if (reasons.Count > 0) { run.Verdict = "INVALID"; run.Message = string.Join(" ", reasons); return run; }
        if (TooMuchForeign(m, limits))
        {
            // Frequent non-repeating HF: a quiet room before playback + HF of all frames above the good unit on every
            // lượt = rattle that does not repeat (loose part bouncing). Otherwise the room was noisy: re-measure.
            double dAll = standard == null ? double.NaN : m.HfAllDbFs - standard.Metrics.HfAllDbFs;
            if (standard != null && dAll > limits.HfMarginDb)
            {
                run.StandardMetrics = standard.Metrics; run.DeltaHfDb = dAll; run.Verdict = "FAIL";
                run.Message = $"RÈ KHÔNG LẶP (va lung tung) · cao tần 4–12 kHz toàn bộ lượt {dAll:+0.0} dB so với chuẩn (cho phép +{limits.HfMarginDb:0.#} dB), "
                    + $"{100 - m.KeptPct:0}% thời gian không lặp giữa các lượt. Nghe lại để loại trừ tiếng động liên tục trong phòng.";
                return run;
            }
            run.Verdict = "INVALID";
            run.Message = $"Tiếng động lạ chiếm {100 - m.KeptPct:0}% thời gian (> {limits.MaxForeignPct:0}%); giữ yên và đo lại.";
            return run;
        }
        if (standard != null)
        {
            double dr = m.RepeatCoherencePct - standard.Metrics.RepeatCoherencePct, dh = m.HfDbFs - standard.Metrics.HfDbFs;
            run.DeltaRepeatPct = dr; run.DeltaHfDb = dh;
            bool fail = dr > limits.RepeatMarginPct || dh > limits.HfMarginDb;
            bool near = limits.ReviewFraction > 0 && (dr > limits.RepeatMarginPct * (1 - limits.ReviewFraction) || dh > limits.HfMarginDb * (1 - limits.ReviewFraction));
            run.Verdict = fail ? "FAIL" : near ? "REVIEW" : "PASS";
            run.Message = (fail ? "RÈ VƯỢT CHO PHÉP · " : near ? "SÁT GIỚI HẠN, CẦN DUYỆT · " : "TRONG GIỚI HẠN · ")
                + $"rè lặp lại {PinkRubBuzzMetrics.DisplayCoherence(m.RepeatCoherencePct)} (chuẩn {PinkRubBuzzMetrics.DisplayCoherence(standard.Metrics.RepeatCoherencePct)}, {dr:+0.0;-0.0} điểm / cho phép +{limits.RepeatMarginPct:0.#}) · "
                + $"cao tần 4–12 kHz {dh:+0.0;-0.0} dB so với chuẩn (cho phép +{limits.HfMarginDb:0.#} dB).";
            return run;
        }
        run.Verdict = m.RepeatCoherencePct >= limits.AbsoluteFailPct ? "FAIL" : m.RepeatCoherencePct <= limits.AbsolutePassPct ? "PASS" : "REVIEW";
        run.Message = $"Chưa có line chuẩn rè: dùng ngưỡng tuyệt đối PASS ≤ {limits.AbsolutePassPct:0.#}%, FAIL ≥ {limits.AbsoluteFailPct:0.#}% · "
            + $"rè lặp lại {PinkRubBuzzMetrics.DisplayCoherence(m.RepeatCoherencePct)}. Nên lưu line chuẩn trên loa tốt cùng gá để so chính xác.";
        return run;
    }

    /// <summary>Saves the run as the line standard after the same validity checks; returns the run with verdict.</summary>
    public static PinkRubBuzzRun SaveAsStandard(PinkRubBuzzRun run, PinkRubBuzzLimits limits, string directory, string source)
    {
        run.Limits = limits;
        var reasons = Validity(run.Metrics, limits);
        if (reasons.Count > 0) { run.Verdict = "INVALID"; run.Message = "Chưa lưu line chuẩn rè: " + string.Join(" ", reasons); return run; }
        var standard = FromRun(run, source); standard.Save(directory);
        run.SavedAsStandard = true; run.StandardMetrics = standard.Metrics; run.StandardCreatedUtc = standard.CreatedUtc;
        run.DeltaRepeatPct = 0; run.DeltaHfDb = 0; run.Verdict = "PASS";
        run.Message = $"ĐÃ LƯU LINE CHUẨN RÈ · rè lặp lại {PinkRubBuzzMetrics.DisplayCoherence(run.Metrics.RepeatCoherencePct)} · cao tần 4–12 kHz {run.Metrics.HfDbFs:0.0} dBFS. "
            + "Chỉ lưu chuẩn trên loa tốt đã kiểm tra bằng tai.";
        return run;
    }

    public static string Export(PinkRubBuzzRun run, string directory)
    {
        using var persistence = ResultPersistencePolicy.BeginResult(run.PersistenceAllowed);
        string folder = Path.Combine(directory, "captures", run.CreatedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        ResultPersistencePolicy.CreateDirectory(folder);
        ResultPersistencePolicy.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, "background.wav"), run.Background, run.SampleRate);
        for (int i = 0; i < run.Captures.Length; i++) PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, $"mic-{i + 1}.wav"), run.Captures[i], run.SampleRate);
        PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, "stimulus.wav"), PinkRubBuzzStimulus.Generate(run.Settings, PinkRubBuzzStimulus.SampleRate), PinkRubBuzzStimulus.SampleRate);
        return folder;
    }
}
