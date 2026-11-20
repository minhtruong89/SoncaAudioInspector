using System.Globalization;

namespace SoncaAudioInspector;

public enum RubBuzzCause { Mechanical, Resonance, DspLimiter, AmpClip, ElectronicNoise, Room, CapturePath }
public sealed record RubBuzzCauseDirection(RubBuzzCause Cause, bool Supports)
{
    public override string ToString() => (Supports ? "Ủng hộ " : "Chống ") + RubBuzzDiagnosis.CauseName(Cause);
}
public sealed record RubBuzzSign(string Key, string Name, string Measurement, string Comparison, string Meaning,
    RubBuzzCauseDirection[] Directions, string Strength, string? Caveat = null)
{
    public string CauseDirections => Directions.Length == 0 ? "Chưa phân biệt" : string.Join("; ", Directions.Select(d => d.ToString()));
}
public sealed record RubBuzzHypothesis(RubBuzzCause Cause, string Name, int Score, string Confidence,
    string[] SupportingSignKeys, string[] ContradictingSignKeys);
public sealed record RubBuzzDiagnosisResult(string Version, RubBuzzSign[] Signs, RubBuzzHypothesis[] Hypotheses,
    string[] Summary, string[] NextChecks, PinkDiagnosticEvidence? PinkEvidence = null, ChirpDiagnosticEvidence? ChirpEvidence = null)
{
    /// <summary>Short operator view (≤ 4 lines) of the same ranking: leading cause, its strongest signs, contrary signs, next step.
    /// Presentation only; signs, scores and the diagnostics version are unchanged.</summary>
    public string[] Brief
    {
        get
        {
            string Names(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => Signs.FirstOrDefault(s => s.Key == k) is { } s ? $"{s.Key} {s.Name}" : k));
            var lines = new List<string>();
            var top = Hypotheses.FirstOrDefault();
            if (top == null || top.Score < 3)
                lines.Add("Chưa đủ bằng chứng để nghiêng về nguyên nhân nào.");
            else
            {
                var close = Hypotheses.Skip(1).Where(h => h.Score >= 3 && top.Score - h.Score <= 1).Select(h => h.Name).ToArray();
                lines.Add($"Nghiêng về: {top.Name} (tin cậy {top.Confidence.ToLowerInvariant()}, điểm {top.Score:+0})"
                    + (close.Length > 0 ? " — ngang với " + string.Join(", ", close) : "") + ". Là giả thuyết, chưa khẳng định.");
                var strongest = top.SupportingSignKeys
                    .OrderByDescending(k => Signs.FirstOrDefault(s => s.Key == k)?.Strength switch { "Mạnh" => 3, "Vừa" => 2, "Yếu" => 1, _ => 0 }).ToArray();
                lines.Add("Dựa trên: " + Names(strongest.Take(3)) + (strongest.Length > 3 ? $" (+{strongest.Length - 3})" : "") + ".");
                if (top.ContradictingSignKeys.Length > 0) lines.Add("Trái chiều: " + Names(top.ContradictingSignKeys.Take(2)) + ".");
            }
            if (NextChecks.Length > 0) lines.Add("Làm tiếp: " + NextChecks[0]);
            return lines.ToArray();
        }
    }
    public string CopyableText => "KẾT LUẬN NHANH\n" + string.Join("\n", Brief) + "\n\nDẤU HIỆU & CHẨN ĐOÁN\n" + string.Join("\n", Signs.Select(s =>
        $"{s.Key} · {s.Name}: {s.Measurement}. {s.Comparison}. {s.Meaning} [{s.Strength}; {s.CauseDirections}]"
        + (string.IsNullOrWhiteSpace(s.Caveat) ? "" : $" Lưu ý: {s.Caveat}")))
        + "\n\nKhả năng (chưa khẳng định nguyên nhân):\n" + string.Join("\n", Hypotheses.Select(h =>
            $"• {h.Name}: {h.Confidence}, điểm {h.Score:+0;-0;0}; ủng hộ {string.Join(", ", h.SupportingSignKeys)}; chống {string.Join(", ", h.ContradictingSignKeys)}."))
        + "\n\nKiểm tra tiếp:\n" + string.Join("\n", NextChecks.Select(s => "• " + s));
}

/// <summary>Thresholds are diagnostics only and never participate in the saved Pink/Chirp standards or Auto Test verdicts.</summary>
public sealed record RubBuzzDiagnosisSettings(
    double EventAboveRoomDb = 10, double DrivePeakFraction = .8, double EventsAtPeaksForPct = 50,
    double EventsAtPeaksAgainstPct = 15, double QuietLoudFlatDb = 1.5, double QuietLoudMechanicalDb = 6,
    double RepeatDecreaseFraction = .3, double RepeatDriveSpreadDb = .2, double ImpulsiveKurtosis = 6,
    double NoiseKurtosisLow = 2.5, double NoiseKurtosisHigh = 3.5, double NarrowBumpDb = 6,
    double NarrowWidthOctaves = 1 / 3.0, double BroadAboveRoomDb = 3, double BroadSpanOctaves = 1,
    double TailWithinRoomDb = 1, double TailSlowMilliseconds = 150, double TailFastMilliseconds = 100,
    double MinimumHfExcessDb = 3, double MinimumBandSnrDb = 10, double LevelOnsetRcPct = 10,
    double LevelMechanicalSlope = 2, double LevelElectronicSlope = 1, double CompressionGainFraction = .7,
    double BroadbandHfContrastDb = 6)
{
    public void Validate()
    {
        double[] values = [EventAboveRoomDb, DrivePeakFraction, EventsAtPeaksForPct, EventsAtPeaksAgainstPct,
            QuietLoudFlatDb, QuietLoudMechanicalDb, RepeatDecreaseFraction, RepeatDriveSpreadDb, ImpulsiveKurtosis,
            NoiseKurtosisLow, NoiseKurtosisHigh, NarrowBumpDb, NarrowWidthOctaves, BroadAboveRoomDb, BroadSpanOctaves,
            TailWithinRoomDb, TailSlowMilliseconds, TailFastMilliseconds, MinimumHfExcessDb, MinimumBandSnrDb,
            LevelOnsetRcPct, LevelMechanicalSlope, LevelElectronicSlope, CompressionGainFraction, BroadbandHfContrastDb];
        if (values.Any(v => !double.IsFinite(v)) || EventAboveRoomDb < 3 || EventAboveRoomDb > 30
            || DrivePeakFraction is <= 0 or >= 1 || EventsAtPeaksAgainstPct < 0 || EventsAtPeaksForPct > 100
            || EventsAtPeaksAgainstPct >= EventsAtPeaksForPct || RepeatDecreaseFraction is <= 0 or >= 1
            || QuietLoudFlatDb <= 0 || QuietLoudMechanicalDb <= QuietLoudFlatDb || RepeatDriveSpreadDb <= 0
            || ImpulsiveKurtosis <= NoiseKurtosisHigh || NarrowWidthOctaves <= 0 || BroadSpanOctaves <= 0
            || TailSlowMilliseconds <= TailFastMilliseconds || CompressionGainFraction is <= 0 or >= 1
            || NoiseKurtosisLow <= 0 || NoiseKurtosisHigh <= NoiseKurtosisLow || NarrowBumpDb <= 0
            || BroadAboveRoomDb <= 0 || TailWithinRoomDb < 0 || TailFastMilliseconds <= 0 || MinimumHfExcessDb <= 0
            || MinimumBandSnrDb <= 0 || LevelOnsetRcPct is <= 0 or >= 100 || LevelMechanicalSlope <= LevelElectronicSlope
            || LevelElectronicSlope <= 0 || BroadbandHfContrastDb <= 0)
            throw new ArgumentException("Ngưỡng chẩn đoán Rè không hợp lệ.");
    }
}

/// <summary>Transparent evidence weighting: Mạnh=3, Vừa=2, Yếu=1, missing=0; each sign can vote once per cause.
/// Positive and negative evidence are retained separately. This is a triage score, not a probability or a defect verdict.</summary>
public static class RubBuzzDiagnosis
{
    public const string Version = "rubbuzz-diagnostics-1.0.0";
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string F(double value, string format = "0.0") => value.ToString(format, Vi);
    private static string Values(IEnumerable<double> values, string unit) => string.Join(" → ", values.Select(v => F(v))) + " " + unit;
    private static string Values(IEnumerable<double?> values, string unit) => string.Join(" → ", values.Select(v => v.HasValue ? F(v.Value) : "chưa tính")) + " " + unit;
    public static string CauseName(RubBuzzCause cause) => cause switch
    {
        RubBuzzCause.Mechanical => "Rè/va đập cơ khí", RubBuzzCause.Resonance => "Cộng hưởng chi tiết lỏng",
        RubBuzzCause.DspLimiter => "Limiter/DRC trong DSP", RubBuzzCause.AmpClip => "Clip ampli",
        RubBuzzCause.ElectronicNoise => "Nhiễu điện tử điều chế theo tín hiệu (xì qua tweeter)",
        RubBuzzCause.Room => "Tiếng ồn phòng", _ => "Đường thu bị Windows xử lý"
    };
    private static RubBuzzCauseDirection For(RubBuzzCause c) => new(c, true);
    private static RubBuzzCauseDirection Against(RubBuzzCause c) => new(c, false);
    private static RubBuzzSign Missing(string key, string name, string reason) => new(key, name, "Chưa có số đo", reason,
        "Chưa đủ dữ liệu để suy luận dấu hiệu này.", [], "Chưa đủ dữ liệu");

    public static RubBuzzDiagnosisResult Build(PinkRubBuzzRun? pink, ChirpRubBuzzRun? chirp,
        ChirpImpulsiveEvidence? impulsive = null, BroadbandDistortionEvidence? broadband = null,
        RubBuzzLevelDiagnostics? levels = null, CapturePathEnhancementStatus? capturePath = null,
        RubBuzzDiagnosisSettings? settings = null, PinkDiagnosticEvidence? pinkEvidence = null,
        ChirpDiagnosticEvidence? chirpEvidence = null)
    {
        settings ??= new(); settings.Validate(); var s = settings;
        string? pinkProblem = pink == null ? null : RubBuzzDiagnosticEvidence.PinkAcquisitionProblem(pink);
        string? chirpProblem = chirp == null ? null : RubBuzzDiagnosticEvidence.ChirpAcquisitionProblem(chirp);
        if (pinkProblem != null) pinkEvidence = null;
        if (chirpProblem != null) { chirpEvidence = null; impulsive = null; }
        pinkEvidence ??= pink == null ? null : RubBuzzDiagnosticEvidence.AnalyzePink(pink, s);
        chirpEvidence ??= chirp == null ? null : RubBuzzDiagnosticEvidence.AnalyzeChirp(chirp, s);
        var signs = new List<RubBuzzSign>(); var repeats = pinkEvidence?.Repeats ?? [];
        bool pinkAligned = repeats.Length > 0 && repeats.All(r => r.AlignmentCorrelation >= .5);
        string alignmentNote = "Căn theo tín hiệu phát danh định; các clock riêng có thể trôi, nên đây là dấu hiệu chẩn đoán.";

        // S1 counts events, not all samples or the time spent above the drive threshold.
        var peakPct = repeats.Where(r => r.EventsAtDrivePeaksPct.HasValue).Select(r => r.EventsAtDrivePeaksPct!.Value).ToArray();
        if (pinkAligned && peakPct.Length > 0)
        {
            double mean = peakPct.Average(); bool high = mean > s.EventsAtPeaksForPct, low = mean < s.EventsAtPeaksAgainstPct;
            signs.Add(new("S1", "HF tại đỉnh biên độ phát", Values(peakPct, "% xung tại đỉnh") + $"; TB {F(mean)} %",
                $"Xung: HF 2 ms > nền +{F(s.EventAboveRoomDb)} dB; đỉnh: |phát| 2 ms > {F(100 * s.DrivePeakFraction, "0")} % đỉnh toàn lượt; >{F(s.EventsAtPeaksForPct, "0")} % / <{F(s.EventsAtPeaksAgainstPct, "0")} %",
                high ? "Cao tần dồn vào đỉnh phát, phù hợp giới hạn biên độ hoặc hành trình."
                    : low ? "Cao tần không dồn vào đỉnh phát; dấu hiệu chống limiter hoặc clip tức thời." : "Phân bố xung theo biên độ chưa phân biệt rõ nguồn gây cao tần.",
                high ? [For(RubBuzzCause.DspLimiter), For(RubBuzzCause.AmpClip)] : low ? [Against(RubBuzzCause.DspLimiter), Against(RubBuzzCause.AmpClip)] : [],
                high || low ? "Vừa" : "Yếu", alignmentNote + " Limiter thích nghi và méo cơ khí có thể có phân bố khác."));
        }
        else signs.Add(Missing("S1", "HF tại đỉnh biên độ phát", peakPct.Length == 0 ? "Chưa có xung vượt nền +10 dB hoặc chưa có Pink." : "Tương quan căn tín hiệu < 0,5; không suy luận từ vị trí đỉnh."));

        if (repeats.Length > 0)
        {
            double difference = pinkEvidence!.QuietLoudDifferenceDb, quiet = repeats.Average(r => r.QuietHfExcessDb);
            bool flat = difference < s.QuietLoudFlatDb && quiet > 0, mechanical = difference >= s.QuietLoudMechanicalDb;
            signs.Add(new("S2", "Cao tần lúc bass nhỏ vs lớn", "Nhỏ: " + Values(repeats.Select(r => r.QuietHfExcessDb), "dB trên nền")
                + "; lớn: " + Values(repeats.Select(r => r.LoudHfExcessDb), "dB trên nền") + $"; chênh TB {F(difference)} dB",
                $"10 % bass nhỏ/lớn nhất, khung 50 ms; <{F(s.QuietLoudFlatDb)} dB / ≥{F(s.QuietLoudMechanicalDb)} dB",
                flat ? "Cao tần vẫn hiện khi bass nhỏ và ít đổi theo bass; phù hợp nhiễu điện tử hơn giới hạn hành trình."
                    : mechanical ? "Cao tần tăng mạnh khi bass lớn, ủng hộ nguồn cơ khí phụ thuộc hành trình."
                    : "Cao tần tăng theo bass nhưng chưa đạt ngưỡng phân biệt cơ khí và nhiễu điện tử.",
                flat ? [For(RubBuzzCause.ElectronicNoise), Against(RubBuzzCause.Mechanical)] : mechanical ? [For(RubBuzzCause.Mechanical)] : [],
                flat || mechanical ? "Vừa" : "Yếu", "Khung 50 ms không biểu diễn hành trình hoặc vận tốc màng loa; nền phòng ảnh hưởng chênh lệch."));
            bool decreasing = repeats.Length >= 3 && repeats.Zip(repeats.Skip(1), (a, b) => b.EventsPct <= a.EventsPct).All(x => x)
                && repeats[0].EventsPct > 0 && repeats[^1].EventsPct <= repeats[0].EventsPct * (1 - s.RepeatDecreaseFraction)
                && pinkEvidence.DriveSpreadDb <= s.RepeatDriveSpreadDb;
            signs.Add(new("S3", "Xu hướng qua các lượt", "Xung: " + Values(repeats.Select(r => r.EventsPct), "%")
                + "; HF: " + Values(repeats.Select(r => r.HfDbFs), "dBFS") + $"; bass lệch {F(pinkEvidence.DriveSpreadDb, "0.00")} dB",
                $"Giảm đơn điệu ≥{F(100 * s.RepeatDecreaseFraction, "0")} %, bass lệch ≤{F(s.RepeatDriveSpreadDb)} dB, ≥3 lượt",
                decreasing ? "Xung giảm dần trong khi bass ổn định; ủng hộ một nguồn cơ khí thay đổi sau rung lắc."
                    : "Chưa thấy xu hướng giảm qua các lượt đáp ứng đồng thời ngưỡng và độ ổn định bass.",
                decreasing ? [For(RubBuzzCause.Mechanical), Against(RubBuzzCause.DspLimiter)] : [], decreasing ? "Vừa" : "Yếu",
                "DSP thích nghi chậm hoặc nhiệt có thể tạo xu hướng tương tự; DSP cố định thường lặp lại với cùng đầu vào."));
        }
        else { signs.Add(Missing("S2", "Cao tần lúc bass nhỏ vs lớn", "Cần waveform Pink và nền.")); signs.Add(Missing("S3", "Xu hướng qua các lượt", "Cần ít nhất 3 lượt Pink và nền.")); }

        double? kurtosis = repeats.Length > 0 ? repeats.Average(r => r.Kurtosis) : null;
        bool impulsiveSign = kurtosis > s.ImpulsiveKurtosis || impulsive is { IsSensitive: true, RepeatableImpulsive: true }
            && impulsive.ConsensusCrestDb >= impulsive.CrestThresholdDb;
        bool noiseLike = kurtosis >= s.NoiseKurtosisLow && kurtosis <= s.NoiseKurtosisHigh && !impulsiveSign;
        if (kurtosis.HasValue || impulsive != null)
            signs.Add(new("S4", "Tính xung", (kurtosis.HasValue ? "Kurtosis Pink: " + Values(repeats.Select(r => r.Kurtosis), "") : "Kurtosis Pink: chưa có")
                + (impulsive?.ConsensusCrestDb is double crest ? $"; crest đồng thuận Chirp {F(crest)} dB @ {F(impulsive.WorstFrequencyHz ?? 0, "0")} Hz" : "; crest Chirp: chưa có"),
                $"Kurtosis >{F(s.ImpulsiveKurtosis, "0")}; nhiễu Gaussian ≈3; crest ≥{F(impulsive?.CrestThresholdDb ?? 13)} dB và vượt nền/lặp lại",
                impulsiveSign ? "Có thành phần xung, ủng hộ va đập/cọ cơ khí nhưng chưa xác định vị trí."
                    : noiseLike ? "Dạng gần nhiễu Gaussian, phù hợp nhiễu điện tử hoặc tiếng ồn phòng." : "Độ nhọn chưa đủ để phân biệt cơ khí với nhiễu.",
                impulsiveSign ? [For(RubBuzzCause.Mechanical)] : noiseLike ? [For(RubBuzzCause.ElectronicNoise), For(RubBuzzCause.Room)] : [],
                impulsiveSign ? "Vừa" : "Yếu", "Kurtosis cũng tăng bởi tiếng động phòng; cần tính lặp lại và độ nhạy để quy nguồn."));
        else signs.Add(Missing("S4", "Tính xung", "Cần waveform Pink hoặc high-pass bám tần số Chirp."));

        if (chirpEvidence != null)
        {
            var c = chirpEvidence; bool narrow = c.NarrowFrequencyHz.HasValue && c.NarrowWidthOctaves <= s.NarrowWidthOctaves;
            bool broad = c.BroadSpanOctaves >= s.BroadSpanOctaves;
            signs.Add(new("S5", "Dải tần (chirp)", (narrow ? $"Đỉnh {F(c.NarrowBumpDb ?? 0)} dB @ {F(c.NarrowFrequencyHz!.Value, "0")} Hz; rộng {F(c.NarrowWidthOctaves ?? 0, "0.00")} octave" : $"Đỉnh cục bộ {F(chirp?.Metrics.PeakBumpDb ?? 0)} dB; chưa có đỉnh hẹp đạt ngưỡng")
                + $"; HF 4–12 kHz > nền +{F(s.BroadAboveRoomDb)} dB từ {F(c.BroadStartHz, "0")}–{F(c.BroadEndHz, "0")} Hz ({F(c.BroadSpanOctaves, "0.00")} octave, min +{F(c.BroadMinExcessDb)} dB)",
                $"Hẹp: bump ≥{F(s.NarrowBumpDb)} dB, rộng ≤{F(s.NarrowWidthOctaves, "0.00")} octave; rộng: ≥{F(s.BroadSpanOctaves)} octave",
                narrow ? "Cộng hưởng hẹp lặp lại ủng hộ chi tiết lỏng quanh tần số đã đo."
                    : broad ? "Cao tần xuất hiện trên dải kích rộng; chỉ số đỉnh cục bộ có thể vẫn thấp." : "Chưa đủ bằng chứng về cộng hưởng hẹp hoặc nguồn cao tần dải rộng.",
                narrow ? [For(RubBuzzCause.Resonance)] : [], narrow ? "Mạnh" : broad ? "Vừa" : "Yếu",
                "Nguồn dải rộng có thể là cơ khí, điện tử hoặc DSP; riêng hình dạng dải chưa phân biệt được."));
        }
        else signs.Add(Missing("S5", "Dải tần (chirp)", "Cần Chirp và nền để đo dải HF 4–12 kHz."));

        var tails = repeats.Where(r => r.TailReturnMilliseconds.HasValue).Select(r => r.TailReturnMilliseconds!.Value).ToArray();
        if (tails.Length > 0)
        {
            // Median over repeats: one room event in a single tail must not decide the sign (06/11 replay: 50/50/700 ms).
            double tail = tails.Order().ElementAt(tails.Length / 2);
            bool slow = tail > s.TailSlowMilliseconds, fast = tail <= s.TailFastMilliseconds;
            signs.Add(new("S6", "Đuôi sau khi dừng", Values(tails, "ms về trong +1 dB nền"),
                $"Khung 50 ms, 2 khung yên liên tiếp; >{F(s.TailSlowMilliseconds, "0")} ms / ≤{F(s.TailFastMilliseconds, "0")} ms",
                slow ? "Đuôi kéo dài: có thể do hold/gate/DSP hoặc vang phòng." : fast ? "Cao tần tắt nhanh theo tín hiệu kích." : "Đuôi nằm giữa hai ngưỡng; chưa phân biệt rõ.",
                slow ? [For(RubBuzzCause.DspLimiter), For(RubBuzzCause.Room)] : [], slow ? "Vừa" : "Yếu",
                "Độ phân giải 50 ms; cần đủ đuôi thu và biết trễ âm học. Không thấy về nền trong bản thu ngắn là thiếu dữ liệu."));
        }
        else signs.Add(Missing("S6", "Đuôi sau khi dừng", "Chưa có 2 khung 50 ms về nền trong phần đuôi thu."));

        double? excess = pink == null ? pinkEvidence == null ? null : repeats.Average(r => r.HfDbFs) - pinkEvidence.BackgroundHfDbFs
            : pinkProblem == null ? pink.Metrics.IsValid ? pink.Metrics.HfDbFs - pink.Metrics.BackgroundHfDbFs
                : pinkEvidence == null ? null : repeats.Average(r => r.HfDbFs) - pinkEvidence.BackgroundHfDbFs : null;
        if (excess.HasValue || broadband != null)
        {
            bool weak = excess < s.MinimumHfExcessDb || broadband?.HighBandSnrDb < s.MinimumBandSnrDb;
            signs.Add(new("S7", "Độ nhạy", (excess.HasValue ? $"HF Pink trên nền +{F(excess.Value)} dB" : "Chưa có Pink")
                + (broadband?.HighBandSnrDb is double snr ? $"; SNR 4–12 kHz {F(snr)} dB" : "; SNR dải rộng: chưa có"),
                $"HF Pink ≥{F(s.MinimumHfExcessDb)} dB; SNR từng dải ≥{F(s.MinimumBandSnrDb)} dB",
                weak ? "Kết luận yếu — tăng mức trong giới hạn hoặc đặt mic gần để cải thiện độ nhạy." : "Độ nhạy tại dải đã đo đủ cho quan sát chẩn đoán.",
                weak ? [For(RubBuzzCause.Room)] : [], weak ? "Yếu" : "Vừa",
                "RC phụ thuộc SNR, không đo độ nặng của rè; phòng ồn có thể làm RC giảm dù nguồn cao tần không đổi."));
        }
        else signs.Add(Missing("S7", "Độ nhạy", pinkProblem ?? "Cần HF Pink/nền hoặc SNR dải rộng. RC luôn phụ thuộc SNR."));

        string mode = pink?.CaptureMode ?? chirp?.CaptureMode ?? "WASAPI Exclusive (bài Rè)";
        string rates = pink != null ? $"phát {pink.PlaybackSampleRate} / thu {pink.SampleRate} Hz" : chirp != null ? $"phát {chirp.PlaybackSampleRate} / thu {chirp.SampleRate} Hz" : "chưa có sample rate";
        bool processed = capturePath?.MayProcessShared == true;
        // Shared-path APOs cannot explain HF in an Exclusive Rè capture: warn, but do not vote for CapturePath then.
        bool exclusive = mode.Contains("Exclusive", StringComparison.OrdinalIgnoreCase);
        signs.Add(new("S8", "Đường thu", $"{mode}; {rates}; " + (capturePath?.ReadSucceeded != true ? "Audio enhancements chưa đọc được"
            : capturePath.EnhancementsOff ? "enhancements Off (Disable_SysFx=1)" : $"Disable_SysFx={capturePath.DisableSysFx?.ToString() ?? "không có"}; APO {string.Join(", ", capturePath.EffectNames)}"),
            "APO hiện diện và Disable_SysFx ≠1 → cảnh báo Shared; khác sample rate cần căn clock",
            processed ? "Windows có thể xử lý các bài thu Shared; Rè thu Exclusive không bị ảnh hưởng bởi APO Shared."
                : capturePath?.EnhancementsOff == true ? "Enhancements đã tắt; Rè thu Exclusive tránh APO Shared." : "Chưa xác nhận đầy đủ đường thu; Rè được thiết kế thu Exclusive.",
            processed && !exclusive ? [For(RubBuzzCause.CapturePath)] : exclusive ? [Against(RubBuzzCause.CapturePath)] : [],
            processed ? "Mạnh" : capturePath?.ReadSucceeded == true ? "Vừa" : "Chưa đủ dữ liệu",
            "Khác sample rate phát/thu không tự chứng minh có xử lý. Windows có thể bật lại enhancements khi đổi cổng USB; registry không phát hiện mọi DSP trong phần cứng."));

        if (levels is { Rows.Length: >= 2 } && levels.Rows.Count(r => r.IsUsable) >= 2)
        {
            var rows = levels.Rows.Where(r => r.IsUsable).ToArray(); double? slope = levels.SpeakerHfSlopeDbPerDb;
            bool threshold = levels.OnsetLevelDbFs > rows[0].StimulusRmsDbFs && rows[0].RepeatCoherencePct < s.LevelOnsetRcPct;
            bool mechanical = threshold || slope > s.LevelMechanicalSlope;
            bool electronic = levels.OnsetLevelDbFs == rows[0].StimulusRmsDbFs && slope <= s.LevelElectronicSlope;
            bool limiter = levels.LimiterKneeLevelDbFs.HasValue;
            // Compression alone shows a limiter exists, not that it makes the HF. Blame it only when the speaker HF jumps
            // faster than the bass above the knee; a clean limiter (14/11: HF flat while bass compresses) counts against.
            var above = limiter ? rows.Where(r => r.StimulusRmsDbFs >= levels.LimiterKneeLevelDbFs!.Value - 1e-9).ToArray() : [];
            double? kneeHfRise = above.Length >= 2 && above[0].SpeakerHfDbFs is double h0 && above[^1].SpeakerHfDbFs is double h1
                && above[0].BassDbFs is double b0 && above[^1].BassDbFs is double b1 ? (h1 - h0) - (b1 - b0) : null;
            bool limiterArtifacts = kneeHfRise >= 3, cleanLimiter = kneeHfRise <= 0;
            var directions = new List<RubBuzzCauseDirection>();
            if (mechanical) directions.Add(For(RubBuzzCause.Mechanical));
            if (electronic) { directions.Add(Against(RubBuzzCause.Mechanical)); directions.Add(For(RubBuzzCause.ElectronicNoise)); }
            if (limiterArtifacts) directions.Add(For(RubBuzzCause.DspLimiter));
            else if (cleanLimiter) directions.Add(Against(RubBuzzCause.DspLimiter));
            signs.Add(new("S9", "Ngưỡng kích, độ dốc, nén", "Mức: " + Values(rows.Select(r => r.StimulusRmsDbFs), "dBFS RMS")
                + "; RC: " + Values(rows.Select(r => r.RepeatCoherencePct), "%")
                + "; HF loa: " + string.Join(" → ", rows.Select(r => r.SpeakerHfDbFs is double value ? F(value) : "dưới nền"))
                + $" dBFS; độ dốc HF/bass {(slope.HasValue ? F(slope.Value, "0.00") : "chưa có")} dB/dB"
                + "; tăng bass từng bước: " + Values(levels.Steps.Select(x => x.BassGainDb), "dB")
                + (limiter ? $"; điểm gối khoảng {F(levels.LimiterKneeLevelDbFs!.Value)} dBFS" : "; chưa thấy điểm gối"),
                $"Khởi phát RC ≥{F(s.LevelOnsetRcPct, "0")} %; độ dốc >{F(s.LevelMechanicalSlope)} / ≤{F(s.LevelElectronicSlope)}; gain <{F(100 * s.CompressionGainFraction, "0")} % mức tăng dự kiến",
                (mechanical ? "Có ngưỡng khởi phát hoặc cao tần tăng nhanh hơn bass, ủng hộ cơ khí. " : electronic ? "Cao tần lặp lại đã có ở mức thấp nhất và tăng chậm hơn bass, phù hợp nhiễu điện tử hoặc hành vi bình thường. " : "Quan hệ theo mức chưa phân biệt rõ nguồn. ")
                + (limiter ? "Bass tăng ít hơn mức số, có dấu hiệu nén/limiter"
                    + (limiterArtifacts ? $"; cao tần tăng nhanh hơn bass {F(kneeHfRise!.Value)} dB sau điểm gối, nghi limiter/clip tạo cao tần."
                        : cleanLimiter ? "; cao tần không tăng theo khi bị nén — limiter nén sạch, không phải nguồn cao tần." : ".") : ""),
                directions.ToArray(), directions.Count > 0 ? "Vừa" : "Yếu",
                $"HF loa dùng HF đã bỏ tiếng động lạ rồi trừ công suất nền; gần nền thì sai số lớn. RC phụ thuộc SNR; khởi phát RC ≥{F(s.LevelOnsetRcPct, "0")} % là dấu hiệu, không phải ngưỡng lỗi vật lý. Chẩn đoán theo mức tới −12 dBFS RMS; cần đối chiếu loa tốt và loa lỗi cùng gá trước khi chọn mức đo."));
        }
        else signs.Add(Missing("S9", "Ngưỡng kích, độ dốc, nén", "Chạy Chẩn đoán theo mức (−30 / −24 / −18 / −12 dBFS RMS)."));

        if (broadband != null && broadband.IsSensitive && broadband.HighNlRelativeDb.HasValue && broadband.MidNlRelativeDb.HasValue)
        {
            bool high = broadband.HighNlRelativeDb.Value - broadband.MidNlRelativeDb.Value >= s.BroadbandHfContrastDb;
            bool pulse = broadband.HighNlKurtosis > s.ImpulsiveKurtosis;
            signs.Add(new("S10", "Méo không kết hợp (Pink dải rộng)", $"NL/output 4–12 kHz {F(broadband.HighNlRelativeDb.Value)} dB; 0,2–2 kHz {F(broadband.MidNlRelativeDb.Value)} dB; kurtosis NL {F(broadband.HighNlKurtosis ?? 0)}",
                $"HF NL cao hơn dải giữa ≥{F(s.BroadbandHfContrastDb)} dB và kurtosis >{F(s.ImpulsiveKurtosis, "0")}",
                high && pulse ? "Phần dư cao tần tăng và có tính xung, ủng hộ nguồn cơ khí." : "Phần dư dải rộng chưa cho đồng thời dấu hiệu cao tần tăng và tính xung.",
                high && pulse ? [For(RubBuzzCause.Mechanical)] : [], high && pulse ? "Vừa" : "Yếu",
                "NL là phần dư của mô hình tuyến tính tốt nhất; một tín hiệu tuần hoàn cố định không tách duy nhất mọi méo phi tuyến khỏi H(f)."));
        }
        else signs.Add(Missing("S10", "Méo không kết hợp (Pink dải rộng)", "Cần bài Pink dải rộng và SNR dải đủ nhạy (≥10 dB)."));

        // Weak sensitivity (HF only a few dB above the room): HF-derived signs are dominated by room noise, so they may
        // not outweigh the level/frequency evidence (06/11 ear-confirmed rattler: S2/S6 pointed away from mechanics).
        if (signs.Any(x => x.Key == "S7" && x.Strength == "Yếu"))
            for (int i = 0; i < signs.Count; i++)
                if (signs[i].Key is "S1" or "S2" or "S3" or "S4" or "S6" && signs[i].Strength is "Mạnh" or "Vừa")
                    signs[i] = signs[i] with { Strength = "Yếu", Caveat = (signs[i].Caveat == null ? "" : signs[i].Caveat + " ")
                        + "Độ nhạy yếu (S7): cao tần chỉ vượt nền vài dB nên dấu hiệu này chỉ tính trọng số Yếu." };
        int Weight(RubBuzzSign sign) => sign.Strength switch { "Mạnh" => 3, "Vừa" => 2, "Yếu" => 1, _ => 0 };
        var hypotheses = Enum.GetValues<RubBuzzCause>().Select(cause =>
        {
            var support = signs.Where(x => x.Directions.Any(d => d.Cause == cause && d.Supports)).ToArray();
            var against = signs.Where(x => x.Directions.Any(d => d.Cause == cause && !d.Supports)).ToArray();
            int score = support.Sum(Weight) - against.Sum(Weight);
            string confidence = score >= 6 && support.Length >= 3 && !signs.Any(x => x.Key == "S7" && x.Strength == "Yếu")
                ? "Cao" : score >= 3 && support.Length >= 2 ? "Trung bình" : "Thấp";
            string name = CauseName(cause) + (cause == RubBuzzCause.Resonance && chirpEvidence?.NarrowFrequencyHz is double hz ? $" tại {F(hz, "0")} Hz" : "");
            return new RubBuzzHypothesis(cause, name, score, confidence, support.Select(x => x.Key).ToArray(), against.Select(x => x.Key).ToArray());
        }).OrderByDescending(h => h.Score).ThenBy(h => h.Cause).ToArray();
        var next = new List<string>();
        if (processed) next.Add($"Tắt Audio enhancements cho {capturePath!.EndpointName}, đo lại các bài Shared; Rè Exclusive không bị ảnh hưởng.");
        if (signs.Any(x => x.Key == "S7" && x.Strength == "Yếu")) next.Add("Đặt mic gần hơn, giữ cùng gá và mức phát an toàn, thu nền lại để cải thiện SNR.");
        next.Add("Đặt mic sát tweeter rồi sát loa bass/cổng hơi với cùng mức phát để định vị nguồn cao tần.");
        if (levels == null) next.Add("Chạy Chẩn đoán theo mức để kiểm tra ngưỡng kích, độ dốc HF/bass và nén.");
        next.Add("Đưa firmware về Flat: tắt Bass Boost/Enhancer, PEQ 0, rồi so cùng tín hiệu/mức.");
        next.Add("Thu điện tại cọc loa bằng giao diện/giảm áp phù hợp để kiểm tra phần ampli/DSP.");
        if (pink?.StandardMetrics == null && chirp?.StandardMetrics == null) next.Add("Đo loa tốt cùng model, cùng gá và mức làm line chuẩn để so tương đối.");
        var summary = signs.Where(x => x.Strength is "Mạnh" or "Vừa").Select(x => $"{x.Name}: {x.Measurement}. {x.Meaning}"
            + (x.Caveat == null ? "" : " Lưu ý: " + x.Caveat)).ToList();
        summary.Add("Các hướng nguyên nhân là giả thuyết, chưa xác nhận chắc chắn; điểm là tổng bằng chứng thuận/trái, không phải xác suất.");
        return new(Version, signs.ToArray(), hypotheses, summary.ToArray(), next.Distinct().ToArray(), pinkEvidence, chirpEvidence);
    }
}
