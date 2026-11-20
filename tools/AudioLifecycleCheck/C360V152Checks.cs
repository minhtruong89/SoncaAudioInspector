using System.IO;
using System.Text.Json;
using SoncaAudioInspector;

/// <summary>v1.5.3 (C360 handoff 2026-11-18): IO008 Rè stage, A/B no-signal retry, automatic LINE CHUẨN, FBX note,
/// 2-run/1-pass default, analog port report and screen-fit scale. Offline only: no radio, audio or server.</summary>
internal static class C360V152Checks
{
    public static async Task Run(string fixture, string output)
    {
        Directory.CreateDirectory(output);
        int passed = 0;
        void Check(bool ok, string why) { if (!ok) throw new Exception("FAIL " + why); passed++; }

        // 1. Rev 3 config: IO008 is its own stage, the station validator and plan accept it.
        var model = JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(fixture))!;
        var io = model.testItems.InOut;
        C360ModelDefinition.NormalizeCaptureAlias(io);
        C360ModelDefinition.Validate(io);
        var endpoints = io.Devices.Input.Values.Distinct().Select((name, i) => new C360ModelEndpoint("out" + i, name, false))
            .Concat(io.Devices.Output.Values.Distinct().Select((name, i) => new C360ModelEndpoint("in" + i, name, true))).ToArray();
        var plan = C360ModelPlan.Create(io, endpoints, new("a", "u", "c"));
        Check(plan.Stages.Last() == C360ElectronicStage.AcousticRubBuzz && plan.Stages.Count(s => s == C360ElectronicStage.AcousticMic) == 1, "rev3 plan has IO006 + separate IO008");
        var rubBuzz = io.Tests.Single(t => t.id == "IO008");
        Check(C360ModelDefinition.Stage(rubBuzz) == C360ElectronicStage.AcousticRubBuzz && rubBuzz.IsRubBuzzEnabled && rubBuzz.UsesPinkRubBuzz, "IO008 runs pink Rè");
        Check(!io.Tests.Single(t => t.id == "IO006").IsRubBuzzEnabled, "IO006 no longer runs pink Rè");
        var mic = io.Devices.Output[rubBuzz.RecordingIn];
        Check(plan.CaptureIds[C360ElectronicStage.AcousticRubBuzz] == endpoints.Single(e => e.Capture && e.Name == mic).Id, "IO008 records from the measurement mic");
        Check(plan.PlaybackIds[C360ElectronicStage.AcousticRubBuzz] == plan.PlaybackIds[C360ElectronicStage.AcousticMic], "IO008 plays through LINE IN like IO006");
        Check(io.Tests.All(t => t.MaximumRuns == 2 && t.RequiredPasses == 1), "rev3: 2 runs, 1 pass");
        Check(io.TestVolume is { BleMasterPercent: 50, BleMusicPercent: 50, BleMicPercent: 50 } && plan.Configuration.Tests.Where(t => t.IsEnabled)
            .All(t => t.VolumeMapping == "BLE MASTER 50% · MUSIC 50% · MIC 50% · Windows phát 75% · thu 100%"), "rev3: BLE 50% and Windows mapping on every test");
        var own = io.Tests.Single(t => t.id == "IO004");
        own.TestVolume = new ModelTestVolume { WindowsPlaybackPercent = 60, BleMusicPercent = 40 };
        var ownPlan = C360ModelPlan.Create(io, endpoints, new("a", "u", "c"));
        Check(ownPlan.Configuration.Tests.Single(t => t.id == "IO004").VolumeMapping == "BLE MASTER 50% · MUSIC 40% · MIC 50% · Windows phát 60% · thu 100%"
            && ownPlan.Configuration.Tests.Single(t => t.id == "IO005").VolumeMapping.Contains("MUSIC 50%"), "per-test volume overrides only that test");
        own.TestVolume = null;
        Check(io.Devices.Input.ContainsKey("BLUETOOTH") && io.Tests.Single(t => t.SpeakerStage == "Bluetooth").PlaybackOut == "BLUETOOTH", "owner port name BLUETOOTH");
        rubBuzz.TestRubBuzz = false;
        try { C360ModelDefinition.Validate(io); throw new Exception("FAIL IO008 without Rè accepted"); }
        catch (ArgumentException ex) { Check(ex.Message.Contains("Rè"), "IO008 without Rè is refused"); }
        rubBuzz.TestRubBuzz = true;

        // 2. IO008 never needs a response LINE CHUẨN; IO006 still does (and reports MissingReference).
        var bench = new SignalBench();
        string empty = Path.Combine(output, "refs-" + Guid.NewGuid().ToString("N"));
        var rb = await new C360ReferenceBench(bench, plan, "C360", empty, "mock", false, 3).CaptureAsync(C360ElectronicStage.AcousticRubBuzz, plan.SetupFor(rubBuzz), default);
        Check(rb.Status == "PASS" && !rb.MissingReference && !rb.Summary.Contains("LINE CHUẨN"), "IO008 sweep pre-check is not judged against a response reference");
        var acoustic = io.Tests.Single(t => t.id == "IO006");
        var noRef = await new C360ReferenceBench(bench, plan, "C360", empty, "mock", false, 3).CaptureAsync(C360ElectronicStage.AcousticMic, plan.SetupFor(acoustic), default);
        Check(noRef.Status == "INVALID" && noRef.MissingReference, "IO006 without reference is INVALID + MissingReference");
        var known = C360ReferenceBench.MissingReferences(plan, "C360", empty, "mock").Select(t => t.id).ToArray();
        Check(known.SequenceEqual(new[] { "IO003", "IO004", "IO005", "IO006" }),
            "missing references known before the run: MIC dry, LINE IN, USB A (endpoint present), acoustic; BT resolved inside its stage: " + string.Join(",", known));

        // 3. Automatic LINE CHUẨN: declined = old behaviour; accepted = saved from this unit, then judged against it.
        int asked = 0;
        var declined = await new C360ReferenceBench(bench, plan, "C360", empty, "mock", false, 3, autoReference: (_, _, _) => { asked++; return Task.FromResult(false); })
            .CaptureAsync(C360ElectronicStage.LineIn, plan.SetupFor(io.Tests.Single(t => t.SpeakerStage == "LineIn")), default);
        Check(asked == 1 && declined.MissingReference && declined.Status == "INVALID", "declined automatic reference keeps INVALID");
        var accepted = await new C360ReferenceBench(bench, plan, "C360", empty, "mock", false, 3, autoReference: (_, _, _) => Task.FromResult(true))
            .CaptureAsync(C360ElectronicStage.LineIn, plan.SetupFor(io.Tests.Single(t => t.SpeakerStage == "LineIn")), default);
        Check(accepted.Status == "PASS" && accepted.Reference != null && accepted.Summary.StartsWith("ĐÃ TỰ LẤY LINE CHUẨN"), "accepted automatic reference saves and judges");
        Check(C360ReferenceBench.MissingReferences(plan, "C360", empty, "mock").All(t => t.SpeakerStage != "LineIn"), "saved reference is found next time");

        // 4. A/B: no signal on an analog stage asks, retries the same stage, and notes the recovery; BT/USB never ask.
        Check(C360SignalCheck.IsMissingAnalogSignal(C360ElectronicStage.LineIn, new(C360ElectronicStage.LineIn, "NO_SIGNAL", "x")), "NO_SIGNAL on LINE IN asks A/B");
        Check(C360SignalCheck.IsMissingAnalogSignal(C360ElectronicStage.LineIn, bench.Result(C360ElectronicStage.LineIn, "INVALID", snr: .3)), "INVALID at SNR 0.3 dB asks A/B (IO004 run 1)");
        Check(!C360SignalCheck.IsMissingAnalogSignal(C360ElectronicStage.LineIn, bench.Result(C360ElectronicStage.LineIn, "FAIL", snr: 46)), "real FAIL with signal does not ask");
        Check(!C360SignalCheck.IsMissingAnalogSignal(C360ElectronicStage.Bluetooth, new(C360ElectronicStage.Bluetooth, "NO_SIGNAL", "x")), "BT never asks A/B");
        Check(!C360SignalCheck.IsMissingAnalogSignal(C360ElectronicStage.UsbA, new(C360ElectronicStage.UsbA, "NO_SIGNAL", "x")), "USB A never asks A/B");
        var flaky = new SignalBench { Queue = new(new[] { "silent", "ok" }) };
        int prompts = 0;
        var retried = await new C360NoSignalRetryBench(flaky, (_, _, _, _) => { prompts++; return Task.FromResult(true); }).CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        Check(prompts == 1 && retried.Status == "PASS" && retried.Summary.StartsWith("Có tín hiệu sau khi kiểm tra cáp"), "cable-check retry recovers the same stage");
        var skipped = await new C360NoSignalRetryBench(new SignalBench { Queue = new(new[] { "silent", "ok" }) }, (_, _, _, _) => Task.FromResult(false))
            .CaptureAsync(C360ElectronicStage.LineIn, plan.Setup, default);
        Check(skipped.Status == "INVALID", "Bỏ qua keeps the no-signal result");

        // 5. FBX note when the config sets no expectation (handoff: −3.000 Hz on and off was a silent PASS).
        var shift = new FrequencyShiftResult(true, true, 0, .15, 0, 0, "x", [], []) { OnShiftHz = -3, OffShiftHz = -3, FbxChangeDetected = false };
        string? note = FrequencyShiftDiagnostics.ExpectationNote(shift, new FrequencyShiftSettings());
        Check(note != null && note.Contains("CHƯA kiểm FBX") && note.Contains("KHÔNG làm đổi"), "FBX without expectation carries a note");
        Check(FrequencyShiftDiagnostics.Status(shift, new FrequencyShiftSettings()) == "PASS", "FBX verdict logic unchanged");

        // 6. Default run policy 2/1: one PASS ends the test; FAIL gets a second run.
        var defaults = new TestConfig();
        Check(defaults.MaximumRuns == 2 && defaults.RequiredPasses == 1, "default 2 runs / 1 pass");
        Check(!TestRunVote.Count(["PASS"], 2, 1).NeedsAnotherRun && TestRunVote.Count(["PASS"], 2, 1).Status == "PASS", "PASS on run 1 skips run 2");
        Check(TestRunVote.Count(["FAIL"], 2, 1).NeedsAnotherRun && TestRunVote.Count(["FAIL", "PASS"], 2, 1).Status == "PASS"
            && TestRunVote.Count(["FAIL", "FAIL"], 2, 1).Status == "FAIL", "FAIL gets a second run");

        // 7. Silent sound card report and screen fit.
        string report = AudioRouting.DescribeAnalogPorts(io, endpoints);
        Check(report.Contains("MIC IN = Analog 3/4 ✓") && report.Contains("LINE IN = Analog 1/2 ✓") && !report.Contains("BLUETOOTH") && !report.Contains("USB A"), "analog port report: " + report);
        string missing = AudioRouting.DescribeAnalogPorts(io, endpoints.Where(e => !e.Name.Contains("3/4")).ToArray());
        Check(missing.Contains("MIC IN = Analog 3/4 ✗"), "missing analog pair reported");
        Check(MainWindow.FitScale(1920, 1040) == 1 && MainWindow.FitScale(1200, 720) == 1 && Math.Abs(MainWindow.FitScale(1024, 600) - .833) < .002
            && MainWindow.FitScale(400, 300) == .55, "screen-fit scale");

        // 8. IO008 QA image: one chirp graph (owner 2026-11-18).
        Check(rubBuzz.UsesChirpRubBuzz && rubBuzz.UsesPinkRubBuzz, "IO008 runs Pink + Chirp, so a chirp image exists");
        double[] freqs = [30, 60, 120, 240, 480, 960, 1920];
        var chirp = new ChirpRubBuzzRun(new ChirpRubBuzzSettings(), "key", "route", 48000, new ChirpRubBuzzMetrics(true, "")
            { FrequenciesHz = freqs, BumpDb = [1, 2, 9, 3, 1, 0, 0], RepeatBumpDb = [[1, 2, 8, 3, 1, 0, 0], [1, 2, 10, 3, 1, 0, 0]] }) { Verdict = "REVIEW" };
        byte[] png = RubBuzzQaGraph.Chirp(chirp, "IO008 · Rè").GetImage(800, 450).GetImageBytes();
        File.WriteAllBytes(Path.Combine(output, "IO008-chirp.png"), png);
        Check(png.Length > 5000 && png[0] == 0x89 && png[1] == (byte)'P', "IO008 chirp QA image renders headless");

        // 9. IO009 gain before feedback (v1.5.4): rev 4 fixture, howl detector on a simulated loop, verdict text.
        var rev4 = JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture)!, "C360-rev4.json")))!.testItems.InOut;
        C360ModelDefinition.NormalizeCaptureAlias(rev4); C360ModelDefinition.Validate(rev4);
        var plan4 = C360ModelPlan.Create(rev4, endpoints, new("a", "u", "c"));
        var io9 = rev4.Tests.Single(t => t.id == "IO009");
        Check(plan4.Stages.Last() == C360ElectronicStage.MicFeedbackGain && plan4.CaptureIds[C360ElectronicStage.MicFeedbackGain] == plan4.CaptureIds[C360ElectronicStage.AcousticMic]
            && plan4.PlaybackIds[C360ElectronicStage.MicFeedbackGain] == plan4.PlaybackIds[C360ElectronicStage.MicDry], "IO009 plays MIC IN and records the measurement mic");
        var fb = io9.FeedbackGain!;
        double? HowlAt(double truth)
        {
            var detector = new HowlDetector(48000, fb);
            for (double g = fb.StartGainDb; g <= fb.MaximumGainDb; g += fb.StepDb)
                for (int b = 0; b < 3; b++) if (detector.Push(FeedbackGainDiagnostics.SimulatedBlock(48000, g, truth, 2150, (int)(g * 10) + b), g)) return g;
            return null;
        }
        Check(HowlAt(-4) == -4 && HowlAt(5) == 5 && HowlAt(99) == null, "howl detector stops at the step where the loop howls, never on noise");
        var loopResult = new FeedbackGainResult(new(false, -4, 2150, -10, []), new(true, 5, 2300, -10, []), fb.MaximumGainDb);
        Check(loopResult.AddedDb == 9 && FeedbackGainDiagnostics.Status(loopResult, fb) == "PASS" && FeedbackGainDiagnostics.Summary(loopResult, fb).Contains("chưa đặt ngưỡng"), "IO009 report without threshold");
        Check(FeedbackGainDiagnostics.Status(loopResult, fb with { MinimumAddedDb = 12 }) == "FAIL", "IO009 threshold FAIL");
        Check(FeedbackGainDiagnostics.Status(new(new(false, null, 0, -60, []), new(true, null, 0, -60, []), 24), fb) == "INVALID", "no howl with FBX off = loop too weak, INVALID");
        Check(new FeedbackGainResult(new(false, 2, 0, 0, []), new(true, null, 0, 0, []), 24) is { AddedDb: 22, AddedIsLowerBound: true }, "FBX never howling gives a lower bound");

        // 10. Rev 5 (owner 2026-11-19): IO002 must have a frequency shift, IO009 needs ≥ 6 dB, BLE name prefix KBEATBOX.
        string rev5Json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixture)!, "C360-rev5.json"));
        var rev5 = JsonSerializer.Deserialize<ModelConfig>(rev5Json)!.testItems.InOut;
        C360ModelDefinition.NormalizeCaptureAlias(rev5); C360ModelDefinition.Validate(rev5);
        Check(rev5.Tests.Count(t => t.IsEnabled) == 9 && rev5.EffectiveBleNamePrefix == "KBEATBOX"
            && ModelConfigEditorWindow.ParseModel("C360", rev5Json).testItems.InOut.BleNamePrefix == "KBEATBOX", "rev 5 keeps 9 tests and the BLE prefix");
        var shift5 = rev5.Tests.Single(t => t.id == "IO002").FrequencyShift!;
        FrequencyShiftResult Shift(double on, double off) => new(true, Math.Abs(on) - .15 >= shift5.DetectionThresholdHz ? true : Math.Abs(on) + .15 < shift5.DetectionThresholdHz ? false : null,
            on - off, .15, 0, 0, "", [], []) { OnShiftHz = on, OffShiftHz = off, FbxChangeDetected = Math.Abs(on - off) - .15 >= shift5.DetectionThresholdHz };
        Check(FrequencyShiftDiagnostics.Status(Shift(-3, -3), shift5) == "PASS" && FrequencyShiftDiagnostics.Status(Shift(0, 0), shift5) == "FAIL",
            "IO002 rev 5: −3 Hz with FBX on and off (station 18/11) PASS, no shift FAIL");
        var fb5 = rev5.Tests.Single(t => t.id == "IO009").FeedbackGain!;
        Check(fb5.MinimumAddedDb == 6 && FeedbackGainDiagnostics.Status(loopResult, fb5) == "PASS"
            && FeedbackGainDiagnostics.Status(new(new(false, -4, 2150, -10, []), new(true, 0, 2150, -10, []), 24), fb5) == "FAIL", "IO009 rev 5: +9 dB PASS, +4 dB FAIL");
        var now = DateTime.Now;
        BleDeviceInfo Ad(ulong address, string name, short rssi, double age = 1) => new(address, name, rssi, true, now.AddSeconds(-age));
        var ranked = SoncaBleModelSelection.RankedByNamePrefix([
            Ad(1, "KBEATBOX", -70), Ad(2, "kbeatbox CHILL", -50), Ad(3, "MI SAM", -30), Ad(4, "KBEATBOX", -40, 20),
            Ad(5, "KBEATBOX ", -60), Ad(5, "", -55, .5), Ad(6, "KBEATBOX", 0)], "KBEATBOX", 1075,
            new AudioBenchPreferences { KnownDeviceModels = new() { [2] = 1076 } }, now);
        Check(ranked.Select(d => d.Address).SequenceEqual([5UL, 1UL]) && ranked[0].Name.Trim() == "KBEATBOX" && ranked[0].Rssi == -55,
            "BLE prefix: strongest fresh match first, other known model / stale / invalid RSSI / other names skipped, unnamed packet keeps the name");
        Check(SoncaBleModelSelection.RankedByNamePrefix([Ad(2, "KBEATBOX CHILL", -50)], "kbeatbox", null, new(), now).Length == 1
            && !SoncaBleModelSelection.MatchesNamePrefix("MY KBEATBOX", "KBEATBOX"), "BLE prefix is case-insensitive and anchored at the start");

        // 11. IO008 Rè without line standard (owner 2026-11-19): Pink PASS < 30 %, Chirp PASS below the FAIL threshold, never REVIEW.
        PinkRubBuzzRun Pink(double rc) => new(new(), new string('a', 64), "route", 48000, new PinkRubBuzzMetrics(true, "")
            { RepeatCoherencePct = rc, KeptPct = 100, SensitivityDb = 60 });
        var pinkLimits = new PinkRubBuzzLimits();
        Check(PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(20), pinkLimits).Verdict == "PASS" && PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(29.9), pinkLimits).Verdict == "PASS"
            && PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(30), pinkLimits).Verdict == "FAIL" && PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(5), pinkLimits with { ReviewFraction = .5 }).Verdict == "PASS"
            && PinkRubBuzzStandard.Compare(Pink(20), null, pinkLimits).Verdict == "REVIEW", "Pink absolute: 20 % and 29.9 % PASS, 30 % FAIL (the old 15–30 % REVIEW band applies only outside IO008)");
        Check(PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(10) with { Metrics = Pink(10).Metrics with { SensitivityDb = 20 } }, pinkLimits).Verdict == "INVALID", "Pink absolute keeps the validity gate");
        ChirpRubBuzzRun Chirp(double bump) => new(new(), "key", "route", 48000, new ChirpRubBuzzMetrics(true, "") { PeakBumpDb = bump, PeakBumpHz = 120 });
        var chirpLimits = new ChirpRubBuzzLimits();
        Check(ChirpRubBuzzConfiguredTest.JudgeAbsolute(Chirp(8), chirpLimits).Verdict == "PASS" && ChirpRubBuzzConfiguredTest.JudgeAbsolute(Chirp(chirpLimits.AbsoluteFailDb), chirpLimits).Verdict == "FAIL",
            "Chirp absolute: 8 dB PASS (was REVIEW), at the FAIL threshold FAIL");

        // 12. IO009 weak loop → "đặt mic gần hơn" popup and a second capture; howl strength grades.
        var weak = new C360ElectronicResult(C360ElectronicStage.MicFeedbackGain, "INVALID", "weak") { FeedbackGain = new(new(false, null, 0, -60, []), new(true, null, 0, -60, []), 24) };
        var strong = new C360ElectronicResult(C360ElectronicStage.MicFeedbackGain, "PASS", "ok") { FeedbackGain = loopResult };
        Check(C360SignalCheck.IsWeakFeedbackLoop(C360ElectronicStage.MicFeedbackGain, weak) && !C360SignalCheck.IsWeakFeedbackLoop(C360ElectronicStage.MicFeedbackGain, strong)
            && !C360SignalCheck.IsWeakFeedbackLoop(C360ElectronicStage.MicDry, weak), "IO009 weak loop recognised only for IO009 without a howl");
        int closer = 0; var loopQueue = new Queue<C360ElectronicResult>([weak, strong]);
        var moved = await new C360NoSignalRetryBench(new QueueBench(loopQueue), (stage, r, _, _) => { closer += C360SignalCheck.IsWeakFeedbackLoop(stage, r) ? 1 : 100; return Task.FromResult(true); })
            .CaptureAsync(C360ElectronicStage.MicFeedbackGain, plan4.Setup, default);
        Check(closer == 1 && moved.Status == "PASS" && moved.Summary.Contains("gần loa hơn"), "IO009: one 'move the mic closer' prompt, then the stage is measured again");
        FeedbackGainStep S(double g, double ratio, double level) => new(g, level, ratio, 2150);
        var graded = new FeedbackGainResult(new(false, -4, 2150, -8, [S(-5, 12, -40), S(-4, 30, -20), S(-4, 52, -9)]), new(true, null, 0, -30, [S(-4, 14, -38), S(5, 20, -30)]), 24);
        var offStrength = FeedbackGainDiagnostics.Strength(graded.Off, fb5);
        Check(offStrength is { Grade: "hú mạnh", PeakToMedianDb: 52, LevelDbFs: -9 } && FeedbackGainDiagnostics.Grade(30, fb5) == "hú nhẹ" && FeedbackGainDiagnostics.Grade(40, fb5) == "hú vừa"
            && FeedbackGainDiagnostics.SuppressionAtOffHowlDb(graded, fb5) == 38 && FeedbackGainDiagnostics.HighestPeakToMedianDb(graded.On) == 20
            && FeedbackGainDiagnostics.Summary(graded, fb5).Contains("hú mạnh") && FeedbackGainDiagnostics.Strength(graded.On, fb5) == null, "howl strength: grade, level, FBX suppression at the same gain");

        // 13. IO009 optional (owner 2026-11-19): skipped or FAIL never changes the overall verdict nor blocks the upload.
        Check(io9.IsOptional && !rev5.Tests.Single(t => t.id == "IO002").IsOptional && !(new TestConfig { SpeakerStage = "MicFeedbackGain", Optional = false }).IsOptional,
            "IO009 is optional by default; other tests are not; config can turn it off");
        ServerEngine.AudioQaStepResult Step(string id, string status, bool optional = false) => new(id, status, "d", optional);
        var mapped = ServerEngine.ValidateAndMapQaSteps([Step("IO001", "PASS"), Step("IO009", "INVALID", true)], true);
        Check(mapped[1] is { status: "PENDING", optional: true } && mapped[1].details.StartsWith(ServerEngine.OptionalStepNote) && mapped[0].status == "PASS",
            "skipped IO009 is uploaded as PENDING with the optional note, suite PASS");
        Check(ServerEngine.ValidateAndMapQaSteps([Step("IO001", "PASS"), Step("IO009", "FAIL", true)], true)[1].status == "FAIL"
            && ServerEngine.ValidateAndMapQaSteps([Step("IO001", "FAIL"), Step("IO009", "PASS", true)], false).Count == 2, "optional FAIL keeps suite PASS; a required FAIL still fails");
        bool Throws(Action a) { try { a(); return false; } catch (InvalidOperationException) { return true; } }
        Check(Throws(() => ServerEngine.ValidateAndMapQaSteps([Step("IO001", "INVALID"), Step("IO009", "PASS", true)], true))
            && Throws(() => ServerEngine.ValidateAndMapQaSteps([Step("IO009", "PASS", true)], true)), "a required INVALID still blocks; optional-only is refused");
        Check(ServerEngine.IsCompletedAutoTestPending(new PendingAudioQaUpload("s", "staff", "p", true, [Step("IO001", "PASS"), Step("IO009", "INVALID", true)], ["a.png", "b.png"],
            true, "id", DateTimeOffset.UtcNow)), "pending queue retries a session whose only unconcluded step is optional");
        AutoTestCaseItem Io9(string grade) => new() { Id = "IO009", Config = io9, MeasurementValues = [new("FBX TẮT · độ mạnh tiếng hú", grade + ": đỉnh nổi 52.0 dB trên nền, mức -9.0 dBFS tại mic, 2150 Hz")] };
        string? loudNote = FeedbackGainDiagnostics.AppendHowlNote("Người đo: ok", [Io9("hú mạnh")]);
        Check(loudNote != null && loudNote.StartsWith("Người đo: ok") && loudNote.Contains("HÚ LỚN") && loudNote.Contains("IO009 · FBX TẮT: hú mạnh")
            && FeedbackGainDiagnostics.AppendHowlNote(loudNote, [Io9("hú mạnh")]) == loudNote
            && FeedbackGainDiagnostics.AppendHowlNote(null, [Io9("hú vừa")]) == null, "a loud howl is added once to the session note; a moderate one is not");

        // 14. v1.5.6 (owner 2026-11-19): the server config is synced to the station, never "press A/B".
        static string Renumber(string name) => System.Text.RegularExpressions.Regex.Replace(name, @"\((\d+)-\s*", m => $"({int.Parse(m.Groups[1].Value) + 1}- ").Replace("  ", " ");
        var rev5Endpoints = rev5.Devices.Input.Values.Distinct().Select((name, i) => new C360ModelEndpoint("o" + i, Renumber(name), false))
            .Concat(rev5.Devices.Output.Values.Distinct().Select((name, i) => new C360ModelEndpoint("i" + i, Renumber(name), true))).ToArray();
        var plan5 = C360ModelPlan.Create(rev5, rev5Endpoints, new("a", "u", "c"));
        string lineId = plan5.PlaybackIds[C360ElectronicStage.LineIn], micId = plan5.PlaybackIds[C360ElectronicStage.MicDry];
        Check(C360ModelPlan.EndpointKey("Analog Connector 1/2 (7- FastTrack Pro)") == C360ModelPlan.EndpointKey("Analog Connector 1/2 (8- FastTrack Pro)")
            && C360ModelPlan.EndpointKey("Headphones (5- KBEATBOX  Stereo)") == "headphones (kbeatbox stereo)" && lineId != micId
            && lineId == rev5Endpoints.Single(e => e.Name == "Analog Connector 1/2 (8- FastTrack Pro)").Id
            && ModelEndpointCheck.Matches("Analog Connector 3/4 (7- FastTrack Pro)", rev5Endpoints.Select(e => new WindowsEndpointName(e.Id, e.Name, e.Capture)), true).Length == 1,
            "config device names written on another PC (7-) resolve to this PC's endpoints (8-), double spaces ignored");
        var lineTest = rev5.Tests.Single(t => t.id == "IO004"); var micTest = rev5.Tests.Single(t => t.id == "IO003");
        C360PortSync NewSync() => new(new Dictionary<string, string> { ["LINE IN"] = lineId, ["MIC IN"] = micId },
            new Dictionary<string, string> { [lineId] = "Analog 1/2", [micId] = "Analog 3/4" });
        bool IsMic(C360ElectronicStage stage) => stage is C360ElectronicStage.MicDry or C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift;
        // Cables swapped against the config: the MIC IN jack hangs on Analog 1/2 and LINE IN on Analog 3/4.
        var swapped = new RouteBench((stage, id) => IsMic(stage) ? id == lineId : id == micId);
        var swapSync = NewSync(); int asks = 0;
        var syncBench = new C360NoSignalRetryBench(swapped, (_, _, _, _) => { asks++; return Task.FromResult(false); }, sync: swapSync);
        var dry = await syncBench.CaptureAsync(C360ElectronicStage.MicDry, plan5.SetupFor(micTest), default);
        var lineResult = await syncBench.CaptureAsync(C360ElectronicStage.LineIn, plan5.SetupFor(lineTest), default);
        Check(dry.Status == "PASS" && dry.Summary.StartsWith("TỰ ĐỒNG BỘ NGÕ SOUND CARD") && lineResult.Status == "PASS" && asks == 0
            && swapped.Played.SequenceEqual([micId, lineId, micId]) && swapSync.EndpointFor("MIC IN") == lineId && swapSync.EndpointFor("LINE IN") == micId
            && swapSync.Notes.Single().Contains("LINE IN đổi sang Analog 3/4"),
            "swapped cables: MIC IN found on the other output, LINE IN follows; no prompt, 3 captures: " + string.Join(",", swapped.Played));
        // Nothing reaches the speaker: every configured output is tried once, then one cable prompt.
        var dead = new RouteBench((_, _) => false); int deadAsks = 0;
        var deadResult = await new C360NoSignalRetryBench(dead, (_, _, _, _) => { deadAsks++; return Task.FromResult(false); }, sync: NewSync())
            .CaptureAsync(C360ElectronicStage.LineIn, plan5.SetupFor(lineTest), default);
        Check(deadResult.Status == "INVALID" && deadAsks == 1 && dead.Played.SequenceEqual([lineId, micId]), "no output carries the signal: both tried, then one cable prompt");
        // A port proven by its own signal is never taken by the other port (MIC IN jack broken, LINE IN fine).
        var broken = new RouteBench((stage, id) => !IsMic(stage) && id == lineId); var brokenSync = NewSync(); int brokenAsks = 0;
        var brokenBench = new C360NoSignalRetryBench(broken, (_, _, _, _) => { brokenAsks++; return Task.FromResult(false); }, sync: brokenSync);
        await brokenBench.CaptureAsync(C360ElectronicStage.LineIn, plan5.SetupFor(lineTest), default);
        var micDead = await brokenBench.CaptureAsync(C360ElectronicStage.MicDry, plan5.SetupFor(micTest), default);
        Check(micDead.Status == "INVALID" && brokenAsks == 1 && broken.Played.SequenceEqual([lineId, micId]) && brokenSync.Notes.Count == 0,
            "LINE IN proven on Analog 1/2 is not tried for MIC IN; MIC IN asks to check its cable");
        // Without a sync (offline fake bench) the retry bench behaves as before.
        var noSync = new RouteBench((_, _) => false); int noSyncAsks = 0;
        await new C360NoSignalRetryBench(noSync, (_, _, _, _) => { noSyncAsks++; return Task.FromResult(false); }).CaptureAsync(C360ElectronicStage.LineIn, plan5.SetupFor(lineTest), default);
        Check(noSync.Played.Count == 1 && noSyncAsks == 1, "no sync: one capture, one prompt (unchanged)");

        // 14b. Effect, FBX (di tần, gain trước hú) and Rè never use a LINE CHUẨN (owner 2026-11-19): one capture, nothing saved or compared.
        Check(new[] { C360ElectronicStage.MicEffects, C360ElectronicStage.MicFrequencyShift, C360ElectronicStage.AcousticRubBuzz, C360ElectronicStage.MicFeedbackGain }
                .All(stage => C360ReferenceBench.UsesNoReference(stage) && !C360ReferenceBench.RequiresReference(stage))
            && !C360ReferenceBench.UsesNoReference(C360ElectronicStage.LineIn) && !C360ReferenceBench.UsesNoReference(C360ElectronicStage.MicDry), "no-reference stages: IO001, IO002, IO008, IO009");
        string refsDir = Path.Combine(output, "refs-noref-" + Guid.NewGuid().ToString("N"));
        foreach (var (stage, id) in new[] { (C360ElectronicStage.MicEffects, "IO001"), (C360ElectronicStage.MicFrequencyShift, "IO002") })
        {
            var counting = new RouteBench((_, _) => true);
            var test = rev5.Tests.Single(t => t.id == id);
            var find = await new C360ReferenceBench(counting, plan5, "C360", refsDir, "mock", true, 3, runBench: _ => counting).CaptureAsync(stage, plan5.SetupFor(test), default);
            var product = await new C360ReferenceBench(counting, plan5, "C360", refsDir, "mock", false, 3).CaptureAsync(stage, plan5.SetupFor(test), default);
            Check(counting.Played.Count == 2 && find.Reference == null && !find.MissingReference && !find.Summary.Contains("LINE CHUẨN")
                && product.Reference == null && !product.Summary.Contains("LINE CHUẨN") && (!Directory.Exists(refsDir) || Directory.GetFiles(refsDir).Length == 0),
                $"{id}: TÌM LINE CHUẨN measures once and saves nothing; Auto Test is not judged against a LINE CHUẨN");
        }

        // 15. Popups: inputs found, server outcome, Rè verdict and chirp picture for IO008.
        string inputs = AudioRouting.InputReport("C360 · AA:BB", "RSSI -48 dBm", ["LINE IN", "BT", "USB A"], ["LINE IN", "BT", "USB A"], [],
            "Sound card: LINE IN = Analog 1/2 ✓ có trong Windows", ["IO001", "IO009 (tùy chọn)"]);
        string lacking = AudioRouting.InputReport("C360 · AA:BB", null, ["LINE IN", "BT"], ["LINE IN", "BT"], ["USB A"], "", ["IO005"]);
        Check(inputs.Contains("Đã chuyển và đọc lại: LINE IN, BT, USB A") && inputs.Contains("Tự đóng") && !inputs.Contains("CHƯA XÁC NHẬN")
            && !inputs.Contains("Sound card: Sound card") && lacking.Contains("CHƯA XÁC NHẬN: USB A") && !lacking.Contains("Tự đóng"), "input report lists inputs; a missing one stays on screen");
        var receipt = new ServerEngine.AudioQaUploadReceipt(201, "log-1", ["a.png", "b.png"], 2);
        var sent = AudioRouting.UploadOutcome(true, false, false, true, true, true, receipt, null, "STAFF", []);
        var sentAdmin = AudioRouting.UploadOutcome(true, false, false, true, true, true, receipt, null, "ADMIN", []);
        var failedUpload = AudioRouting.UploadOutcome(true, false, false, true, true, false, null, "HTTP 500", "STAFF", []);
        var notConcluded = AudioRouting.UploadOutcome(true, false, false, false, true, false, null, null, "STAFF", ["IO004 INVALID"]);
        Check(sent is { Type: ModernMessageBox.MessageBoxType.Info } && sent.Value.Message.Contains("FACTORY FLOOR QA & APP TRACKER") && sent.Value.Message.Contains("Quy trình QA/QC")
            && !sent.Value.Message.Contains("STAFF") && sentAdmin!.Value.Message.Contains("ADMIN")
            && failedUpload is { Type: ModernMessageBox.MessageBoxType.Error } && failedUpload.Value.Message.Contains("GỬI LẠI KẾT QUẢ CHỜ")
            && notConcluded is { Type: ModernMessageBox.MessageBoxType.Warning } && notConcluded.Value.Message.Contains("IO004 INVALID")
            && AudioRouting.UploadOutcome(false, false, false, true, true, true, receipt, null, "STAFF", []) == null, "server outcome popup: sent / account / failed / not concluded");
        var io8 = rev5.Tests.Single(t => t.id == "IO008");
        var pinkRun = PinkRubBuzzConfiguredTest.JudgeAbsolute(Pink(12), pinkLimits);
        var chirpRun = ChirpRubBuzzConfiguredTest.JudgeAbsolute(new ChirpRubBuzzRun(new(), "key", "route", 48000, new ChirpRubBuzzMetrics(true, "")
            { PeakBumpDb = 4, PeakBumpHz = 120, FrequenciesHz = freqs, BumpDb = [1, 2, 4, 3, 1, 0, 0], RepeatBumpDb = [[1, 2, 4, 3, 1, 0, 0]] }), chirpLimits);
        var rubRow = new AutoTestCaseItem { Id = "IO008", Config = io8, Status = "PASS", AdditionalResults = [new ConfiguredTestResult("Rè/rung · Pink + Chirp", "PASS", "") { PinkRubBuzz = pinkRun, ChirpRubBuzz = chirpRun }] };
        string verdict = AudioRouting.RubBuzzVerdictLine(rubRow);
        var notRun = new AutoTestCaseItem { Id = "IO008", Config = io8, Status = "INVALID", AdditionalResults = [new ConfiguredTestResult("Rè Pink", "INVALID", "Bản thu mic chính chưa hợp lệ; chưa phát pink.")] };
        var drawn = new ScottPlot.Plot(); drawn.Add.Scatter(new double[] { 1, 2 }, new double[] { 1, 2 });
        RubBuzzQaGraph.Draw(drawn, chirpRun, "IO008");
        Check(verdict.Contains("Pink PASS: rè lặp lại 12.0% (PASS < 30%)") && verdict.Contains("Chirp PASS: đỉnh rè 4.0 dB tại 120 Hz (PASS < 10 dB)")
            && AudioRouting.RubBuzzVerdictLine(notRun).Contains("chưa phát pink") && drawn.GetPlottables().Count() >= 2 && drawn.Axes.Title.Label.Text.Contains("PASS"),
            "IO008 shows the Rè verdict and redraws the chirp graph into the result view: " + verdict);

        File.WriteAllText(Path.Combine(output, "c360-v152.txt"), $"PASS {passed} v1.5.3 checks (offline, synthetic bench)");
        Console.WriteLine($"PASS {passed} v1.5.3 checks: IO008 stage, no response reference for Rè, auto LINE CHUẨN, A/B retry, FBX note, 2/1 runs, port report, screen fit. No hardware.");
    }

    /// <summary>Hears the stage only when it is played on the endpoint the wiring connects (stage, endpoint id → signal).</summary>
    private sealed class RouteBench(Func<C360ElectronicStage, string, bool> hears) : IC360ElectronicBench
    {
        public List<string> Played { get; } = [];
        private readonly SignalBench _signal = new();
        public string DeviceIdentity => "v156-route";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException("No BLE");
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new NotSupportedException("No BLE");
        public void StopAudio() { }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            string id = setup.PlaybackId(stage); Played.Add(id);
            return Task.FromResult(hears(stage, id) ? _signal.Result(stage, "PASS", snr: 46) : _signal.Result(stage, "INVALID", snr: .3));
        }
    }

    private sealed class QueueBench(Queue<C360ElectronicResult> results) : IC360ElectronicBench
    {
        public string DeviceIdentity => "v155-mock";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException("No BLE");
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new NotSupportedException("No BLE");
        public void StopAudio() { }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token) => Task.FromResult(results.Dequeue());
    }

    private sealed class SignalBench : IC360ElectronicBench
    {
        public Queue<string>? Queue { get; init; }
        public string DeviceIdentity => "v152-mock";
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => throw new NotSupportedException("No BLE");
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => throw new NotSupportedException("No BLE");
        public void StopAudio() { }
        public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            string next = Queue is { Count: > 0 } queue ? queue.Dequeue() : "ok";
            return Task.FromResult(next == "silent" ? Result(stage, "INVALID", snr: .3) : Result(stage, "PASS", snr: 46));
        }
        public C360ElectronicResult Result(C360ElectronicStage stage, string status, double snr)
        {
            var curve = new Dictionary<double, double> { [50] = 0, [65] = 0, [80] = 0, [1000] = 0 };
            var measurement = JsonSerializer.Deserialize<StandardAcousticResult>(JsonSerializer.Serialize(new {
                FrequencyResponse = curve.Select(p => new { FrequencyHz = p.Key, LevelDb = p.Value }), ExcitationRmsDbFs = 0, Validity = "DIAGNOSTIC",
                EstimatedSnrDb = snr, SignalLevelDbFs = snr < 10 ? -77 : -11
            }, C360Reference.Json), C360Reference.Json)!;
            return new(stage, status, "synthetic " + status, measurement);
        }
    }
}
