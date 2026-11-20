using System.Text.Json;

namespace SoncaAudioInspector;

public enum RubBuzzSoundChoice { PinkAndChirp, Pink, Chirp, SlowChirp, Sweep512K, BroadbandPink, LevelDiagnostic }
public sealed record RubBuzzManualResult(PinkRubBuzzRun? Pink, ChirpRubBuzzRun? Chirp, RubBuzzSoundChoice Choice)
{
    public bool AllowsStandard => RubBuzzManualCapture.AllowsStandard(Choice);
    public bool AbsoluteOnly { get; init; }
    public ChirpImpulsiveResult? Impulsive { get; init; }
    public BroadbandPinkResult? Broadband { get; init; }
    public RubBuzzLevelDiagnostics? Levels { get; init; }
    public RubBuzzDiagnosisResult? Diagnosis { get; init; }
    public CapturePathEnhancementStatus? CapturePathEnhancements { get; init; }
}
public sealed record RubBuzzCapturedAudio(float[] Samples, int SampleRate);

/// <summary>The manual path uses exactly three repeats. Auto Test keeps its configured repeat counts.</summary>
public static class RubBuzzManualCapture
{
    public static readonly string[] Labels = ["Pink + Chirp (khuyến nghị)", "Pink 25–150 Hz · rè lặp lại",
        "Chirp 20→2000 Hz · 3 s · tần số rè", "Chirp chậm 20→2000 Hz · 10 s · khoanh tần số",
        "Sweep 20 Hz–20 kHz · 512K (giống REW)",
        "Pink dải rộng (dạng IEC/AES) · méo không kết hợp", "Chẩn đoán theo mức"];
    public static bool AllowsStandard(RubBuzzSoundChoice choice) => choice is >= RubBuzzSoundChoice.PinkAndChirp and <= RubBuzzSoundChoice.Chirp;
    public static string InvalidCaptureLevels(float[][] captures, float[] background)
    {
        static double Level(float[] samples) => 10 * Math.Log10(Math.Max(1e-20, samples.Average(v => (double)v * v)));
        if (background.Length == 0 || captures.Length == 0 || captures.Any(c => c.Length == 0)) return "Mức thu và SNR chưa xác định.";
        double low = captures.Min(Level), floor = Level(background);
        return double.IsFinite(low) && double.IsFinite(floor)
            ? $"RMS bản thu thấp nhất {low:0.0} dBFS · nền {floor:0.0} dBFS · SNR RMS ước tính {low - floor:0.0} dB."
            : "Mức thu và SNR chưa xác định do mẫu thu không hữu hạn.";
    }
    public static RubBuzzSoundChoice DefaultChoice(TestConfig? config) => config?.RubBuzzMode switch
        { "PinkNoise" => RubBuzzSoundChoice.Pink, "Chirp" => RubBuzzSoundChoice.Chirp, _ => RubBuzzSoundChoice.PinkAndChirp };
    public static TestConfig Configuration(TestConfig? source, RubBuzzSoundChoice choice)
    {
        if (!Enum.IsDefined(choice)) throw new ArgumentOutOfRangeException(nameof(choice));
        var config = source == null ? new TestConfig { id = "manual-rubbuzz" }
            : JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(source))!;
        config.TestRubBuzz = true;
        config.RubBuzzMode = choice is RubBuzzSoundChoice.PinkAndChirp or RubBuzzSoundChoice.LevelDiagnostic ? "PinkNoiseChirp" : choice == RubBuzzSoundChoice.Pink ? "PinkNoise" : "Chirp";
        config.RubBuzzPink = config.PinkSettings with { Repeats = 3 };
        config.RubBuzzChirp = config.ChirpSettings with { Repeats = 3 };
        if (choice == RubBuzzSoundChoice.SlowChirp) config.RubBuzzChirp = config.ChirpSettings with { DurationSeconds = 10 };
        if (choice == RubBuzzSoundChoice.Sweep512K) config.RubBuzzChirp = config.ChirpSettings with
            { F1Hz = 20, F2Hz = 20000, DurationSeconds = 524288 / 44100.0, PeakDbFs = -15 };
        return config;
    }
    public static AdditionalTestPlan Plan(TestConfig? source, RubBuzzSoundChoice choice, AdditionalTestContext context,
        BroadbandPinkSettings? broadband = null)
        => AcousticRubBuzzConfiguredTest.PlanManual(Configuration(source, choice), context, choice, broadband);

    /// <summary>
    /// Captures the Rè background and applies the station allowance. A refused floor goes to <paramref name="askRetry"/>
    /// (the operator may raise the allowance, which <paramref name="allowance"/> then returns); true captures a fresh
    /// background, false or no callback throws <see cref="RubBuzzBackgroundException"/>. Digital silence is never retried
    /// with a higher allowance; it throws so the caller can explain the blocked mic.
    /// </summary>
    public static async Task<(RubBuzzCapturedAudio Audio, BackgroundAllowance Allowance)> GateBackgroundAsync(double seconds,
        Func<double, CancellationToken, Task<RubBuzzCapturedAudio>> backgroundAsync, Func<BackgroundAllowance> allowance,
        Func<BackgroundCheck, CancellationToken, Task<bool>>? askRetry, Action validateContext, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested(); validateContext();
            var background = await backgroundAsync(seconds, token);
            token.ThrowIfCancellationRequested(); validateContext();
            var current = allowance();
            var check = SignalDiagnostics.CheckBackground(background.Samples, background.SampleRate,
                PinkRubBuzzConfiguredTest.BackgroundOnsetSeconds, current, rejectDigitalSilence: true);
            if (check.IsQuiet) return (background, current);
            if (check.IsDigitalSilence || askRetry == null || !await askRetry(check, token)) throw new RubBuzzBackgroundException(check);
        }
    }

    // Delegates are also the offline seam: exercise the actual orchestration without opening audio devices.
    public static async Task<RubBuzzManualResult> RunAsync(TestConfig? source, RubBuzzSoundChoice choice, AdditionalTestContext context,
        Func<double, CancellationToken, Task<RubBuzzCapturedAudio>> backgroundAsync,
        Func<AdditionalTestStepStimulus, CancellationToken, Task<RubBuzzCapturedAudio>> stepAsync,
        Action validateContext, Action<bool> exclusiveCapture, Action stop, CancellationToken token,
        Func<BackgroundAllowance>? allowance = null, Func<BackgroundCheck, CancellationToken, Task<bool>>? askRetry = null,
        BroadbandPinkSettings? broadband = null, ChirpImpulsiveSettings? impulsive = null, bool useExclusive = true)
    {
        var config = Configuration(source, choice);
        (impulsive ?? new ChirpImpulsiveSettings()).Validate();
        var plan = AcousticRubBuzzConfiguredTest.PlanManual(config, context, choice, broadband);
        try
        {
            token.ThrowIfCancellationRequested(); validateContext(); exclusiveCapture(useExclusive);
            var (background, accepted) = await GateBackgroundAsync(plan.BackgroundSeconds, backgroundAsync,
                allowance ?? (() => BackgroundAllowance.Default), askRetry, validateContext, token);
            AcousticRubBuzzConfiguredTest.Validate(config, context with { RecordingSampleRate = background.SampleRate });
            var captures = new List<float[]>();
            foreach (var step in plan.Steps)
            {
                token.ThrowIfCancellationRequested(); validateContext();
                var captured = await stepAsync(step, token);
                token.ThrowIfCancellationRequested(); validateContext();
                if (captured.SampleRate != background.SampleRate)
                    throw new InvalidOperationException("Rate thu đổi giữa các lượt; không dùng dữ liệu dở dang.");
                captures.Add(captured.Samples);
            }
            // Diagnostic variants may never read or write a standard, even when the caller supplied a directory.
            var analysisContext = context with { SaveAsStandard = false,
                ReferenceDirectory = AllowsStandard(choice) ? context.ReferenceDirectory : "" };
            var result = await Task.Run(() => AcousticRubBuzzConfiguredTest.AnalyzeManual(config,
                new(background.SampleRate, background.Samples, captures, context.RouteLabel)
                    { Background = accepted, CaptureMode = useExclusive ? "WASAPI Exclusive" : "WASAPI Shared" },
                analysisContext, choice, plan, broadband, impulsive), token);
            token.ThrowIfCancellationRequested();
            return result with { AbsoluteOnly = context.AbsoluteOnly };
        }
        finally { try { exclusiveCapture(false); } finally { stop(); } }
    }
}
