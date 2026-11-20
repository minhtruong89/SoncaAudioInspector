using System.Globalization;
using System.Text.RegularExpressions;

namespace SoncaAudioInspector;

/// <summary>Owner 2026-11-19 (A1): single verdict policy for C360 Auto Test (screen, log, summary.json, server upload, retest).
/// 1) Overall FAIL as soon as >= 1 required test FAILs, even when other tests are INVALID/NO_SIGNAL/INCONCLUSIVE.
/// 2) Overall PASS only when every required test PASSes. Optional tests (e.g. IO009) never decide the verdict.
/// 3) No required FAIL + any unconcluded required test => INCOMPLETE (not uploaded).
/// 4) Unfinished auto-LINE-CHUẨN ("INCONCLUSIVE") is never counted as required FAIL.</summary>
public sealed record C360SuiteVerdict(
    string OverallStatus,
    bool CanUpload,
    bool Passed,
    bool HasRequiredFail,
    bool HasUnconcludedRequired,
    bool HasReview,
    IReadOnlyList<string> RequiredFailIds,
    IReadOnlyList<string> UnconcludedRequiredSummaries)
{
    public string DisplayText(bool findReference = false) => findReference
        ? (CanUpload && Passed ? "LINE CHUẨN ĐỦ" : "LINE CHUẨN CHƯA ĐỦ — KIỂM TRA KẾT QUẢ")
        : OverallStatus switch
        {
            "PASS" => "ĐẠT",
            "FAIL" => "KHÔNG ĐẠT",
            "REVIEW" => "CẦN DUYỆT — PINK RÈ / THAM CHIẾU",
            _ => "CHƯA ĐO ĐỦ — KIỂM TRA TÍN HIỆU"
        };
}

public static class C360VerdictPolicy
{
    public static bool IsPassStatus(string? status) =>
        status?.Trim().ToUpperInvariant() is "PASS" or "PASSED";

    public static bool IsFailStatus(string? status) =>
        status?.Trim().ToUpperInvariant() is "FAIL" or "FAILED";

    /// <summary>True when a stage result is genuinely FAIL (not an unfinished auto-LINE-CHUẨN capture).</summary>
    public static bool IsConcludedFail(C360ElectronicResult result) =>
        IsFailStatus(result.Status) && !result.MissingReference;

    public static bool IsUnconcludedStatus(string? status) =>
        status?.Trim().ToUpperInvariant() is "INVALID" or "NO_SIGNAL" or "INCONCLUSIVE" or "PENDING" or "REVIEW";

    public static C360SuiteVerdict Evaluate(
        IReadOnlyList<(TestConfig Config, C360ElectronicResult Result)> items,
        Func<TestConfig, C360ElectronicResult, string>? shortReasonSelector = null)
    {
        var required = items.Where(x => !x.Config.IsOptional).ToArray();
        var failIds = new List<string>();
        var unconcluded = new List<string>();
        bool hasReview = false;

        foreach (var (config, result) in required)
        {
            if (IsConcludedFail(result))
            {
                failIds.Add(config.id);
            }
            else if (!IsPassStatus(result.Status))
            {
                if (string.Equals(result.Status, "REVIEW", StringComparison.OrdinalIgnoreCase))
                    hasReview = true;
                string reason = shortReasonSelector?.Invoke(config, result) ?? ShortUnconcludedReason(config, result);
                unconcluded.Add($"{config.id} chưa kết luận ({reason})");
            }
        }

        bool hasRequiredFail = failIds.Count > 0;
        bool allRequiredPass = required.Length > 0 && failIds.Count == 0 && unconcluded.Count == 0;
        bool hasUnconcluded = unconcluded.Count > 0;

        if (hasRequiredFail)
            return new("FAIL", CanUpload: true, Passed: false, HasRequiredFail: true, HasUnconcludedRequired: hasUnconcluded, HasReview: hasReview, failIds, unconcluded);
        if (allRequiredPass)
            return new("PASS", CanUpload: true, Passed: true, HasRequiredFail: false, HasUnconcludedRequired: false, HasReview: false, failIds, unconcluded);
        return new(hasReview ? "REVIEW" : "INCOMPLETE", CanUpload: false, Passed: false, HasRequiredFail: false, HasUnconcludedRequired: hasUnconcluded, HasReview: hasReview, failIds, unconcluded);
    }

    public static C360SuiteVerdict Evaluate(IEnumerable<AutoTestCaseItem> rows)
    {
        var required = rows.Where(r => r.Config?.IsOptional != true).ToArray();
        var failIds = new List<string>();
        var unconcluded = new List<string>();
        bool hasReview = false;

        foreach (var row in required)
        {
            if (IsFailStatus(row.Status))
            {
                failIds.Add(row.Id);
            }
            else if (!IsPassStatus(row.Status))
            {
                if (string.Equals(row.Status, "REVIEW", StringComparison.OrdinalIgnoreCase))
                    hasReview = true;
                string reason = ShortUnconcludedReason(row);
                unconcluded.Add($"{row.Id} chưa kết luận ({reason})");
            }
        }

        bool hasRequiredFail = failIds.Count > 0;
        bool allRequiredPass = required.Length > 0 && failIds.Count == 0 && unconcluded.Count == 0;
        bool hasUnconcluded = unconcluded.Count > 0;

        if (hasRequiredFail)
            return new("FAIL", CanUpload: true, Passed: false, HasRequiredFail: true, HasUnconcludedRequired: hasUnconcluded, HasReview: hasReview, failIds, unconcluded);
        if (allRequiredPass)
            return new("PASS", CanUpload: true, Passed: true, HasRequiredFail: false, HasUnconcludedRequired: false, HasReview: false, failIds, unconcluded);
        return new(hasReview ? "REVIEW" : "INCOMPLETE", CanUpload: false, Passed: false, HasRequiredFail: false, HasUnconcludedRequired: hasUnconcluded, HasReview: hasReview, failIds, unconcluded);
    }

    public static string ShortUnconcludedReason(TestConfig config, C360ElectronicResult result)
    {
        if (result.MissingReference || string.Equals(result.Status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase))
        {
            string line = FirstMeaningfulLine(result.Summary);
            return line.Length > 0 ? TrimTo(line, 120) : "chưa có LINE CHUẨN";
        }
        if (result.Stage == C360ElectronicStage.AcousticRubBuzz)
        {
            var pink = result.AdditionalResults.Select(r => r.PinkRubBuzz).FirstOrDefault(p => p != null);
            if (pink != null && (!pink.Metrics.IsValid || pink.Verdict is not ("PASS" or "FAIL")))
                return TrimTo($"Pink: {FirstMeaningfulLine(pink.Message)}", 120);
            var chirp = result.AdditionalResults.Select(r => r.ChirpRubBuzz).FirstOrDefault(c => c != null);
            if (chirp != null && (!chirp.Metrics.IsValid || chirp.Verdict is not ("PASS" or "FAIL")))
                return TrimTo($"Chirp: {FirstMeaningfulLine(chirp.Message)}", 120);
            var add = result.AdditionalResults.FirstOrDefault(r => !r.Passed);
            if (add != null && !string.IsNullOrWhiteSpace(add.Reason))
                return TrimTo(FirstMeaningfulLine(add.Reason), 120);
        }
        if (string.Equals(result.Status, "NO_SIGNAL", StringComparison.OrdinalIgnoreCase))
            return $"không có tín hiệu {config.PlaybackOut}";
        string summary = FirstMeaningfulLine(result.Summary);
        return summary.Length > 0 ? TrimTo(summary, 120) : result.Status;
    }

    public static string ShortUnconcludedReason(AutoTestCaseItem row)
    {
        if (string.Equals(row.Status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase))
        {
            string line = FirstMeaningfulLine(row.DistortionText);
            return line.Length > 0 ? TrimTo(line, 120) : "chưa có LINE CHUẨN";
        }
        if (row.Config?.SpeakerStage == "AcousticRubBuzz")
        {
            var (pink, chirp, add) = AudioRouting.RubBuzzOf(row);
            if (pink != null && (!pink.Metrics.IsValid || pink.Verdict is not ("PASS" or "FAIL")))
                return TrimTo($"Pink: {FirstMeaningfulLine(pink.Message)}", 120);
            if (chirp != null && (!chirp.Metrics.IsValid || chirp.Verdict is not ("PASS" or "FAIL")))
                return TrimTo($"Chirp: {FirstMeaningfulLine(chirp.Message)}", 120);
            if (add != null && !string.IsNullOrWhiteSpace(add.Reason))
                return TrimTo(FirstMeaningfulLine(add.Reason), 120);
        }
        if (string.Equals(row.Status, "NO_SIGNAL", StringComparison.OrdinalIgnoreCase))
            return $"không có tín hiệu {row.Config?.PlaybackOut ?? row.Name}";
        string first = FirstMeaningfulLine(row.ThdStatus);
        if (first.Length == 0 || first is "ĐANG CHỜ" or "ĐANG ĐO" or "CHƯA CÓ DỮ LIỆU DISTORTION")
            first = FirstMeaningfulLine(row.DistortionText);
        return first.Length > 0 ? TrimTo(first, 120) : row.Status;
    }

    public static string FormatOptionalStepDetails(AutoTestCaseItem row, string baseDetails)
    {
        string summary = !string.IsNullOrWhiteSpace(row.OptionalMeasurementSummary)
            ? row.OptionalMeasurementSummary
            : row.FeedbackGain != null
                ? FeedbackGainDiagnostics.OptionalMeasurementLabel(row.FeedbackGain, row.Config?.FeedbackGain)
                : FirstMeaningfulLine(row.DistortionText);
        string header = string.IsNullOrWhiteSpace(summary)
            ? ServerEngine.OptionalStepPrefix.TrimEnd()
            : ServerEngine.OptionalStepPrefix + summary;
        if (baseDetails.StartsWith(ServerEngine.OptionalStepPrefix, StringComparison.Ordinal))
            return baseDetails;
        return string.IsNullOrWhiteSpace(baseDetails) ? header : header + Environment.NewLine + baseDetails;
    }

    public static string FormatUnconcludedStepDetails(AutoTestCaseItem row, string baseDetails)
    {
        if (baseDetails.StartsWith(ServerEngine.InconclusiveStepPrefix, StringComparison.Ordinal))
            return baseDetails;
        string reason = ShortUnconcludedReason(row);
        string header = ServerEngine.InconclusiveStepPrefix + reason;
        return string.IsNullOrWhiteSpace(baseDetails) ? header : header + Environment.NewLine + baseDetails;
    }

    internal static string FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("TÍN HIỆU ĐO THẤP", StringComparison.OrdinalIgnoreCase)) continue;
            return line;
        }
        return "";
    }

    private static string TrimTo(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}

/// <summary>Owner 2026-11-19 (A3, COMMON #4): concise fault-only note sent to the server.
/// Only required FAIL tests (plus abnormal IO009 howl); empty when no faults; operator note appended with " · ".</summary>
public static class AutoTestFaultNotes
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly Regex ThdValueRegex = new(@"(?<hz>80|1000|4000)\s*Hz:\s*(?<val>\d+(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutlierCountRegex = new(@"Ngoài biên:\s*(?<count>\d+)\s*/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Build(IEnumerable<AutoTestCaseItem> cases, string? operatorNote = null)
    {
        var items = new List<string>();
        foreach (var row in cases)
        {
            if (row == null) continue;
            if (row.Config?.IsOptional == true)
            {
                if (row.Config.SpeakerStage == "MicFeedbackGain"
                    && row.FeedbackGain is { } loop
                    && FeedbackGainDiagnostics.IsAbnormalHowl(loop, row.Config.FeedbackGain ?? new()))
                {
                    items.Add($"Hú bất thường ({row.Id})");
                }
                continue;
            }

            if (C360VerdictPolicy.IsFailStatus(row.Status))
            {
                var faults = ExtractFaultLabels(row);
                if (faults.Count == 0) items.Add($"Không đạt ({row.Id})");
                else foreach (string fault in faults) items.Add($"{fault} ({row.Id})");
            }
            else if (string.Equals(row.Status, "NO_SIGNAL", StringComparison.OrdinalIgnoreCase))
            {
                string port = string.IsNullOrWhiteSpace(row.Config?.PlaybackOut) ? row.Name : row.Config.PlaybackOut;
                items.Add($"Mất tín hiệu {port} ({row.Id})");
            }
        }

        string autoNote = string.Join("; ", items);
        string? op = ProductMeasurementContext.NormalizeOperatorNote(operatorNote);
        string combined = autoNote.Length > 0 && op != null ? $"{autoNote} · {op}"
            : autoNote.Length > 0 ? autoNote
            : op ?? "";
        return combined.Length > 1000 ? combined[..1000] : combined;
    }

    /// <summary>Popup summary line for A1 #6: e.g. "IO001 (mất echo); IO008 chưa kết luận (Pink: độ nhạy 41,9 dB &lt; 45 dB)".</summary>
    public static string BuildPopupHeadlineDetails(IEnumerable<AutoTestCaseItem> cases)
    {
        var parts = new List<string>();
        foreach (var row in cases)
        {
            if (row == null || row.Config?.IsOptional == true) continue;
            if (C360VerdictPolicy.IsFailStatus(row.Status))
            {
                var faults = ExtractFaultLabels(row);
                string desc = faults.Count > 0 ? string.Join(", ", faults.Select(LowerFirst)) : "không đạt";
                parts.Add($"{row.Id} ({desc})");
            }
            else if (!C360VerdictPolicy.IsPassStatus(row.Status))
            {
                parts.Add($"{row.Id} chưa kết luận ({C360VerdictPolicy.ShortUnconcludedReason(row)})");
            }
        }
        return string.Join("; ", parts);
    }

    public static IReadOnlyList<string> ExtractFaultLabels(AutoTestCaseItem row)
    {
        var faults = new List<string>();
        var config = row.Config ?? new TestConfig();
        string stage = config.SpeakerStage ?? "";
        string combinedText = string.Join("\n", new[] { row.DistortionText, row.ThdStatus, row.FreqStatus, row.AdditionalStatus }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (row.SilentInputDetected)
        {
            string port = string.IsNullOrWhiteSpace(config.PlaybackOut) ? row.Name : config.PlaybackOut;
            faults.Add($"Mất tín hiệu {port}");
            return faults;
        }

        // 1. Echo / Reverb (IO001 or TestEffect)
        if (stage == "MicEffects" || config.TestEffect)
        {
            var fx = row.ImpulseResult?.ImpulseEffects;
            var fxSettings = config.ImpulseEffects ?? new ImpulseEffectSettings { ExpectEcho = true, ExpectReverb = true };
            if (fx != null)
            {
                if (fxSettings.ExpectEcho == true && !fx.EchoDetected) faults.Add("Mất echo");
                if (fxSettings.ExpectReverb == true && !fx.ReverbDetected) faults.Add("Mất reverb");
            }
            else
            {
                if (combinedText.Contains("Thiếu echo", StringComparison.OrdinalIgnoreCase)
                    || combinedText.Contains("Chưa phát hiện echo", StringComparison.OrdinalIgnoreCase)
                    || combinedText.Contains("ECHO_MISSING", StringComparison.OrdinalIgnoreCase))
                    faults.Add("Mất echo");
                if (combinedText.Contains("Thiếu reverb", StringComparison.OrdinalIgnoreCase)
                    || combinedText.Contains("Chưa phát hiện reverb", StringComparison.OrdinalIgnoreCase)
                    || combinedText.Contains("REVERB_MISSING", StringComparison.OrdinalIgnoreCase))
                    faults.Add("Mất reverb");
            }
            if (stage == "MicEffects") return faults;
        }

        // 2. Frequency shift (IO002)
        if (stage == "MicFrequencyShift")
        {
            faults.Add("Lỗi di tần FBX");
            return faults;
        }

        // 3. Feedback gain (non-optional IO009)
        if (stage == "MicFeedbackGain")
        {
            if (row.FeedbackGain is { } loop && FeedbackGainDiagnostics.IsAbnormalHowl(loop, config.FeedbackGain ?? new()))
                faults.Add("Hú bất thường");
            else
                faults.Add("FBX chống hú không đạt");
            return faults;
        }

        // 4. Acoustic Rub & Buzz (IO008 or legacy RubBuzz)
        if (stage == "AcousticRubBuzz" || config.RubBuzzTestFreq.HasValue
            || row.AdditionalResults.Any(r => C360VerdictPolicy.IsFailStatus(r.Status) && (r.PinkRubBuzz != null || r.ChirpRubBuzz != null || r.Name.Contains("Rè", StringComparison.OrdinalIgnoreCase))))
        {
            faults.Add("Rè/rung");
            if (stage == "AcousticRubBuzz" || config.RubBuzzTestFreq.HasValue) return faults;
        }

        // 5. THD (1 kHz and optional 80 Hz / 4000 Hz)
        bool addedThd = false;
        double[] thdFreqs = [80d, 1000d, 4000d];
        if (row.ImpulseResult is { SweepHarmonics.Count: > 0 } sweep)
        {
            foreach (double f in thdFreqs.Where(config.EvaluatesThdAt))
            {
                var pt = sweep.SweepHarmonics.Where(p => p.FundamentalFrequencyHz > 0)
                    .MinBy(p => Math.Abs(Math.Log(p.FundamentalFrequencyHz / f)));
                if (pt == null || Math.Abs(Math.Log(pt.FundamentalFrequencyHz / f, 2.0)) > 0.35) continue;
                double limit = config.ThdLimitAt(f);
                if (double.IsFinite(pt.ThdPercent) && pt.ThdPercent > limit)
                {
                    faults.Add(FormatHighThd(pt.ThdPercent, f));
                    addedThd = true;
                }
            }
        }
        else if (row.DistortionMeasurements.Count > 0)
        {
            foreach (double f in thdFreqs.Where(config.EvaluatesThdAt))
            {
                var kv = row.DistortionMeasurements.FirstOrDefault(p => Math.Abs(p.Key - f) < 20);
                if (kv.Value is { IsValid: true } q && double.IsFinite(q.ThdPercent) && q.ThdPercent > config.ThdLimitAt(f))
                {
                    faults.Add(FormatHighThd(q.ThdPercent, f));
                    addedThd = true;
                }
            }
        }
        if (!addedThd)
        {
            foreach (Match m in ThdValueRegex.Matches(combinedText))
            {
                if (!double.TryParse(m.Groups["hz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)) continue;
                if (!config.EvaluatesThdAt(hz)) continue;
                string rawVal = m.Groups["val"].Value.Replace(',', '.');
                if (!double.TryParse(rawVal, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)) continue;
                if (val > config.ThdLimitAt(hz))
                {
                    faults.Add(FormatHighThd(val, hz));
                    addedThd = true;
                }
            }
        }

        // 6. Frequency response deviation
        if (row.ResponseAssessment is { Passed: false, IsInvalid: false } assessment)
        {
            int n = assessment.Points.Count(p => p.Outside);
            faults.Add(n > 0 ? $"Lệch đáp tuyến ({n} điểm)" : "Lệch đáp tuyến");
        }
        else if (combinedText.Contains("lệch line chuẩn", StringComparison.OrdinalIgnoreCase)
            || combinedText.Contains("vượt biên", StringComparison.OrdinalIgnoreCase))
        {
            var match = OutlierCountRegex.Match(combinedText);
            if (match.Success && int.TryParse(match.Groups["count"].Value, out int n) && n > 0)
                faults.Add($"Lệch đáp tuyến ({n} điểm)");
            else
                faults.Add("Lệch đáp tuyến");
        }

        // 7. Other configured additional tests
        foreach (var add in row.AdditionalResults.Where(r => C360VerdictPolicy.IsFailStatus(r.Status)))
        {
            if (add.PinkRubBuzz != null || add.ChirpRubBuzz != null || add.Name.Contains("Rè", StringComparison.OrdinalIgnoreCase)) continue;
            if (add.Name.Contains("Rò khí", StringComparison.OrdinalIgnoreCase)) faults.Add("Rò khí");
            else if (add.Name.Contains("Hạt rác", StringComparison.OrdinalIgnoreCase)) faults.Add("Hạt rác");
            else if (add.Name.Contains("Mức âm", StringComparison.OrdinalIgnoreCase)) faults.Add("Lệch mức âm");
            else if (!string.IsNullOrWhiteSpace(add.Name)) faults.Add(add.Name);
        }

        return faults;
    }

    private static string FormatHighThd(double thdPercent, double frequencyHz) =>
        Math.Abs(frequencyHz - 1000) < 1
            ? $"THD cao {thdPercent.ToString("0.00", Vi)}%"
            : $"THD cao {thdPercent.ToString("0.00", Vi)}% tại {frequencyHz:0} Hz";

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLower(text[0], Vi) + text[1..];
}

/// <summary>Owner 2026-11-19 (A4): single-test retest volume suggestion, validation and audit text for INVALID/unconcluded steps.</summary>
public sealed record RetestVolumePlan(
    string TestId,
    string TestName,
    string Reason,
    double ConfigWindowsPlaybackPercent,
    double ConfigWindowsRecordingPercent,
    double? ConfigBleMasterPercent,
    double? ConfigBleMusicPercent,
    double? ConfigBleMicPercent,
    double PlaybackLevelDbfs,
    double SuggestedWindowsPlaybackPercent,
    double? SuggestedBleMusicPercent,
    double MaxBleMusicPercent,
    bool UsesBleMusic)
{
    public const double DefaultBoostPercent = 20;
    public const double DefaultBleMusicCeiling = 95;

    public static RetestVolumePlan Create(TestConfig test, InOutConfig modelConfig, string reason)
    {
        var effective = ModelTestVolume.Merge(modelConfig.TestVolume, test.TestVolume);
        double winPlay = effective?.WindowsPlaybackPercent ?? test.WindowsPlaybackPercent ?? 75;
        double winRec = effective?.WindowsRecordingPercent ?? test.WindowsRecordingPercent ?? 100;
        double? bleMaster = effective?.BleMasterPercent;
        double? bleMusic = effective?.BleMusicPercent;
        double? bleMic = effective?.BleMicPercent;
        double levelDbfs = test.PlaybackLevelDbfs ?? -24;
        var stage = C360ModelDefinition.Stage(test);
        bool usesBleMusic = stage is not (C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift
            or C360ElectronicStage.MicDry or C360ElectronicStage.MicFeedbackGain);

        double suggestedWin = Math.Clamp(Math.Round(winPlay + DefaultBoostPercent), 1, 100);
        double maxBle = bleMusic is double cur ? Math.Min(100, Math.Max(DefaultBleMusicCeiling, cur)) : DefaultBleMusicCeiling;
        double? suggestedBle = usesBleMusic && bleMusic is double m
            ? Math.Clamp(Math.Round(m + DefaultBoostPercent), 1, Math.Min(90, maxBle))
            : null;
        if (usesBleMusic && bleMusic is double currentMusic && suggestedBle < currentMusic)
            suggestedBle = Math.Min(maxBle, currentMusic);

        return new(test.id, test.name, reason, winPlay, winRec, bleMaster, bleMusic, bleMic, levelDbfs,
            suggestedWin, suggestedBle, maxBle, usesBleMusic);
    }

    public RetestVolumeOverride BuildOverride(double windowsPlaybackPercent, double? bleMusicPercent)
    {
        if (!double.IsFinite(windowsPlaybackPercent) || windowsPlaybackPercent < 1 || windowsPlaybackPercent > 100)
            throw new ArgumentException("Windows phát (%) phải từ 1 đến 100%.");
        if (bleMusicPercent is double ble && (!double.IsFinite(ble) || ble < 1 || ble > MaxBleMusicPercent))
            throw new ArgumentException($"BLE MUSIC (%) cho lần đo lại phải từ 1 đến {MaxBleMusicPercent:0}% để tránh clipping.");

        var changes = new List<string>();
        if (Math.Abs(windowsPlaybackPercent - ConfigWindowsPlaybackPercent) >= 0.5)
            changes.Add($"Windows phát {windowsPlaybackPercent:0.#} % (config {ConfigWindowsPlaybackPercent:0.#} %)");
        if (bleMusicPercent is double b && (ConfigBleMusicPercent == null || Math.Abs(b - ConfigBleMusicPercent.Value) >= 0.5))
            changes.Add(ConfigBleMusicPercent is double cfg
                ? $"BLE MUSIC {b:0.#} % (config {cfg:0.#} %)"
                : $"BLE MUSIC {b:0.#} %");

        string audit = changes.Count > 0
            ? $"đo lại {TestId} với {string.Join(", ", changes)}"
            : $"đo lại {TestId} với mức config (Windows phát {windowsPlaybackPercent:0.#} %{(bleMusicPercent is double bm ? $", BLE MUSIC {bm:0.#} %" : "")})";

        return new(windowsPlaybackPercent, bleMusicPercent, audit);
    }

    public string CurrentLevelsSummary()
    {
        static string Pct(double? v) => v is double x ? $"{x:0.#} %" : "giữ";
        return $"Windows phát {ConfigWindowsPlaybackPercent:0.#} % / thu {ConfigWindowsRecordingPercent:0.#} % · "
            + $"BLE MASTER {Pct(ConfigBleMasterPercent)} / MUSIC {Pct(ConfigBleMusicPercent)} · "
            + $"Playback Level {PlaybackLevelDbfs:0.#} dBFS";
    }
}

public sealed record RetestVolumeOverride(double WindowsPlaybackPercent, double? BleMusicPercent, string AuditNote)
{
    /// <summary>Applies this one-shot override onto a cloned configuration's target test so config/TestVolume cannot overwrite it.</summary>
    public void ApplyToClonedTest(InOutConfig clonedConfig, TestConfig clonedTarget)
    {
        var merged = ModelTestVolume.Merge(clonedConfig.TestVolume, clonedTarget.TestVolume) ?? new ModelTestVolume();
        clonedTarget.TestVolume = new ModelTestVolume
        {
            BleMasterPercent = merged.BleMasterPercent,
            BleMusicPercent = BleMusicPercent ?? merged.BleMusicPercent,
            BleMicPercent = merged.BleMicPercent,
            WindowsPlaybackPercent = WindowsPlaybackPercent,
            WindowsRecordingPercent = merged.WindowsRecordingPercent ?? clonedTarget.WindowsRecordingPercent
        };
        clonedTarget.WindowsPlaybackPercent = WindowsPlaybackPercent;
    }
}
