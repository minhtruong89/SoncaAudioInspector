using System;
using System.Collections.Generic;
using System.Linq;

namespace SoncaAudioInspector;

public sealed record DiagnosisResponsePoint(double FrequencyHz, double MeasuredDbFs, double TargetDbFs, double ToleranceDb);
public sealed record DiagnosisDistortionPoint(double FrequencyHz, double ThdPercent, double LimitPercent);

/// <summary>Local UI evidence only; never part of an upload or a product verdict.</summary>
public sealed record MeasurementDiagnosisEvidence
{
    public bool SignalTooWeak { get; init; }
    public bool Clipped { get; init; }
    public double SignalDbFs { get; init; } = double.NaN;
    public double NoiseDbFs { get; init; } = double.NaN;
    public double SnrDb { get; init; } = double.NaN;
    public bool ResponseValid { get; init; }
    public string AcquisitionIssue { get; init; } = "";
    public bool DistortionValid { get; init; }
    public bool HasComparableReference { get; init; }
    public bool ReliableImpulse { get; init; }
    public string Polarity { get; init; } = "UNKNOWN";
    public double ToneThdPercent { get; init; } = double.NaN;
    public double ToneThdNPercent { get; init; } = double.NaN;
    public IReadOnlyList<DiagnosisResponsePoint> Response { get; init; } = Array.Empty<DiagnosisResponsePoint>();
    public IReadOnlyList<DiagnosisDistortionPoint> Distortion { get; init; } = Array.Empty<DiagnosisDistortionPoint>();
}

/// <summary>Evidence and next checks, not a model-independent speaker acceptance verdict.</summary>
public static class SpeakerMeasurementDiagnosis
{
    public static IReadOnlyList<string> DescribeMeasurement(MeasurementDiagnosisEvidence evidence)
    {
        var notes = new List<string>();
        if (evidence.SignalTooWeak)
            notes.Add($"Tín hiệu quá yếu ({Level(evidence.SignalDbFs)}): kiểm tra mic IN 2, ngõ phát, mute, dây và mức tín hiệu.");
        if (evidence.Clipped)
            notes.Add("Ngõ thu bị clipping/quá tải: giảm mức phát hoặc gain phần cứng rồi đo lại; chưa quy méo cho loa.");
        bool noisy = double.IsFinite(evidence.NoiseDbFs) && evidence.NoiseDbFs > -55.0;
        if (noisy)
            notes.Add($"Nhiễu nền cao ({Level(evidence.NoiseDbFs)}): kiểm tra đường thu, mixer và soundcard; THD có thể sai.");
        if (!evidence.ResponseValid || evidence.SignalTooWeak || evidence.Clipped || noisy)
        {
            if (!string.IsNullOrWhiteSpace(evidence.AcquisitionIssue)) notes.Add(evidence.AcquisitionIssue);
            notes.Add("Bản thu chưa đủ tin cậy; chưa phán đoán lỗi loa hoặc đấu sai cực.");
            return notes;
        }

        var points = evidence.Response.Where(p => p.FrequencyHz >= 50 && p.FrequencyHz <= 18000
            && double.IsFinite(p.MeasuredDbFs) && double.IsFinite(p.TargetDbFs)).ToArray();
        if (evidence.HasComparableReference && points.Length >= 3)
        {
            double[] offsets = points.Select(p => p.MeasuredDbFs - p.TargetDbFs).OrderBy(v => v).ToArray();
            double offset = Median(offsets);
            double tolerance = Median(points.Select(p => Math.Max(0.1, p.ToleranceDb)).OrderBy(v => v).ToArray());
            bool mostlySameLevelChange = offsets.Count(v => Math.Abs(v - offset) <= 1.5) >= Math.Ceiling(points.Length * 0.8);
            if (Math.Abs(offset) > tolerance && mostlySameLevelChange)
                notes.Add($"Mức thu toàn dải {(offset < 0 ? "thấp" : "cao")} hơn line chuẩn khoảng {Math.Abs(offset):F1} dB: kiểm tra volume, gain, khoảng cách mic và đúng route.");
            var bands = new[] { (Name: "bass", Low: 50.0, High: 250.0), (Name: "mid", Low: 250.0, High: 4000.0), (Name: "treble", Low: 4000.0, High: 18001.0) };
            var bandsOutsideLimits = bands.Where(b => points.Any(p => p.FrequencyHz >= b.Low && p.FrequencyHz < b.High
                && Math.Abs(p.MeasuredDbFs - p.TargetDbFs) > p.ToleranceDb)).Select(b => b.Name).ToArray();
            if (bandsOutsideLimits.Length > 0 && !(mostlySameLevelChange && Math.Abs(offset) > tolerance))
                notes.Add($"Đáp tuyến lệch ở {string.Join(" / ", bandsOutsideLimits)}: kiểm tra vị trí mic, phản xạ, phân tần và củ loa; riêng biên độ chưa xác định được đấu sai cực.");
        }
        else
            notes.Add("Chưa có line chuẩn dBFS cùng cấu hình: cần tìm lại line để đánh giá mức âm lượng và đáp tuyến.");

        // The sign of one impulse is relative to the whole playback/capture chain.
        // Without a saved impulse polarity from the same route it cannot diagnose wiring.
        if (!evidence.DistortionValid)
            notes.Add("THD chưa đủ tin cậy; kiểm tra SNR, tần số, clock và chất lượng bản thu trước khi kết luận méo.");
        else
        {
            var over = evidence.Distortion.Where(p => double.IsFinite(p.ThdPercent) && p.ThdPercent > p.LimitPercent)
                .OrderByDescending(p => p.ThdPercent / Math.Max(0.001, p.LimitPercent)).FirstOrDefault();
            if (over != null)
                notes.Add($"THD {over.ThdPercent:F3}% tại {over.FrequencyHz:0} Hz vượt giới hạn {over.LimitPercent:F3}%: có thể quá mức phát hoặc phi tuyến ampli/củ loa. Thử giảm mức phát và đối chiếu loopback.");
            if (double.IsFinite(evidence.ToneThdNPercent) && double.IsFinite(evidence.ToneThdPercent)
                && evidence.ToneThdNPercent > Math.Max(0.1, evidence.ToneThdPercent * 2))
                notes.Add($"THD+N ({evidence.ToneThdNPercent:F3}%) cao hơn nhiều THD ({evidence.ToneThdPercent:F3}%): phần dư ngoài hài đáng kể; kiểm tra nhiễu nền, rung/rè và DSP.");
        }
        if (double.IsFinite(evidence.SnrDb) && evidence.SnrDb < 30)
            notes.Add($"SNR chỉ {evidence.SnrDb:F1} dB: các giá trị méo nhỏ dễ bị nhiễu che lấp.");
        if (notes.Count == 0)
            notes.Add("Chưa thấy dấu hiệu bất thường theo line chuẩn và giới hạn đang dùng; kết luận chỉ áp dụng cho cấu hình đo này.");
        return notes;
    }

    private static double Median(double[] sorted) => (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    private static string Level(double value) => double.IsFinite(value) ? $"{value:F1} dBFS" : "chưa xác định mức";

    public static string ToneValidity(ToneQualityMetrics tone, double expectedFrequency)
    {
        if (!tone.IsValid) return "INVALID_TONE_NOT_DETECTED";
        if (tone.PeakSample >= 0.995 || tone.ClippedSamplePercent > 0.01) return "INVALID_CLIPPING";
        if (Math.Abs(tone.FundamentalFrequencyHz - expectedFrequency) > Math.Max(5, expectedFrequency * 0.005))
            return "INVALID_WRONG_FREQUENCY";
        if (!double.IsFinite(tone.ThdPercent)) return "INVALID_HARMONIC_BANDWIDTH";
        return "DIAGNOSTIC";
    }

    public static string DescribeTone(ToneQualityMetrics tone, double expectedFrequency)
    {
        string validity = ToneValidity(tone, expectedFrequency);
        if (validity == "INVALID_CLIPPING")
            return "Ngõ thu quá tải: giảm gain phần cứng/volume rồi đo lại; chưa quy méo cho loa.";
        if (validity == "INVALID_HARMONIC_BANDWIDTH")
            return "Không đủ băng thông để đo hài H2: THD không khả dụng, không phải 0%.";
        if (validity != "DIAGNOSTIC")
            return "Chưa khóa được đúng tone đủ mạnh: kiểm tra ngõ phát/thu, kênh mic và DSP di tần; chưa chẩn đoán loa.";
        var strongest = tone.Harmonics.OrderByDescending(h => h.LevelDbc).FirstOrDefault();
        string evidence = $"{expectedFrequency:0} Hz: THD {tone.ThdPercent:F3}%, THD+N {tone.ThdNPercent:F3}%, SNR {tone.SnrDb:F1} dB; đo đến H{tone.Harmonics.Count + 1}. ";
        if (tone.ThdNPercent > Math.Max(0.1, tone.ThdPercent * 2))
            return evidence + "Phần dư ngoài hài chiếm ưu thế: kiểm tra nhiễu nền, rung/rè, DSP và độ ổn định tone; đo nền và chạy bass rè/rung để phân biệt.";
        if (strongest != null)
            return evidence + $"Hài trội H{strongest.Order} ({strongest.LevelDbc:F1} dBc). Nếu vượt golden cùng mức: nghi phi tuyến ampli/củ loa; giảm mức phát 6 dB và đo lại, kiểm tra ampli bằng loopback. Chưa xác định được bộ phận hỏng từ riêng THD.";
        return evidence + "Cần golden cùng model/mức để đánh giá bất thường.";
    }
}
