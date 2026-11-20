using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

/// <summary>IO009 "Gain trước hú" (owner 2026-11-18): how much more loop gain the speaker tolerates before howling with FBX on
/// than with FBX off ("added stable gain" / gain before feedback). The station closes the loop through the measurement mic:
/// mic → app gain → MIC IN → speaker → mic. No IEC/AES standard is claimed; the method compares the same loop with FBX off/on.</summary>
public sealed record FeedbackGainSettings
{
    public double StartGainDb { get; init; } = -30;
    public double MaximumGainDb { get; init; } = 24;
    public double StepDb { get; init; } = 1;
    public double StepSeconds { get; init; } = .25;
    /// <summary>Howl = a narrow peak this far above the spectrum median, at or above HowlLevelDbFs, for two blocks in a row.</summary>
    public double HowlMarginDb { get; init; } = 25;
    public double HowlLevelDbFs { get; init; } = -35;
    /// <summary>PASS when FBX on adds at least this many dB; null = report only (no threshold chosen yet).</summary>
    public double? MinimumAddedDb { get; init; }
    public void Validate()
    {
        static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
        if (!In(StartGainDb, -60, 20) || !In(MaximumGainDb, StartGainDb + 3, 40) || !In(StepDb, .25, 6) || !In(StepSeconds, .1, 2)
            || !In(HowlMarginDb, 10, 60) || !In(HowlLevelDbFs, -70, -6) || MinimumAddedDb is double m && !In(m, 0, 40))
            throw new ArgumentException("Gain trước hú: gain −60…+40 dB, bước 0,25–6 dB / 0,1–2 s, ngưỡng hú 10–60 dB, mức −70…−6 dBFS, ngưỡng thêm 0–40 dB.");
    }
}

public sealed record FeedbackGainRun(bool FbxEnabled, double? HowlGainDb, double HowlFrequencyHz, double PeakDbFs, IReadOnlyList<FeedbackGainStep> Steps)
{
    public bool Howled => HowlGainDb.HasValue;
}
public sealed record FeedbackGainStep(double GainDb, double LevelDbFs, double PeakToMedianDb, double PeakHz);
public sealed record FeedbackGainResult(FeedbackGainRun Off, FeedbackGainRun On, double MaximumGainDb)
{
    /// <summary>Added stable gain; when FBX on never howled it is a lower bound ("≥").</summary>
    public double? AddedDb => Off.HowlGainDb is double off ? (On.HowlGainDb ?? MaximumGainDb) - off : null;
    public bool AddedIsLowerBound => On.HowlGainDb == null;
}

public sealed class HowlDetector(int sampleRate, FeedbackGainSettings settings)
{
    public const int BlockSize = 4096;
    private int _consecutive;
    public FeedbackGainStep Last { get; private set; } = new(double.NaN, -200, 0, 0);
    /// <summary>Feeds one block of mono samples; true once a howl has been seen in two consecutive blocks.</summary>
    public bool Push(ReadOnlySpan<float> block, double gainDb)
    {
        var spectrum = new Complex[BlockSize];
        int n = Math.Min(block.Length, BlockSize);
        double energy = 0;
        for (int i = 0; i < n; i++)
        {
            double w = .5 - .5 * Math.Cos(2 * Math.PI * i / (BlockSize - 1));
            spectrum[i] = new Complex(block[i] * w, 0); energy += block[i] * (double)block[i];
        }
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        int lo = Math.Max(1, (int)(80.0 * BlockSize / sampleRate)), hi = Math.Min(BlockSize / 2 - 1, (int)(12000.0 * BlockSize / sampleRate));
        var magnitudes = new double[hi - lo];
        int peak = 0;
        for (int k = lo; k < hi; k++) { magnitudes[k - lo] = spectrum[k].Magnitude; if (magnitudes[k - lo] > magnitudes[peak]) peak = k - lo; }
        var sorted = magnitudes.ToArray(); Array.Sort(sorted);
        double median = Math.Max(sorted[sorted.Length / 2], 1e-12);
        double ratio = 20 * Math.Log10(Math.Max(magnitudes[peak], 1e-12) / median);
        double level = 10 * Math.Log10(Math.Max(energy / Math.Max(n, 1), 1e-20));
        Last = new(gainDb, level, ratio, (peak + lo) * (double)sampleRate / BlockSize);
        bool howl = level >= settings.HowlLevelDbFs && ratio >= settings.HowlMarginDb;
        _consecutive = howl ? _consecutive + 1 : 0;
        return _consecutive >= 2;
    }
}

/// <summary>How strong a howl is (owner 2026-11-19 "hú ít hay hú nhiều"): the narrow peak's height above the spectrum median and
/// its level at the mic, at the gain step where the howl was detected.</summary>
public sealed record HowlStrength(double GainDb, double PeakToMedianDb, double LevelDbFs, double FrequencyHz, string Grade);

public static class FeedbackGainDiagnostics
{
    /// <summary>Grade by height above the detection threshold: &lt; +10 dB nhẹ, &lt; +20 dB vừa, otherwise mạnh.</summary>
    public static string Grade(double peakToMedianDb, FeedbackGainSettings settings)
        => peakToMedianDb < settings.HowlMarginDb + 10 ? "hú nhẹ" : peakToMedianDb < settings.HowlMarginDb + 20 ? "hú vừa" : "hú mạnh";

    /// <summary>Strongest block at the step that howled; null when the run never howled.</summary>
    public static HowlStrength? Strength(FeedbackGainRun run, FeedbackGainSettings settings)
    {
        if (run.HowlGainDb is not double gain) return null;
        var at = run.Steps.Where(s => Math.Abs(s.GainDb - gain) < 1e-6).ToArray();
        if (at.Length == 0) return null;
        var peak = at.MaxBy(s => s.PeakToMedianDb)!;
        return new(gain, peak.PeakToMedianDb, at.Max(s => s.LevelDbFs), peak.PeakHz, Grade(peak.PeakToMedianDb, settings));
    }

    /// <summary>Highest narrow peak a run reached (how close a run that never howled came to the threshold).</summary>
    public static double? HighestPeakToMedianDb(FeedbackGainRun run) => run.Steps.Count == 0 ? null : run.Steps.Max(s => s.PeakToMedianDb);

    /// <summary>At the loop gain where FBX off started to howl: how much lower the narrow peak is with FBX on (dB, positive = FBX
    /// suppresses). Null without FBX-on blocks at that gain.</summary>
    public static double? SuppressionAtOffHowlDb(FeedbackGainResult result, FeedbackGainSettings settings)
    {
        if (Strength(result.Off, settings) is not { } off) return null;
        var on = result.On.Steps.Where(s => Math.Abs(s.GainDb - off.GainDb) < 1e-6).ToArray();
        return on.Length == 0 ? null : off.PeakToMedianDb - on.Max(s => s.PeakToMedianDb);
    }

    /// <summary>Owner 2026-11-19: a loud howl ("hú mạnh") in an optional IO009 run never changes the verdict but is added to the
    /// session note sent with the QA result. Reads the stored result lines, so it also works for a session resumed after restart.</summary>
    public static string? AppendHowlNote(string? operatorNote, IEnumerable<AutoTestCaseItem> cases)
    {
        var lines = cases.Where(c => c.Config?.SpeakerStage == "MicFeedbackGain")
            .SelectMany(c => c.MeasurementValues.Where(v => v.ThongSo.Contains("độ mạnh tiếng hú", StringComparison.Ordinal)
                    && v.GiaTri.StartsWith("hú mạnh", StringComparison.Ordinal))
                .Select(v => $"{c.Id} · {v.ThongSo.Replace(" · độ mạnh tiếng hú", "", StringComparison.Ordinal)}: {v.GiaTri}"))
            .ToArray();
        if (lines.Length == 0) return operatorNote;
        string howl = "HÚ LỚN (bài tùy chọn, không đổi kết luận): " + string.Join("; ", lines) + ".";
        if (operatorNote?.Contains(howl, StringComparison.Ordinal) == true) return operatorNote;
        string note = string.IsNullOrWhiteSpace(operatorNote) ? howl : operatorNote.TrimEnd() + Environment.NewLine + howl;
        return note.Length > 4000 ? note[..4000] : note;
    }

    public static string Describe(HowlStrength s)
        => $"{s.Grade}: đỉnh nổi {s.PeakToMedianDb:0.0} dB trên nền, mức {s.LevelDbFs:0.0} dBFS tại mic, {s.FrequencyHz:0} Hz";

    public static string Status(FeedbackGainResult result, FeedbackGainSettings settings)
        => !result.Off.Howled ? "INVALID"
            : settings.MinimumAddedDb is double minimum && result.AddedDb < minimum ? "FAIL" : "PASS";

    public static string Summary(FeedbackGainResult r, FeedbackGainSettings settings)
    {
        if (!r.Off.Howled)
            return $"FBX TẮT chưa hú tới gain vòng tối đa {settings.MaximumGainDb:+0;-0} dB: vòng mic → loa quá yếu (đặt mic đo gần/đối diện loa hơn hoặc tăng MIC trên loa) — chưa kết luận chống hú.";
        string on = r.On.Howled ? $"hú ở {r.On.HowlGainDb:+0.0;-0.0} dB ({r.On.HowlFrequencyHz:0} Hz{(Strength(r.On, settings) is { } onStrength ? ", " + onStrength.Grade + $" {onStrength.PeakToMedianDb:0} dB" : "")})"
            : $"chưa hú tới {settings.MaximumGainDb:+0;-0} dB{(HighestPeakToMedianDb(r.On) is double highest ? $" (đỉnh cao nhất {highest:0} dB, ngưỡng hú {settings.HowlMarginDb:0} dB)" : "")}";
        string offStrength = Strength(r.Off, settings) is { } strength ? ", " + strength.Grade + $" {strength.PeakToMedianDb:0} dB trên nền" : "";
        string suppression = SuppressionAtOffHowlDb(r, settings) is double reduced ? $" Tại gain đó FBX bật giảm đỉnh hú {reduced:+0.0;-0.0} dB." : "";
        string added = (r.AddedIsLowerBound ? "≥ " : "") + $"{r.AddedDb:+0.0;-0.0} dB";
        string verdict = settings.MinimumAddedDb is double m ? $" (yêu cầu ≥ {m:0.#} dB)"
            : " · GHI CHÚ: chưa đặt ngưỡng MinimumAddedDb nên chỉ báo số, chưa kết luận chống hú đạt/không đạt.";
        return $"Gain trước hú: FBX TẮT hú ở {r.Off.HowlGainDb:+0.0;-0.0} dB ({r.Off.HowlFrequencyHz:0} Hz{offStrength}); FBX BẬT {on}.{suppression} FBX thêm được {added}{verdict}";
    }

    /// <summary>Offline model of the loop: a resonance whose loop gain reaches 0 dB at <paramref name="howlAtDb"/>.</summary>
    public static float[] SimulatedBlock(int rate, double gainDb, double howlAtDb, double toneHz, int seed)
    {
        var random = new Random(seed); var block = new float[HowlDetector.BlockSize];
        double tone = gainDb >= howlAtDb ? .3 : .3 * Math.Pow(10, (gainDb - howlAtDb - 30) / 20);
        for (int i = 0; i < block.Length; i++)
            block[i] = (float)(tone * Math.Sin(2 * Math.PI * toneHz * i / rate) + .003 * (random.NextDouble() * 2 - 1));
        return block;
    }
}

public partial class TestConfig
{
    // Omitted when null: existing configurations, reference keys and resume keys keep their exact bytes.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FeedbackGainSettings? FeedbackGain { get; set; }
    /// <summary>Optional test (owner 2026-11-19, IO009): still measured, shown and uploaded, may be skipped, and never changes the
    /// overall PASS/FAIL nor blocks the upload. Null = default: optional only for the gain-before-feedback stage.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Optional { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOptional => Optional ?? SpeakerStage == "MicFeedbackGain";
}
