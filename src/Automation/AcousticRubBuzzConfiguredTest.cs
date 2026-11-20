namespace SoncaAudioInspector;

public static class ChirpRubBuzzConfiguredTest
{
    public const string DisplayName = "Rè/rung · Chirp";
    public static void Validate(TestConfig config, AdditionalTestContext? context)
    {
        config.ChirpSettings.Validate(); config.ChirpLimits.Validate();
        if (context != null && (!PinkRubBuzzStimulus.IsSupportedRate(context.PlaybackSampleRate) || context.RecordingSampleRate < 32000))
            throw new ArgumentException("Chirp Rè cần ngõ phát 44,1/48 kHz và mic thu ≥ 32 kHz.");
    }
    public static IEnumerable<AdditionalTestStepStimulus> Steps(TestConfig config, AdditionalTestContext context)
    {
        var s = config.ChirpSettings; float[] samples = ChirpRubBuzzStimulus.Generate(s, context.PlaybackSampleRate);
        return Enumerable.Range(0, s.Repeats).Select(_ => new AdditionalTestStepStimulus(s.F1Hz, s.F2Hz, s.PeakDbFs, s.DurationSeconds, samples));
    }
    /// <summary>IO008 rule (owner 2026-11-19): no line standard; PASS when the rattle bump is below AbsoluteFailDb, else FAIL.</summary>
    public static ChirpRubBuzzRun JudgeAbsolute(ChirpRubBuzzRun run, ChirpRubBuzzLimits limits)
    {
        ChirpRubBuzzStandard.Compare(run, null, limits with { AbsolutePassDb = limits.AbsoluteFailDb, ReviewFraction = 0 });
        if (run.Verdict is "PASS" or "FAIL")
            run.Message = run.Metrics.HasMeasuredPeak
                ? $"Ngưỡng tuyệt đối (không dùng line chuẩn): PASS khi đỉnh rè < {limits.AbsoluteFailDb:0.#} dB · đo {run.Metrics.PeakBumpDb:0.0} dB tại {run.Metrics.PeakBumpHz:0} Hz."
                : run.Metrics.HasSensitiveBins ? "Không có đỉnh rè dương lặp lại." : run.Metrics.GraphStatus;
        return run;
    }
    public static ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context)
    {
        var settings = config.ChirpSettings;
        var metrics = ChirpRubBuzzAnalysis.Analyze(capture.StepCaptures, capture.BackgroundSamples, capture.SampleRate, settings,
            capture.Background?.AnalysisRmsLimitDbFs ?? -50);
        string key = ChirpRubBuzzStimulus.Key(settings, context.AcquisitionKey + ":" + config.id + ":" + capture.SampleRate);
        var run = new ChirpRubBuzzRun(settings, key, capture.ResultRoute, capture.SampleRate, metrics)
            { Captures = capture.StepCaptures.ToArray(), Background = capture.BackgroundSamples, PlaybackSampleRate = context.PlaybackSampleRate, CaptureMode = capture.CaptureMode };
        if (context.AbsoluteOnly) return new(DisplayName, JudgeAbsolute(run, config.ChirpLimits).Verdict, run.Summary) { ChirpRubBuzz = run };
        try
        {
            var standard = context.SaveAsStandard ? null : ChirpRubBuzzStandard.Load(context.ReferenceDirectory, key);
            if (context.SaveAsStandard || context.SaveIfMissing && standard == null)
            {
                if (string.IsNullOrEmpty(context.ReferenceDirectory)) throw new InvalidOperationException("Thiếu thư mục line chuẩn.");
                ChirpRubBuzzStandard.SaveAsStandard(run, config.ChirpLimits, context.ReferenceDirectory,
                    (context.SaveAsStandard ? "Tìm line chuẩn · " : "Auto Test tự lấy chuẩn · ") + capture.RouteLabel);
            }
            else ChirpRubBuzzStandard.Compare(run, standard, config.ChirpLimits);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { run.Verdict = "REVIEW"; run.Message = "Không đọc/ghi được line chuẩn chirp: " + ex.Message; }
        return new(DisplayName, run.Verdict, run.Summary) { ChirpRubBuzz = run };
    }
}

/// <summary>Acoustic rattle test: pink (repeatable rattle, v1.3) and/or chirp (rattle frequency, v1.4) in one acquisition:
/// one background, then pink × N, then chirp × M; results combined (FAIL &gt; INVALID &gt; REVIEW &gt; PASS).</summary>
public static class AcousticRubBuzzConfiguredTest
{
    /// <summary>Manual "Chẩn đoán theo mức": 6 dB steps up to the Pink maximum, so onset and a limiter knee are visible.</summary>
    public static readonly double[] DiagnosticLevelsDbFs = [-30, -24, -18, PinkRubBuzzSettings.MaxLevelRmsDbFs];

    /// <summary>Manual-only tagged plan. Existing Plan/Analyze and every station standard retain their exact path.</summary>
    public static AdditionalTestPlan PlanManual(TestConfig config, AdditionalTestContext context, RubBuzzSoundChoice choice,
        BroadbandPinkSettings? broadband = null)
    {
        if (choice == RubBuzzSoundChoice.BroadbandPink)
        {
            broadband ??= new();
            var samples = BroadbandPinkStimulus.Generate(broadband, context.PlaybackSampleRate);
            return new("RubBuzzBroadbandDiagnostic", PinkRubBuzzConfiguredTest.BackgroundSeconds,
                Enumerable.Range(0, broadband.Repeats).Select(_ => new AdditionalTestStepStimulus(
                    broadband.LowHz, broadband.HighHz, broadband.LevelRmsDbFs,
                    samples.Length / (double)context.PlaybackSampleRate, samples) { DiagnosticTag = "broadband" }).ToArray());
        }
        if (choice != RubBuzzSoundChoice.LevelDiagnostic) return Plan(config, context);
        Validate(config, context);
        var steps = new List<AdditionalTestStepStimulus>();
        foreach (double level in DiagnosticLevelsDbFs)
        {
            var settings = config.PinkSettings with { LevelRmsDbFs = level, Repeats = 3 };
            settings.Validate();
            var samples = PinkRubBuzzStimulus.Generate(settings, context.PlaybackSampleRate);
            steps.AddRange(Enumerable.Range(0, 3).Select(_ => new AdditionalTestStepStimulus(25, 150, level,
                settings.DurationSeconds, samples) { DiagnosticTag = "pink-level" }));
        }
        steps.AddRange(ChirpRubBuzzConfiguredTest.Steps(config, context).Select(s => s with { DiagnosticTag = "chirp" }));
        return new("RubBuzzLevelDiagnostic", PinkRubBuzzConfiguredTest.BackgroundSeconds, steps);
    }

    public static RubBuzzManualResult AnalyzeManual(TestConfig config, AdditionalTestCaptureData capture,
        AdditionalTestContext context, RubBuzzSoundChoice choice, AdditionalTestPlan plan,
        BroadbandPinkSettings? broadband = null, ChirpImpulsiveSettings? impulsive = null)
    {
        if (capture.StepCaptures.Count != plan.Steps.Count)
            throw new InvalidOperationException("Thiếu lượt chẩn đoán; không dùng dữ liệu dở dang.");
        PinkRubBuzzRun? pink = null; ChirpRubBuzzRun? chirp = null;
        BroadbandPinkResult? wide = null; RubBuzzLevelDiagnostics? levels = null;
        if (choice == RubBuzzSoundChoice.BroadbandPink)
        {
            wide = BroadbandPinkDistortion.Analyze(capture.StepCaptures.ToArray(), capture.BackgroundSamples,
                capture.SampleRate, plan.Steps[0].Samples, context.PlaybackSampleRate, broadband ?? new());
        }
        else if (choice == RubBuzzSoundChoice.LevelDiagnostic)
        {
            var runs = new List<PinkRubBuzzRun>();
            foreach (var group in plan.Steps.Select((s, i) => (Step: s, Index: i))
                .Where(s => s.Step.DiagnosticTag == "pink-level").GroupBy(s => s.Step.LevelDbFs))
            {
                var settings = config.PinkSettings with { LevelRmsDbFs = group.Key, Repeats = 3 };
                var captures = group.Select(s => capture.StepCaptures[s.Index]).ToArray();
                var metrics = PinkRubBuzzAnalysis.Analyze(captures, capture.BackgroundSamples, capture.SampleRate, settings,
                    capture.Background?.AnalysisRmsLimitDbFs ?? -50);
                runs.Add(new(settings, "manual-level-diagnostic", capture.ResultRoute, capture.SampleRate, metrics)
                { Captures = captures, Background = capture.BackgroundSamples, PlaybackSampleRate = context.PlaybackSampleRate, CaptureMode = capture.CaptureMode });
            }
            pink = runs.Last(); levels = RubBuzzLevelDiagnostics.Analyze(runs);
            var c = plan.Steps.Select((s, i) => (s, i)).Where(p => p.s.DiagnosticTag == "chirp").Select(p => capture.StepCaptures[p.i]).ToArray();
            // The existing analyzer may compare only a null reference here; it cannot access any station standard.
            chirp = ChirpRubBuzzConfiguredTest.Analyze(config, capture with { StepCaptures = c },
                context with { ReferenceDirectory = "", SaveAsStandard = false }).ChirpRubBuzz;
        }
        else
        {
            var result = Analyze(config, capture, context);
            pink = result.PinkRubBuzz; chirp = result.ChirpRubBuzz;
        }
        var a = chirp == null ? null : ChirpImpulsiveDistortion.Analyze(chirp, impulsive);
        var diagnosis = RubBuzzDiagnosis.Build(pink, chirp, a?.ToDiagnosisEvidence(), wide?.ToDiagnosisEvidence(), levels);
        return new(pink, chirp, choice) { Impulsive = a, Broadband = wide, Levels = levels, Diagnosis = diagnosis };
    }

    public static void Validate(TestConfig config, AdditionalTestContext? context)
    {
        if (config.UsesPinkRubBuzz) PinkRubBuzzConfiguredTest.Validate(config, context);
        if (config.UsesChirpRubBuzz) ChirpRubBuzzConfiguredTest.Validate(config, context);
    }
    public static int PinkCount(TestConfig config) => config.UsesPinkRubBuzz ? config.PinkSettings.Repeats : 0;
    public static int ChirpCount(TestConfig config) => config.UsesChirpRubBuzz ? config.ChirpSettings.Repeats : 0;
    public static AdditionalTestPlan Plan(TestConfig config, AdditionalTestContext context)
    {
        Validate(config, context);
        var steps = new List<AdditionalTestStepStimulus>();
        if (config.UsesPinkRubBuzz) steps.AddRange(PinkRubBuzzConfiguredTest.Plan(config, context).Steps);
        if (config.UsesChirpRubBuzz) steps.AddRange(ChirpRubBuzzConfiguredTest.Steps(config, context));
        return new("RubBuzz", PinkRubBuzzConfiguredTest.BackgroundSeconds, steps);
    }
    public static string Combine(params string[] statuses)
        => statuses.Contains("FAIL") ? "FAIL" : statuses.Contains("INVALID") ? "INVALID" : statuses.Contains("REVIEW") ? "REVIEW" : "PASS";
    public static ConfiguredTestResult Analyze(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context)
    {
        int pink = PinkCount(config), chirp = ChirpCount(config);
        if (capture.StepCaptures.Count != pink + chirp)
            return new("Rè/rung", "INVALID", $"Cần đủ {pink + chirp} lượt thu ({pink} pink + {chirp} chirp); không dùng dữ liệu dở dang.");
        // Line-standard acquisition with both signals saves both standards or neither: a half standard taken from an
        // acquisition where one signal was unusable would make later Pink/Chirp comparisons inconsistent.
        // Auto Test "tự lấy LINE CHUẨN" (v1.5.3): when neither standard exists yet, take both from this unit under the same rule;
        // when only one is missing, each analyser saves its own missing standard and compares the other one.
        bool bothMissing = !context.AbsoluteOnly && context.SaveIfMissing && !context.SaveAsStandard && pink > 0 && chirp > 0 && BothStandardsMissing(config, capture, context);
        bool saveBoth = !context.AbsoluteOnly && (context.SaveAsStandard || bothMissing) && pink > 0 && chirp > 0;
        var analysisContext = saveBoth ? context with { SaveAsStandard = false, SaveIfMissing = false } : context;
        ConfiguredTestResult? p = pink == 0 ? null : PinkRubBuzzConfiguredTest.Analyze(config, capture with { StepCaptures = capture.StepCaptures.Take(pink).ToArray() }, analysisContext);
        ConfiguredTestResult? c = chirp == 0 ? null : ChirpRubBuzzConfiguredTest.Analyze(config, capture with { StepCaptures = capture.StepCaptures.Skip(pink).ToArray() }, analysisContext);
        if (p == null) return c!;
        if (c == null) return p;
        if (saveBoth) (p, c) = SaveBoth(config, context, p.PinkRubBuzz!, c.ChirpRubBuzz!);
        return new("Rè/rung · Pink + Chirp", Combine(p.Status, c.Status), "Pink — " + p.Reason + Environment.NewLine + "Chirp — " + c.Reason)
            { PinkRubBuzz = p.PinkRubBuzz, ChirpRubBuzz = c.ChirpRubBuzz };
    }

    static bool BothStandardsMissing(TestConfig config, AdditionalTestCaptureData capture, AdditionalTestContext context)
    {
        if (string.IsNullOrEmpty(context.ReferenceDirectory)) return false;
        string suffix = context.AcquisitionKey + ":" + config.id + ":" + capture.SampleRate;
        try
        {
            return PinkRubBuzzStandard.Load(context.ReferenceDirectory, PinkRubBuzzStimulus.Key(config.PinkSettings, suffix)) == null
                && ChirpRubBuzzStandard.Load(context.ReferenceDirectory, ChirpRubBuzzStimulus.Key(config.ChirpSettings, suffix)) == null;
        }
        // An unreadable standard is not "missing": never overwrite it silently, let the comparison report it.
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException) { return false; }
    }

    static (ConfiguredTestResult Pink, ConfiguredTestResult Chirp) SaveBoth(TestConfig config, AdditionalTestContext context,
        PinkRubBuzzRun pink, ChirpRubBuzzRun chirp)
    {
        var reasons = PinkRubBuzzStandard.Validity(pink.Metrics, config.PinkLimits).Select(r => "Pink: " + r).ToList();
        if (!chirp.IsValid) reasons.Add("Chirp: " + chirp.Metrics.Reason);
        if (reasons.Count > 0)
        {
            pink.Verdict = chirp.Verdict = "INVALID";
            pink.Message = chirp.Message = "Chưa lưu line chuẩn rè (Pink và Chirp phải cùng hợp lệ mới lưu): " + string.Join(" ", reasons);
        }
        else
        {
            try
            {
                if (string.IsNullOrEmpty(context.ReferenceDirectory)) throw new InvalidOperationException("Thiếu thư mục line chuẩn.");
                PinkRubBuzzStandard.SaveAsStandard(pink, config.PinkLimits, context.ReferenceDirectory, "Tìm line chuẩn · " + pink.Route);
                ChirpRubBuzzStandard.SaveAsStandard(chirp, config.ChirpLimits, context.ReferenceDirectory, "Tìm line chuẩn · " + chirp.Route);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pink.Verdict = chirp.Verdict = "REVIEW";
                pink.Message = chirp.Message = "Không ghi được line chuẩn rè: " + ex.Message;
            }
        }
        return (new(PinkRubBuzzConfiguredTest.DisplayName, pink.Verdict, pink.Summary) { PinkRubBuzz = pink },
            new(ChirpRubBuzzConfiguredTest.DisplayName, chirp.Verdict, chirp.Summary) { ChirpRubBuzz = chirp });
    }
}
