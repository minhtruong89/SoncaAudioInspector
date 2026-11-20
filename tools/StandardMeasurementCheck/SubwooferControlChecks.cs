using SoncaAudioInspector;

/// <summary>Subwoofer rear-panel test (Ctrl+Shift+T) on a simulated sub: HP 25 Hz + LR4 crossover, digital delay,
/// polarity switch, a 7 ms reflection, mic noise and per-lượt system latency jitter. Synthetic only; no hardware claim.</summary>
internal static class SubwooferControlChecks
{
    const int Rate = 48000;
    static int _checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        _checks++; Console.WriteLine("PASS " + message);
    }

    /// <summary>HighPassStages: 2nd-order high-pass sections at HighPassHz (the real bench sub behaves like ~4th order).</summary>
    sealed record Unit(double CrossoverHz, double DelayMs, bool Inverted, double Gain = 1, double JitterMs = 0.25, bool Silent = false,
        double HighPassHz = 25, int HighPassStages = 1);

    static double[] Biquad(double[] x, double f, bool highPass, double q = 0.7071)
    {
        double w = 2 * Math.PI * f / Rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        double b0 = (highPass ? 1 + cos : 1 - cos) / 2 / a0, b1 = (highPass ? -(1 + cos) : 1 - cos) / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        var y = new double[x.Length]; double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++) { y[i] = b0 * x[i] + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2; x2 = x1; x1 = x[i]; y2 = y1; y1 = y[i]; }
        return y;
    }

    static SubwooferCapture Capture(SubwooferSweepSettings settings, Unit unit, Random random)
    {
        var x = SubwooferSweep.Generate(settings, Rate);
        int tail = (int)(0.6 * Rate);
        var s = x.Select(v => (double)v).Concat(new double[tail]).ToArray();
        for (int k = 0; k < unit.HighPassStages; k++) s = Biquad(s, unit.HighPassHz, true); // enclosure/subsonic high-pass
        s = Biquad(Biquad(s, unit.CrossoverHz, false), unit.CrossoverHz, false); // LR4 crossover low-pass
        int latency = (int)Math.Round((0.1 + unit.JitterMs / 1000 * (random.NextDouble() * 2 - 1)) * Rate);
        int delay = (int)Math.Round(unit.DelayMs * Rate / 1000), reflection = (int)(0.007 * Rate);
        var y = new double[latency + s.Length];
        double sign = unit.Inverted ? -1 : 1;
        for (int i = 0; i < s.Length; i++)
        {
            if (unit.Silent) break;
            if (latency + delay + i < y.Length) y[latency + delay + i] += sign * unit.Gain * s[i];
            if (latency + delay + reflection + i < y.Length) y[latency + delay + reflection + i] += 0.25 * sign * unit.Gain * s[i];
        }
        var result = new float[y.Length];
        for (int i = 0; i < y.Length; i += 2)
        {
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble(), m = Math.Sqrt(-2 * Math.Log(u1)) * 1e-4;
            result[i] = (float)Math.Clamp(y[i] + m * Math.Cos(2 * Math.PI * u2), -1, 1);
            if (i + 1 < y.Length) result[i + 1] = (float)Math.Clamp(y[i + 1] + m * Math.Sin(2 * Math.PI * u2), -1, 1);
        }
        return new(result, Rate);
    }

    static SubwooferResponse Measure(SubwooferSweepSettings settings, Unit unit, int seed, string routeKey = "route-a")
    {
        var random = new Random(seed);
        return SubwooferControlAnalysis.Analyze(Enumerable.Range(0, settings.Repeats).Select(_ => Capture(settings, unit, random)).ToArray(), settings, routeKey, "sim");
    }

    /// <summary>led4Lit/led5Lit: the 180° LED state the operator reports after the first and the second press.</summary>
    static SubwooferControlSession Run(Unit baseline, Unit crossover, Unit delay, Unit phase, Unit back, bool? led4Lit = true, bool? led5Lit = false)
    {
        var session = new SubwooferControlSession();
        var s = session.Settings;
        Check(session.Record(Measure(s, baseline, 1)), "step 1 recorded");
        Check(session.Record(Measure(s, crossover, 2)), "step 2 recorded");
        Check(session.Record(Measure(s, delay, 3)), "step 3 recorded");
        Check(session.Record(Measure(s, phase, 4), led4Lit), "step 4 recorded");
        Check(session.Record(Measure(s, back, 5), led5Lit), "step 5 recorded");
        Check(session.IsComplete, "five steps complete the session");
        return session;
    }

    static SubwooferCheck Of(SubwooferControlSession session, string name) => session.Checks.Single(c => c.Control == name);

    public static void Run()
    {
        var settings = new SubwooferSweepSettings();
        float[] a = SubwooferSweep.Generate(settings, 44100), b = SubwooferSweep.Generate(settings, 48000);
        Check(a.Length == (int)Math.Round(settings.TotalSeconds * 44100) && b.Length == (int)Math.Round(settings.TotalSeconds * 48000)
            && Math.Abs(a.Max(v => Math.Abs(v)) - Math.Pow(10, -15 / 20.0)) < 0.01 && a.Take((int)(0.14 * 44100)).All(v => v == 0),
            "sweep has the requested length, peak and silent lead at 44.1 and 48 kHz");

        Unit good = new(180, 0, false);
        var session = Run(good, good with { CrossoverHz = 40 }, good with { DelayMs = 20 }, good with { Inverted = true }, good);
        var xo = Of(session, "Crossover"); var dl = Of(session, "Time aligning delay"); var ph = Of(session, "Phase 180°"); var rt = Of(session, "Trả về ban đầu");
        Console.WriteLine("  " + string.Join("\n  ", session.Checks.Select(c => $"{c.Control}: {c.Verdict} · {c.Measured} · {c.Message}")));
        Check(xo.Verdict == "PASS", "good unit: crossover knob moves the corner 180 → 40 Hz");
        double corner = SubwooferControlAnalysis.RelativeCornerHz(session.Response(SubwooferControlProcedure.Baseline)!,
            session.Response(SubwooferControlProcedure.CrossoverMin)!);
        Check(corner is > 33 and < 50, $"good unit: 40 Hz position cuts at {corner:0} Hz relative to the 180 Hz position");
        Check(dl.Verdict == "PASS" && Math.Abs(dl.Relation!.DelayMs - 20) < 0.6, $"good unit: delay knob measured {dl.Relation!.DelayMs:0.00} ms of 20 ms despite latency jitter");
        Check(ph.Verdict == "PASS" && Math.Abs(ph.Relation!.PhaseDeg) > 170, $"good unit: phase button inverts ({ph.Relation!.PhaseDeg:0}°)");
        Check(ph.Relation!.PhaseCurveDeg.Length > 50 && ph.Relation.PhaseCurveDeg.All(v => Math.Abs(v) > 165)
            && dl.Relation!.PhaseCurveDeg.All(v => Math.Abs(v) < 15),
            "phase plot data: inverted step is flat near ±180°, delay step flat near 0° after removing the delay");

        // Real bench sub (2026-11-11): strong subsonic high-pass makes the response a band-pass peaking 50–70 Hz.
        // The old "−6 dB below the peak" corner read 71 Hz at the 40 Hz position; the relative corner must not.
        Unit bandPass = good with { HighPassHz = 45, HighPassStages = 2 };
        var real = Run(bandPass, bandPass with { CrossoverHz = 40 }, bandPass with { DelayMs = 20 }, bandPass with { Inverted = true }, bandPass);
        double realCorner = SubwooferControlAnalysis.RelativeCornerHz(real.Response(SubwooferControlProcedure.Baseline)!, real.Response(SubwooferControlProcedure.CrossoverMin)!);
        Check(Of(real, "Crossover").Verdict == "PASS" && realCorner is > 33 and < 50,
            $"band-pass sub: crossover PASS, relative corner {realCorner:0} Hz (absolute corner {SubwooferControlAnalysis.UpperCornerHz(real.Response(SubwooferControlProcedure.CrossoverMin)!):0} Hz is not used)");
        Check(real.Verdict == "PASS", "band-pass sub: all four checks PASS");

        // The operator run of 2026-11-11: crossover left at 40 Hz in steps 3–4 and delay left at 20 ms in step 4.
        var misplaced = Run(bandPass, bandPass with { CrossoverHz = 40 }, bandPass with { CrossoverHz = 40, DelayMs = 20 },
            bandPass with { CrossoverHz = 40, DelayMs = 20, Inverted = true }, bandPass);
        var md = Of(misplaced, "Time aligning delay"); var mp = Of(misplaced, "Phase 180°");
        Console.WriteLine("  misplaced: " + md.Message + " | " + mp.Message);
        Check(md.Verdict == "INVALID" && md.Message.Contains("CROSSOVER"), "delay step measured with crossover still at 40 Hz is INVALID and names CROSSOVER");
        Check(mp.Verdict == "INVALID" && mp.Message.Contains("CROSSOVER") && mp.Message.Contains("DELAY"),
            "phase step measured with crossover 40 Hz and delay 20 ms is INVALID and names both knobs");
        Check(Of(misplaced, "Trả về ban đầu").Verdict == "PASS" && misplaced.Verdict == "INVALID", "misplaced run is INVALID overall, not FAIL");

        var actions = Enumerable.Range(0, 5).Select(i => SubwooferControlProcedure.Actions(i).Select(a => a.Control).ToArray()).ToArray();
        Check(actions[0].Length == 3 && actions[1].SequenceEqual([SubwooferControl.Crossover])
            && actions[2].SequenceEqual([SubwooferControl.Crossover, SubwooferControl.Delay])
            && actions[3].SequenceEqual([SubwooferControl.Delay, SubwooferControl.Phase]) && actions[4].SequenceEqual([SubwooferControl.Phase]),
            "each step highlights every control that differs from the previous step");
        Check(rt.Verdict == "PASS" && session.Verdict == "PASS", "good unit: return matches step 1 and overall PASS");
        session.GoTo(2);
        Check(session.Record(Measure(session.Settings, good with { DelayMs = 20 }, 6)) && session.IsComplete && session.Verdict == "PASS",
            "re-measuring a middle step skips steps that already have a result");

        var broken = Run(good, good, good with { DelayMs = 0 }, good, good);
        Check(Of(broken, "Crossover").Verdict == "FAIL", "crossover knob without effect fails");
        Check(Of(broken, "Time aligning delay").Verdict == "FAIL" && Of(broken, "Time aligning delay").Message.Contains("không có tác dụng"), "delay knob without effect fails");
        Check(Of(broken, "Phase 180°").Verdict == "FAIL", "phase button without inversion fails");
        Check(broken.Verdict == "FAIL", "broken unit overall FAIL");

        var shortDelay = Run(good, good with { CrossoverHz = 40 }, good with { DelayMs = 10 }, good with { Inverted = true }, good);
        Check(Of(shortDelay, "Time aligning delay").Verdict == "FAIL", "delay knob reaching only 10 ms of 20 ms fails");

        var led = Run(good, good with { CrossoverHz = 40 }, good with { DelayMs = 20 }, good with { Inverted = true }, good with { Inverted = true }, led4Lit: false, led5Lit: false);
        Check(Of(led, "Phase 180°").Verdict == "FAIL" && Of(led, "Phase 180°").Message.Contains("đèn"), "inverted phase with an unlit 180° LED fails");
        Check(Of(led, "Trả về ban đầu").Verdict == "FAIL", "second press that does not return to 0° fails");
        var stuckLed = Run(good, good with { CrossoverHz = 40 }, good with { DelayMs = 20 }, good with { Inverted = true }, good, led4Lit: true, led5Lit: true);
        Check(Of(stuckLed, "Trả về ban đầu").Verdict == "FAIL" && Of(stuckLed, "Trả về ban đầu").Message.Contains("vẫn sáng"),
            "phase back at 0° with the 180° LED still lit fails (LED state is reported, not a yes/no to a reversed question)");

        var jitter = Run(good, good with { CrossoverHz = 40 }, good with { DelayMs = 20, JitterMs = 6 }, good with { Inverted = true }, good);
        Check(Of(jitter, "Time aligning delay").Verdict == "INVALID", "latency jitter above the limit makes the delay result INVALID, not PASS/FAIL");

        var guarded = new SubwooferControlSession();
        var silent = Measure(guarded.Settings, good with { Silent = true }, 9);
        Check(!silent.IsValid && silent.Reason.Contains("Không thấy sweep") && !guarded.Record(silent) && guarded.CurrentIndex == 0,
            "no sound at the mic is rejected and the step is kept");
        var clipped = Measure(guarded.Settings, good with { Gain = 12 }, 10);
        Check(!clipped.IsValid && clipped.Reason.Contains("quá mức"), "clipping is rejected");
        Check(guarded.Record(Measure(guarded.Settings, good, 11)) && !guarded.Record(Measure(guarded.Settings, good with { CrossoverHz = 40 }, 12, "route-b"))
            && guarded.CurrentIndex == 1, "a later step on another route/level is rejected");
        guarded.GoTo(0); guarded.Record(Measure(guarded.Settings, good, 13));
        guarded.Record(Measure(guarded.Settings, good with { CrossoverHz = 40 }, 14)); guarded.Record(Measure(guarded.Settings, good with { DelayMs = 20 }, 15));
        bool threw = false;
        try { guarded.Record(Measure(guarded.Settings, good with { Inverted = true }, 16)); } catch (InvalidOperationException) { threw = true; }
        Check(threw && guarded.CurrentIndex == 3, "the 180° LED question must be answered before the phase step is recorded");
        guarded.NoLedAnswered = true;
        Check(guarded.Record(Measure(guarded.Settings, good with { Inverted = true }, 17)) && Of(guarded, "Phase 180°").Verdict == "PASS",
            "a unit without a 180° LED is judged on the acoustic phase only");
        guarded.GoTo(0); guarded.Record(Measure(guarded.Settings, good, 18));
        Check(guarded.CurrentIndex == 1 && guarded.Response(SubwooferControlProcedure.CrossoverMin) == null, "re-measuring step 1 discards later steps");
        Console.WriteLine($"ALL {_checks} SUBWOOFER CONTROL CHECKS PASSED");
    }
}
