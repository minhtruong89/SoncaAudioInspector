using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

public partial class TestConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RubBuzzMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PinkRubBuzzSettings? RubBuzzPink { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public PinkRubBuzzLimits? RubBuzzPinkLimits { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ChirpRubBuzzSettings? RubBuzzChirp { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ChirpRubBuzzLimits? RubBuzzChirpLimits { get; set; }
    // Modes: SteppedSine (legacy), PinkNoise (v1.3), Chirp (v1.4), PinkNoiseChirp (v1.4, both — recommended).
    [JsonIgnore] public bool UsesPinkRubBuzz => RubBuzzMode is "PinkNoise" or "PinkNoiseChirp";
    [JsonIgnore] public bool UsesChirpRubBuzz => RubBuzzMode is "Chirp" or "PinkNoiseChirp";
    [JsonIgnore] public bool UsesAcousticRubBuzz => UsesPinkRubBuzz || UsesChirpRubBuzz;
    [JsonIgnore] public PinkRubBuzzSettings PinkSettings => RubBuzzPink ?? new();
    [JsonIgnore] public PinkRubBuzzLimits PinkLimits => RubBuzzPinkLimits ?? new();
    [JsonIgnore] public ChirpRubBuzzSettings ChirpSettings => RubBuzzChirp ?? new();
    [JsonIgnore] public ChirpRubBuzzLimits ChirpLimits => RubBuzzChirpLimits ?? new();
}

public static class PinkRubBuzzConfiguredTest
{
    public const string DisplayName = "Rè/rung · Pink noise";
    public const double BackgroundSeconds = 3.0;
    /// <summary>Stream-start span the Pink/Chirp analyses skip in the background; the pre-playback gate skips it too.</summary>
    public const double BackgroundOnsetSeconds = 0.3;
    public static void Validate(TestConfig config, AdditionalTestContext? context)
    {
        config.PinkSettings.Validate(); config.PinkLimits.Validate();
        if (context != null && (!PinkRubBuzzStimulus.IsSupportedRate(context.PlaybackSampleRate) || context.RecordingSampleRate < 32000
            || !double.IsFinite(context.PlaybackFrequencyScale) || context.PlaybackFrequencyScale <= 0))
            throw new ArgumentException("Pink Rè cần ngõ phát 44,1/48 kHz và mic thu ≥ 32 kHz (phân tích 4–12 kHz).");
    }
    public static AdditionalTestPlan Plan(TestConfig config, AdditionalTestContext context, int? repeats = null)
    {
        Validate(config, context);
        var settings = config.PinkSettings;
        float[] samples = PinkRubBuzzStimulus.Generate(settings, context.PlaybackSampleRate);
        // Identical waveform on every lượt: repeat-coherence separates mechanical rattle from room noise.
        return new("RubBuzz", BackgroundSeconds, Enumerable.Range(0, repeats ?? settings.Repeats).Select(_ => new AdditionalTestStepStimulus(
            0, 0, settings.LevelRmsDbFs, settings.DurationSeconds, samples)).ToArray());
    }
    /// <summary>IO008 rule (owner 2026-11-19): no line standard; PASS when "rè lặp lại" &lt; AbsoluteFailPct (30 %), else FAIL.
    /// Validity (sensitivity, foreign noise) still gives INVALID.</summary>
    public static PinkRubBuzzRun JudgeAbsolute(PinkRubBuzzRun run, PinkRubBuzzLimits limits)
    {
        PinkRubBuzzStandard.Compare(run, null, limits with { AbsolutePassPct = limits.AbsoluteFailPct, ReviewFraction = 0 });
        if (run.Verdict is "PASS" or "FAIL")
            run.Message = $"Ngưỡng tuyệt đối (không dùng line chuẩn): PASS khi rè lặp lại < {limits.AbsoluteFailPct:0.#}% · đo {PinkRubBuzzMetrics.DisplayCoherence(run.Metrics.RepeatCoherencePct)}.";
        return run;
    }
    public static ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context)
    {
        if (capture.StepCaptures.Count < 2) return new(DisplayName, "INVALID", "Thiếu lượt thu pink (cần ≥ 2 lượt cùng dạng sóng).");
        var settings = config.PinkSettings;
        var metrics = PinkRubBuzzAnalysis.Analyze(capture.StepCaptures, capture.BackgroundSamples, capture.SampleRate, settings,
            capture.Background?.AnalysisRmsLimitDbFs ?? -50);
        string key = PinkRubBuzzStimulus.Key(settings, context.AcquisitionKey + ":" + config.id + ":" + capture.SampleRate);
        var run = new PinkRubBuzzRun(settings, key, capture.ResultRoute, capture.SampleRate, metrics)
            { Captures = capture.StepCaptures.ToArray(), Background = capture.BackgroundSamples, PlaybackSampleRate = context.PlaybackSampleRate, CaptureMode = capture.CaptureMode };
        if (context.AbsoluteOnly) return new(DisplayName, JudgeAbsolute(run, config.PinkLimits).Verdict, run.Summary) { PinkRubBuzz = run };
        try
        {
            var standard = context.SaveAsStandard ? null : PinkRubBuzzStandard.Load(context.ReferenceDirectory, key);
            if (context.SaveAsStandard || context.SaveIfMissing && standard == null)
            {
                if (string.IsNullOrEmpty(context.ReferenceDirectory)) throw new InvalidOperationException("Thiếu thư mục line chuẩn.");
                PinkRubBuzzStandard.SaveAsStandard(run, config.PinkLimits, context.ReferenceDirectory,
                    (context.SaveAsStandard ? "Tìm line chuẩn · " : "Auto Test tự lấy chuẩn · ") + capture.RouteLabel);
            }
            else PinkRubBuzzStandard.Compare(run, standard, config.PinkLimits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { run.Verdict = "REVIEW"; run.Message = "Không đọc/ghi được line chuẩn rè: " + ex.Message; }
        return new(DisplayName, run.Verdict, run.Summary) { PinkRubBuzz = run };
    }
}
