using System.Text;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using SoncaAudioInspector;

public static class RubBuzzDiagnosisChecks
{
    public static void Run(Action<bool, string> check, string output)
    {
        const string micId = "{0.0.1.00000000}.{12635ac4-b45b-420c-b2a4-81edd1a005ee}";
        var properties = new Dictionary<string, object?>
        {
            [CapturePathEnhancements.DisableSysFxKey] = 0,
            ["{9e6136e0-57ab-4949-b57a-3627be142855},100"] = @"SWD\DRIVERENUM\{96bedf2c-18cb-4a15-b821-5e95ed0fea61}#VocaEffectPack&1&1c6495cf&0",
            ["{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0"] = new[] { "{238a216b-0000-0000-0000-000000000001}" }
        };
        var on = CapturePathEnhancements.Parse(micId, "MIC (Umik-1 Gain: 18dB)", properties);
        check(on.MayProcessShared && on.Warning.Contains("VocaEffectPack") && on.Warning.Contains("Exclusive không bị ảnh hưởng"),
            "E registry ON fixture: APO + Disable_SysFx=0 warns Shared and preserves Exclusive scope");
        properties[CapturePathEnhancements.DisableSysFxKey] = 1;
        properties.Remove("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0");
        var off = CapturePathEnhancements.Parse(micId, "MIC (Umik-1 Gain: 18dB)", properties);
        check(off.EnhancementsOff && !off.MayProcessShared && off.Warning == "" && off.EffectNames.Length == 1,
            "E registry OFF fixture (UMIK 2026-11-14): Disable_SysFx=1 overrides installed APO entries");
        check(on.DisableSysFx == 0 && CapturePathEnhancements.EndpointGuid(micId) == "{12635ac4-b45b-420c-b2a4-81edd1a005ee}"
            && CapturePathEnhancements.EndpointGuid("../bad") == null && off.Tooltip.Contains("cổng/hub USB"),
            "E parser is immutable, extracts endpoint GUID strictly, explains USB endpoint reset");
        properties.Remove(CapturePathEnhancements.DisableSysFxKey);
        check(CapturePathEnhancements.Parse(micId, "mic", properties).MayProcessShared,
            "E missing Disable_SysFx with an APO remains a warning");
        var unknown = CapturePathEnhancements.Parse(micId, "mic", new Dictionary<string, object?>(), false, "fixture denied");
        check(!unknown.EnhancementsOff && !unknown.ReadSucceeded && !unknown.MayProcessShared,
            "E denied registry is unknown, never reported as enhancements Off");

        PinkDiagnosticEvidence Evidence(double[] events, double peaks = 5, double quiet = 3, double loud = 4,
            double kurtosis = 10, double tail = 50) => new(events.Select((x, i) =>
                new PinkDiagnosticRepeat(i + 1, x, peaks, -78 - i, -20 + .002 * i, quiet, loud, kurtosis, tail, .99)).ToArray(), -84);
        var declining = Evidence([9.8, 6, 5]);
        var diagnosis = RubBuzzDiagnosis.Build(null, null, pinkEvidence: declining, capturePath: on,
            chirpEvidence: new(null, null, null, 40, 300, Math.Log2(300 / 40.0), 4));
        RubBuzzSign Sign(RubBuzzDiagnosisResult result, string key) => result.Signs.Single(s => s.Key == key);
        bool Has(RubBuzzDiagnosisResult result, string key, RubBuzzCause cause, bool supports) =>
            Sign(result, key).Directions.Any(d => d.Cause == cause && d.Supports == supports);
        check(diagnosis.Signs.Length == 10 && diagnosis.Version == "rubbuzz-diagnostics-1.0.0", "C all ten signs always present; independent diagnostic version");
        check(Has(diagnosis, "S1", RubBuzzCause.AmpClip, false) && Has(diagnosis, "S1", RubBuzzCause.DspLimiter, false),
            "C S1 low share at drive peaks contradicts instantaneous clipping/limiting");
        check(Has(diagnosis, "S2", RubBuzzCause.ElectronicNoise, true) && Has(diagnosis, "S2", RubBuzzCause.Mechanical, false),
            "C S2 <1.5 dB quiet/loud with HF present supports electronics and contradicts excursion mechanism");
        check(Has(diagnosis, "S3", RubBuzzCause.Mechanical, true) && Sign(diagnosis, "S3").Caveat!.Contains("DSP thích nghi chậm"),
            "C S3 9.8→6→5% with stable bass supports mechanical with adaptive-DSP caveat");
        check(Has(diagnosis, "S4", RubBuzzCause.Mechanical, true) && !Has(diagnosis, "S5", RubBuzzCause.Resonance, true)
            && Sign(diagnosis, "S5").Meaning.Contains("dải kích rộng"), "C S4 kurtosis>6 and S5 broad excess are distinct observations");
        // Rè captures Exclusive: a Shared-path APO is a strong warning but cannot explain the HF, so S8 votes against CapturePath.
        check(Sign(diagnosis, "S6").Meaning.Contains("tắt nhanh") && Has(diagnosis, "S8", RubBuzzCause.CapturePath, false)
            && !Has(diagnosis, "S8", RubBuzzCause.CapturePath, true) && Sign(diagnosis, "S8").Strength == "Mạnh"
            && Sign(diagnosis, "S8").Meaning.Contains("Exclusive không bị ảnh hưởng"), "C S6 fast tail; S8 warns on Shared APO but does not blame an Exclusive Rè capture");
        var peaksAndResonance = RubBuzzDiagnosis.Build(null, null, pinkEvidence: Evidence([1, 2, 3], 80, 1, 8, 3, 200),
            chirpEvidence: new(246, 22.8, .25, 0, 0, 0, 0));
        check(Has(peaksAndResonance, "S1", RubBuzzCause.AmpClip, true) && Has(peaksAndResonance, "S2", RubBuzzCause.Mechanical, true)
            && Has(peaksAndResonance, "S5", RubBuzzCause.Resonance, true) && Has(peaksAndResonance, "S6", RubBuzzCause.Room, true),
            "C S1 peak-cluster, S2 ≥6dB, narrow 246Hz resonance and long tail vote independently");
        check(Has(peaksAndResonance, "S4", RubBuzzCause.ElectronicNoise, true) && !Has(peaksAndResonance, "S3", RubBuzzCause.Mechanical, true),
            "C Gaussian kurtosis≈3 supports noise; increasing repeats do not create a decreasing-trend sign");
        var broadband = RubBuzzDiagnosis.Build(null, null, broadband: new(true, -20, -40, 12, 35));
        check(Has(broadband, "S10", RubBuzzCause.Mechanical, true), "C S10 high HF nonlinear residual plus impulsive kurtosis supports mechanical");
        var insensitive = RubBuzzDiagnosis.Build(null, null, broadband: new(false, -20, -40, 12, 5), pinkEvidence: Evidence([1, 1, 1]) with { BackgroundHfDbFs = -79 });
        check(Sign(insensitive, "S7").Strength == "Yếu" && Sign(insensitive, "S10").Strength == "Chưa đủ dữ liệu"
            && Sign(insensitive, "S7").Caveat!.Contains("RC phụ thuộc SNR"), "C weak SNR limits conclusions and never creates a measured NL sign");
        var rows = new[]
        {
            new RubBuzzLevelRow(-30,-37.2,.9,-92,-54.8,26.9,0,[0,0,0]),
            new RubBuzzLevelRow(-24,-31.2,1.9,-88.2,-57,67.5,0,[0,0,0]),
            new RubBuzzLevelRow(-18,-25.3,3.2,-85.2,-59.9,77.9,1.1,[1.8,1,.5]),
            new RubBuzzLevelRow(-15,-23.7,3,-85.6,-61.9,80.1,.5,[.5,.5,.5])
        };
        var levelEvidence = new RubBuzzLevelDiagnostics(rows, [new(-18, -15, 3, 1.6, -.25, true)], .5, -30, -18);
        var levelDiagnosis = RubBuzzDiagnosis.Build(null, null, levels: levelEvidence);
        // 14/11 numbers: bass compresses above −18 while speaker HF stays flat → the limiter exists but is clean.
        check(Has(levelDiagnosis, "S9", RubBuzzCause.Mechanical, false) && Has(levelDiagnosis, "S9", RubBuzzCause.ElectronicNoise, true)
            && Has(levelDiagnosis, "S9", RubBuzzCause.DspLimiter, false) && !Has(levelDiagnosis, "S9", RubBuzzCause.DspLimiter, true)
            && Sign(levelDiagnosis, "S9").Meaning.Contains("nén sạch"),
            "C S9 lowest-level RC + slope≤1 contradicts mechanical; compression with flat HF = clean limiter, not the HF source");
        var artifactRows = rows.Take(3).Append(new RubBuzzLevelRow(-15, -23.7, 8, -79.2, -55.5, 85, 4, [4, 4, 4])).ToArray();
        var artifactDiagnosis = RubBuzzDiagnosis.Build(null, null, levels: levelEvidence with { Rows = artifactRows });
        check(Has(artifactDiagnosis, "S9", RubBuzzCause.DspLimiter, true),
            "C S9 HF jumping ≥3 dB faster than bass above the knee supports limiter/clip artifacts");
        check(diagnosis.Hypotheses.All(h => h.Score == diagnosis.Signs.Sum(sign =>
            (sign.Directions.Any(d => d.Cause == h.Cause && d.Supports) ? 1 : sign.Directions.Any(d => d.Cause == h.Cause && !d.Supports) ? -1 : 0)
            * (sign.Strength == "Mạnh" ? 3 : sign.Strength == "Vừa" ? 2 : sign.Strength == "Yếu" ? 1 : 0))),
            "C hypothesis score equals disclosed signed evidence weights");
        check(RubBuzzDiagnosis.Build(null, null).Signs.All(x => x.Strength == "Chưa đủ dữ liệu")
            && diagnosis.CopyableText.Contains("DẤU HIỆU & CHẨN ĐOÁN") && diagnosis.NextChecks.Any(x => x.Contains("Chẩn đoán theo mức")),
            "C missing inputs stay missing; copyable report and concrete next checks exist");
        check(PinkRubBuzzAnalysis.Version == "pink-rattle-2.0.0" && ChirpRubBuzzAnalysis.Version == "chirp-rattle-1.0.0",
            "C additive diagnostics preserve legacy algorithm versions");
        bool rejectedNan = false;
        try { new RubBuzzDiagnosisSettings(QuietLoudFlatDb: double.NaN).Validate(); } catch (ArgumentException) { rejectedNan = true; }
        check(rejectedNan, "C diagnostic settings reject NaN thresholds");
        var badBackground = new float[3 * 48000];
        var fakePink = new PinkRubBuzzRun(new(), new string('A', 64), "invalid fixture", 48000,
            new(false, "Nền số 0") { HfDbFs = -60, BackgroundHfDbFs = -120 })
            { Captures = [new float[6 * 48000], new float[6 * 48000], new float[6 * 48000]], Background = badBackground };
        var gated = RubBuzzDiagnosis.Build(fakePink, null, pinkEvidence: declining);
        check(gated.Signs.Where(x => x.Key != "S8").All(x => x.Directions.Length == 0)
            && gated.PinkEvidence == null, "C zero acquisition suppresses cause votes even if stale evidence was supplied");
        var invalidLevels = RubBuzzLevelDiagnostics.Analyze([fakePink, fakePink with { Settings = new(-24) }]);
        check(invalidLevels.Rows.All(x => !x.IsUsable && x.BassDbFs == null && x.RepeatCoherencePct == null)
            && invalidLevels.Steps.Length == 0 && invalidLevels.SpeakerHfSlopeDbPerDb == null
            && RubBuzzDiagnosis.Build(null, null, levels: invalidLevels).Signs.Single(x => x.Key == "S9").Directions.Length == 0,
            "D invalid acquisition yields nullable rows, no synthetic zero-gain compression or onset votes");
        Replay(check, output);
    }

    private static bool RecordingExists(string path) => File.Exists(path) || File.Exists(path + ".f32.gz");
    private static float[] Wave(string path)
    {
        if (!File.Exists(path))
        {
            using var compressed = File.OpenRead(path + ".f32.gz");
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var memory = new MemoryStream(); gzip.CopyTo(memory);
            byte[] bytes = memory.ToArray(); var values = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
        using var reader = new WaveFileReader(path); var provider = reader.ToSampleProvider();
        if (provider.WaveFormat.SampleRate != 48000) throw new InvalidDataException("Replay capture must be 48 kHz: " + path);
        int channels = provider.WaveFormat.Channels; var samples = new List<float>(); var buffer = new float[48000 * channels];
        int count; while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < count; i += channels) samples.Add(buffer[i]);
        return samples.ToArray();
    }
    private static PinkRubBuzzRun Pink(string folder, string bg, Func<int, string> capture, double level = -18)
    {
        var settings = new PinkRubBuzzSettings(level); var background = Wave(Path.Combine(folder, bg));
        var captures = Enumerable.Range(1, 3).Select(i => Wave(Path.Combine(folder, capture(i)))).ToArray();
        return new(settings, new string('A', 64), "Replay offline", 48000,
            PinkRubBuzzAnalysis.Analyze(captures, background, 48000, settings)) { Captures = captures, Background = background };
    }
    private static ChirpRubBuzzRun Chirp(string folder, string bg, Func<int, string> capture, int repeats = 3)
    {
        var settings = new ChirpRubBuzzSettings(Repeats: repeats); var background = Wave(Path.Combine(folder, bg));
        var captures = Enumerable.Range(1, repeats).Select(i => Wave(Path.Combine(folder, capture(i)))).ToArray();
        return new(settings, new string('B', 64), "Replay offline", 48000,
            ChirpRubBuzzAnalysis.Analyze(captures, background, 48000, settings)) { Captures = captures, Background = background };
    }
    private static void Replay(Action<bool, string> check, string output)
    {
        const string base14 = "artifacts/20261114-064502-633Z-rubbuzz-remeasure-review";
        const string base06 = "artifacts/20261106-070750-775Z-claude-rattle-remeasure";
        var datasets = new List<(string Name, RubBuzzDiagnosisResult Result)>();
        var messages = new List<string>(); RubBuzzLevelDiagnostics? levels14 = null, levels06 = null;
        if (File.Exists(Path.Combine(base14, "level-step/bg.wav")))
        {
            var runs = new[] { -30, -24, -18, -15, -12 }.Select(level =>
            {
                // Historical replay: the 14/11 script scaled the −18 waveform, as the app now does above −18.
                var scale = level <= -18 ? level : -18;
                return Pink(Path.Combine(base14, "level-step"), "bg.wav", i => $"pink{level}-{i}.wav", scale)
                    with { Settings = new PinkRubBuzzSettings(level) };
            }).ToArray();
            // Row analysis keeps the −18 nominal shape (identical shape above −18); labels are corrected below.
            var capped = runs.Select(r => r.Settings.LevelRmsDbFs > -18 ? r with { Settings = r.Settings with { LevelRmsDbFs = -18 } } : r).ToArray();
            var analyzed = RubBuzzLevelDiagnostics.Analyze(capped);
            var correctedRows = analyzed.Rows.Select((row, i) => row with { StimulusRmsDbFs = new[] { -30, -24, -18, -15, -12 }[i] }).ToArray();
            var correctedSteps = correctedRows.Zip(correctedRows.Skip(1), (a, b) => new RubBuzzLevelStep(a.StimulusRmsDbFs, b.StimulusRmsDbFs,
                b.StimulusRmsDbFs-a.StimulusRmsDbFs, b.BassDbFs!.Value-a.BassDbFs!.Value, null, b.BassDbFs-a.BassDbFs < .7*(b.StimulusRmsDbFs-a.StimulusRmsDbFs))).ToArray();
            levels14 = analyzed with { Rows = correctedRows, Steps = correctedSteps, OnsetLevelDbFs = correctedRows.First(r => r.RepeatCoherencePct >= 15).StimulusRmsDbFs,
                LimiterKneeLevelDbFs = correctedSteps.FirstOrDefault(x => x.Compressed)?.FromLevelDbFs };
            check(correctedRows[0].RepeatCoherencePct >= 20 && levels14.SpeakerHfSlopeDbPerDb < 1,
                $"C replay 14/11: lowest-level RC={correctedRows[0].RepeatCoherencePct:0.0}% ≥20; HF/bass slope={levels14.SpeakerHfSlopeDbPerDb:0.00}<1");
            check(correctedSteps.Where(x => x.ToLevelDbFs > -18).All(x => x.Compressed), "C replay 14/11: −15/−12 dBFS historical captures show bass compression");
            var at18 = runs[2];
            var result = RubBuzzDiagnosis.Build(at18, Chirp(Path.Combine(base14, "level-step"), "bg.wav", i => $"chirp-18-{i}.wav"), levels: levels14);
            check(result.PinkEvidence!.QuietLoudDifferenceDb is > .5 and < 3,
                $"C replay 14/11 −18: S2 quiet/loud difference {result.PinkEvidence.QuietLoudDifferenceDb:0.00} dB in measured 1–2 dB region");
            datasets.Add(("14/11 theo mức −18 (kèm 5 mức lịch sử)", result));
        }
        else messages.Add("SKIP 14/11 level-step: optional historical WAVs absent.");
        foreach (string folder in new[] { "rubbuzz-20261114-064123-716-6b5ec339", "rubbuzz-20261114-064316-122-285a1a95" })
        {
            string path = Path.Combine(base14, "captures", folder);
            if (!File.Exists(Path.Combine(path, "pink-background.wav"))) { messages.Add("SKIP " + folder + ": optional WAVs absent."); continue; }
            var pink = Pink(path, "pink-background.wav", i => $"pink-microphone-{i}.wav");
            var chirp = Chirp(path, "chirp-background.wav", i => $"chirp-microphone-{i}.wav");
            var result = RubBuzzDiagnosis.Build(pink, chirp, levels: levels14);
            check(chirp.Metrics.PeakBumpDb < 6 && result.ChirpEvidence!.BroadSpanOctaves >= 1,
                $"C replay {folder}: chirp bump {chirp.Metrics.PeakBumpDb:0.00}<6dB, 4–12kHz broad excess spans {result.ChirpEvidence.BroadSpanOctaves:0.00} octave≥1");
            datasets.Add((folder, result));
        }
        string pink06 = RecordingExists(Path.Combine(base06, "live-tol/bg-excl.wav"))
            ? Path.Combine(base06, "live-tol")
            : "tools/PinkRubBuzzCheck/Fixtures/pink/live-tol";
        if (RecordingExists(Path.Combine(pink06, "bg-excl.wav")))
        {
            var runs = new[] { -30, -24, -18 }.Select(level => Pink(pink06, "bg-excl.wav", i => $"cap-a{-level}-{i}.wav", level)).ToArray();
            levels06 = RubBuzzLevelDiagnostics.Analyze(runs);
            check(runs[0].Metrics.RepeatCoherencePct < 5 && runs[1].Metrics.RepeatCoherencePct > runs[0].Metrics.RepeatCoherencePct,
                $"C replay 06/11 known rattler: RC −30={runs[0].Metrics.RepeatCoherencePct:0.0}%<5 and increases at −24");
            datasets.Add(("06/11 rattler Pink (lượt live-tol −18; 3 mức)", RubBuzzDiagnosis.Build(runs[2], null, levels: levels06)));
        }
        else messages.Add("SKIP 06/11 level replay: optional historical WAVs absent.");
        string chirp06 = RecordingExists("artifacts/20261106-085907-845Z-rattle-listening/chirp-3s/background.wav")
            ? "artifacts/20261106-085907-845Z-rattle-listening/chirp-3s"
            : "tools/PinkRubBuzzCheck/Fixtures/chirp/chirp-3s";
        if (RecordingExists(Path.Combine(chirp06, "background.wav")))
        {
            var chirp = Chirp(chirp06, "background.wav", i => $"capture-{i}.wav", 2);
            var result = RubBuzzDiagnosis.Build(null, chirp);
            check(result.ChirpEvidence?.NarrowFrequencyHz is >= 240 and <= 250,
                $"C replay 06/11 rattler Chirp: narrow resonance {result.ChirpEvidence?.NarrowFrequencyHz:0.0}Hz inside 240–250Hz");
            datasets.Add(("06/11 rattler Chirp (session riêng, 2 lượt)", result));
        }
        else messages.Add("SKIP 06/11 chirp replay: optional historical WAVs absent.");
        var report = new StringBuilder("# C/E diagnosis replay (offline)\n\nSource: 9d80361e7f7e93c40838a1f2aa3fec43a02d45eb plus uncommitted changes. Historical endpoint state is unknown in replay; no live device or registry write. A/B signs remain uncomputed in this C-only table.\n\n");
        foreach (string message in messages) report.AppendLine(message);
        report.AppendLine("\n| Dataset | Sign | Measured value | Meaning | Strength |\n|---|---|---|---|---|");
        foreach (var dataset in datasets)
            foreach (var sign in dataset.Result.Signs)
                report.AppendLine($"| {dataset.Name} | {sign.Key} {sign.Name} | {sign.Measurement.Replace("|", "\\|")} | {sign.Meaning.Replace("|", "\\|")} | {sign.Strength} |");
        foreach (var pair in new[] { ("14/11", levels14), ("06/11", levels06) })
        {
            if (pair.Item2 == null) continue;
            report.AppendLine($"\n## {pair.Item1} level rows\n\n| Level dBFS RMS | Bass dBFS | HF excess dB | Speaker HF dBFS | HF/bass dB | RC % | Events % |\n|---|---|---|---|---|---|---|");
            foreach (var row in pair.Item2.Rows) report.AppendLine($"| {row.StimulusRmsDbFs:0} | {row.BassDbFs:0.00} | {row.HfExcessDb:0.00} | {row.SpeakerHfDbFs:0.00} | {row.HfBassDb:0.00} | {row.RepeatCoherencePct:0.00} | {row.EventsPct:0.00} |");
        }
        File.WriteAllText(Path.Combine(output, "diagnosis-replay.md"), report.ToString());
        File.WriteAllText(Path.Combine(output, "diagnosis-replay.json"), JsonSerializer.Serialize(datasets.Select(x => new { dataset = x.Name, diagnosis = x.Result }), new JsonSerializerOptions { WriteIndented = true }));
    }
}
