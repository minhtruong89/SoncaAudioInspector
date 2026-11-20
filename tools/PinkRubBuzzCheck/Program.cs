using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;
using SoncaAudioInspector;

// pink-rattle-2.0 checks: synthetic DSP cases, line standard + allowed ratios, real UMIK-1 captures, UI render.
// Usage: dotnet PinkRubBuzzCheck.dll <app-dir-with-SoncaAudioInspector.dll> <new-output-dir> [<claude-remeasure-artifact>] [<codex-live-artifact>]
static partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    private static int _checks;
    private static readonly List<string> Log = [];
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        _checks++; Log.Add("PASS " + message); Console.WriteLine("PASS " + message);
    }
    static void Reject(Action action, string message) { try { action(); } catch { Check(true, message); return; } throw new Exception("FAIL " + message); }
    [STAThread] static void Main(string[] args)
    {
        string app = Path.GetFullPath(args[0]);
        SetDllDirectory(Path.Combine(app, "runtimes", "win-x64", "native"));
        AssemblyLoadContext.Default.Resolving += (_, name) => { string path = Path.Combine(app, name.Name + ".dll"); return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null; };
        string output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Output must be new.");
        Directory.CreateDirectory(output);
        // Full replay uses committed first-channel lossless float fixtures; no previous artifact is required.
        const string fixtures = "tools/PinkRubBuzzCheck/Fixtures";
        try { Run(output, args.Length > 2 ? args[2] : Path.Combine(fixtures, "pink"), args.Length > 3 ? args[3] : Path.Combine(fixtures, "gated"), args.Length > 4 ? args[4] : Path.Combine(fixtures, "chirp")); }
        finally { File.WriteAllLines(Path.Combine(output, "checks.txt"), Log); }
        Console.WriteLine($"ALL {_checks} PINK RATTLE CHECKS PASSED");
    }

    const int Rate = 48000;
    static float[] Lf(double seconds, double levelDbFs)
    {
        var period = PinkNoiseSignal.CreatePeriod(new PinkNoiseSettings(25, 150, levelDbFs), Rate);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(i => period[i % period.Length]).ToArray();
    }
    static float[] Noise(int n, double rms, int seed)
    {
        var r = new Random(seed); var x = new float[n];
        for (int i = 0; i < n; i += 2)
        {
            double u1 = 1 - r.NextDouble(), u2 = r.NextDouble(), m = Math.Sqrt(-2 * Math.Log(u1));
            x[i] = (float)(rms * m * Math.Cos(2 * Math.PI * u2)); if (i + 1 < n) x[i + 1] = (float)(rms * m * Math.Sin(2 * Math.PI * u2));
        }
        return x;
    }
    static float[] HighPass(float[] x, double f)
    {
        var d = PinkRubBuzzAnalysis.BandPass(x, Rate, f, 0.45 * Rate, 8); return d.Select(v => (float)v).ToArray();
    }
    /// <summary>A capture: LF drive, room/hiss noise (random per lượt), optional rattle.</summary>
    static float[] Capture(float[] lf, int seed, float[]? deterministicRattle = null, float[]? randomRattle = null, double hissRms = 3e-5)
    {
        var hiss = HighPass(Noise(lf.Length, hissRms * 3, seed), 1000);
        var x = new float[lf.Length + Rate / 2];
        for (int i = 0; i < lf.Length; i++)
            x[i + Rate / 4] = lf[i] + hiss[i] + (deterministicRattle?[i] ?? 0) + (randomRattle?[i] ?? 0);
        var tail = Noise(x.Length, hissRms, seed + 1000);
        for (int i = 0; i < x.Length; i++) if (i < Rate / 4 || i >= lf.Length + Rate / 4) x[i] = tail[i] * .2f;
        return x;
    }
    /// <summary>Rattle that fires when the bass exceeds a threshold: short 4–12 kHz bursts, identical on every lượt when seeded.</summary>
    static float[] Rattle(float[] lf, double threshold, double amplitude, int seed, bool randomTiming)
    {
        double rms = Math.Sqrt(lf.Average(v => (double)v * v));
        var burst = HighPass(Noise(Rate / 500, 1, seed), 4000); var x = new float[lf.Length]; var r = new Random(seed);
        for (int i = 1; i < lf.Length - burst.Length; i++)
        {
            bool trigger = randomTiming ? r.NextDouble() < 0.004 : Math.Abs(lf[i]) > threshold * rms && Math.Abs(lf[i - 1]) <= threshold * rms;
            if (!trigger) continue;
            for (int j = 0; j < burst.Length; j++) x[i + j] += (float)(amplitude * burst[j]);
        }
        return x;
    }
    static float[] Background(double seconds, double rms, int seed, bool gated = false)
    {
        var x = Noise((int)(seconds * Rate), rms, seed);
        if (gated) for (int i = 0; i < x.Length; i++) x[i] *= (float)Math.Exp(-i / (0.3 * Rate));
        return x;
    }
    static float[] Resample(float[] x, double ratio)
    {
        var y = new float[(int)(x.Length / ratio) - 2];
        for (int i = 0; i < y.Length; i++) { double p = i * ratio; int a = (int)p; double f = p - a; y[i] = (float)(x[a] * (1 - f) + x[a + 1] * f); }
        return y;
    }
    static PinkRubBuzzRun Run(float[][] caps, float[] bg, PinkRubBuzzSettings? settings = null)
    {
        settings ??= new();
        var m = PinkRubBuzzAnalysis.Analyze(caps, bg, Rate, settings);
        return new PinkRubBuzzRun(settings, new string('A', 64), "synthetic", Rate, m) { Captures = caps, Background = bg };
    }
    static float[] ReadWav(string path)
    {
        if (!File.Exists(path))
        {
            using var compressed = File.OpenRead(path + ".f32.gz");
            using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
            using var memory = new MemoryStream(); gzip.CopyTo(memory);
            byte[] bytes = memory.ToArray(); var values = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); return values;
        }
        using var reader = new WaveFileReader(path);
        var samples = reader.ToSampleProvider(); int channels = samples.WaveFormat.Channels;
        if (samples.WaveFormat.SampleRate != Rate) throw new InvalidDataException("Expected 48 kHz capture: " + path);
        var all = new List<float>(); var buffer = new float[Rate * channels];
        int read; while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) for (int i = 0; i < read; i += channels) all.Add(buffer[i]);
        return all.ToArray();
    }

    /// <summary>The app synthesizes every Rè stimulus at run time; the shipped WAVs are listening/reference copies only.
    /// They must stay identical to what the app plays, so an outdated file cannot be mistaken for the test signal.</summary>
    static void AssetChecks()
    {
        static float[] Asset(string name, out WaveFormat format)
        {
            using var reader = new WaveFileReader(Path.Combine("Assets", "Audio", name)); format = reader.WaveFormat;
            var provider = reader.ToSampleProvider(); var samples = new float[reader.SampleCount]; int read = 0, n;
            while (read < samples.Length && (n = provider.Read(samples, read, samples.Length - read)) > 0) read += n;
            return samples[..read];
        }
        Check(!File.Exists(Path.Combine("Assets", "Audio", "pink-rattle-30-80Hz-44100Hz-minus18dBFS-RMS.wav")),
            "outdated v1.2 pink WAV (full-band, random, not the pink-rattle-2.0 stimulus) is not shipped");
        var pink = Asset("pink-rattle-25-150Hz-6s-44100Hz-minus18dBFS-RMS.wav", out var pinkFormat);
        var expected = PinkRubBuzzStimulus.Generate(new PinkRubBuzzSettings(), PinkRubBuzzStimulus.SampleRate);
        Check(pinkFormat.Encoding == WaveFormatEncoding.IeeeFloat && pinkFormat.SampleRate == 44100 && pinkFormat.Channels == 1
            && pink.AsSpan().SequenceEqual(expected),
            "shipped pink WAV is bit-identical to PinkRubBuzzStimulus (25–150 Hz, 6 s, −18 dBFS RMS, 44.1 kHz float)");
        foreach (double seconds in new[] { 3.0, 10.0 })
        {
            var chirp = Asset($"chirp-rattle-20-2000Hz-{seconds:0}s-44100Hz-minus12dBFS-peak.wav", out _);
            var reference = ChirpRubBuzzStimulus.Generate(new ChirpRubBuzzSettings(DurationSeconds: seconds), 44100);
            double worst = chirp.Length == reference.Length ? chirp.Zip(reference, (a, b) => Math.Abs((double)a - b)).Max() : 1;
            Check(20 * Math.Log10(Math.Max(1e-12, worst)) < -70,
                $"shipped chirp {seconds:0} s WAV matches ChirpRubBuzzStimulus (max error {20 * Math.Log10(Math.Max(1e-12, worst)):0.0} dBFS < −70)");
        }
    }

    static void Run(string output, string? claudeArtifact, string? codexArtifact, string? chirpArtifact)
    {
        // --- Stimulus --------------------------------------------------------------------------------------
        var settings = new PinkRubBuzzSettings();
        var s44 = PinkRubBuzzStimulus.Generate(settings, 44100);
        Check(s44.Length == 6 * 44100 && s44.Max(Math.Abs) < .7 && s44[0] == 0 && s44[^1] == 0, "stimulus 6 s, headroom, faded ends");
        Check(PinkRubBuzzStimulus.Hash(settings) == PinkRubBuzzStimulus.Hash(settings) && PinkRubBuzzStimulus.Hash(settings) != PinkRubBuzzStimulus.Hash(settings with { LevelRmsDbFs = -24 }),
            "stimulus deterministic; hash changes with level");
        Check(PinkRubBuzzStimulus.Generate(settings, 48000).Length == 6 * 48000, "48 kHz playback supported (C360)");
        Reject(() => PinkRubBuzzStimulus.Generate(settings, 32000), "unsupported playback rate rejected");
        Reject(() => new PinkRubBuzzSettings(-11).Validate(), "level above −12 dBFS RMS rejected");
        foreach (int rate in new[] { 44100, 48000 })
        {
            // Above −18 the −18 waveform is only scaled: same shape, no digital clip, and ≤ −18 hashes stay as before.
            var at18 = PinkRubBuzzStimulus.Generate(settings, rate); var at12 = PinkRubBuzzStimulus.Generate(settings with { LevelRmsDbFs = -12 }, rate);
            double gain = Math.Pow(10, 6 / 20.0), worst = at18.Zip(at12, (a, b) => Math.Abs(b - gain * a)).Max();
            double rms12 = 10 * Math.Log10(at12.Skip(rate).Take(4 * rate).Average(v => (double)v * v));
            Check(worst < 1e-6 && at12.Max(Math.Abs) < .98f && Math.Abs(rms12 + 12) < .5,
                $"−12 dBFS stimulus at {rate} Hz = −18 shape × 6 dB (RMS {rms12:0.00} dBFS, peak {20 * Math.Log10(at12.Max(Math.Abs)):0.00} dBFS)");
        }
        // Hashes recorded from v1.5.0 (73a7bd8). A change here invalidates every saved Pink line standard.
        Check(new (double Level, string Hash)[] {
                (-60, "4F637D68E51F994D926D13E5ADACA3C3BCB762D923B92EBEB1E4AC67D287ACC9"),
                (-30, "B2729208944E366DD0E08861B37414FD2D58CDB6C2FDB01326D4B5B253D8AE3E"),
                (-24, "E8369AFC1456C5AB3170793D1F167A7EB056A993A35AE7F31EB139929E7FAE11"),
                (-18, "205244C09D38D525AD9BCEBEEBE328B51471CCC2CA34C2099DBFDE1C81D2F393") }
            .All(x => PinkRubBuzzStimulus.Hash(settings with { LevelRmsDbFs = x.Level }) == x.Hash),
            "stimulus hashes at −60/−30/−24/−18 dBFS unchanged from v1.5.0 (saved line standards stay valid)");
        AssetChecks();
        Reject(() => new PinkRubBuzzSettings(Repeats: 1).Validate(), "single lượt rejected (needs repeats)");
        int n = 1; while (n < s44.Length) n <<= 1;
        var spectrum = new Complex[n]; for (int i = 0; i < s44.Length; i++) spectrum[i] = s44[i];
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        double Band(double lo, double hi) { double s = 0; for (int k = (int)(lo * n / 44100.0); k < (int)(hi * n / 44100.0); k++) s += spectrum[k].Magnitude * spectrum[k].Magnitude; return s; }
        double stopband = 10 * Math.Log10(Band(1000, 20000) / Band(25, 150));
        Check(stopband < -100, $"stimulus has no energy above 1 kHz ({stopband:0} dBr ≤ −100): HF at the mic is produced by the speaker");

        // --- Synthetic DSP -----------------------------------------------------------------------------------
        var lf = Lf(6, -34);
        var bg = Background(3, 5e-5, 7);
        var clean = Enumerable.Range(0, 3).Select(i => Capture(lf, 100 + i)).ToArray();
        var cleanRun = Run(clean, bg);
        Check(cleanRun.IsValid && cleanRun.Metrics.RepeatCoherencePct < 5, $"clean speaker (random hiss only): RC {cleanRun.Metrics.RepeatCoherencePct:0.0}% < 5%");
        var rattle = Rattle(lf, 2.0, 8e-4, 9, randomTiming: false);
        var rattleCaps = Enumerable.Range(0, 3).Select(i => Capture(lf, 200 + i, rattle)).ToArray();
        var rattleRun = Run(rattleCaps, bg);
        Check(rattleRun.IsValid && rattleRun.Metrics.RepeatCoherencePct > 50, $"deterministic rattle at bass peaks: RC {rattleRun.Metrics.RepeatCoherencePct:0.0}% > 50%");
        var drifted = rattleCaps.Select((c, i) => i == 1 ? Resample(c, 1.0001) : c).ToArray();
        var driftRun = Run(drifted, bg);
        Check(driftRun.IsValid && driftRun.Metrics.RepeatCoherencePct > 40, $"100 ppm DAC/ADC clock drift tolerated: RC {driftRun.Metrics.RepeatCoherencePct:0.0}% > 40%");
        // Room transients: an 8 kHz click (inside the 4–12 kHz band) in a single lượt.
        void Click(float[] x, int at) { for (int j = 0; j < 400; j++) x[at + j] += (float)(0.02 * Math.Sin(2 * Math.PI * 8000 * j / Rate) * Math.Exp(-j / 80.0)); }
        var withClick = rattleCaps.Select(c => c.ToArray()).ToArray(); Click(withClick[2], Rate * 3);
        var clickRun = Run(withClick, bg);
        Check(clickRun.IsValid && clickRun.Metrics.KeptPct < 100 && clickRun.Metrics.RepeatCoherencePct > 50, $"foreign click in one lượt masked (kept {clickRun.Metrics.KeptPct:0.0}%), rattle still {clickRun.Metrics.RepeatCoherencePct:0}%");
        var cleanClick = clean.Select(c => c.ToArray()).ToArray(); Click(cleanClick[1], Rate * 2); Click(cleanClick[0], Rate * 4);
        var cleanClickRun = Run(cleanClick, bg);
        Check(cleanClickRun.Metrics.KeptPct < 100 && cleanClickRun.Metrics.RepeatCoherencePct < 10, $"foreign clicks do not create a false rattle on a clean speaker (RC {cleanClickRun.Metrics.RepeatCoherencePct:0.0}%)");
        var chaotic = Enumerable.Range(0, 3).Select(i => Capture(lf, 300 + i, randomRattle: Rattle(lf, 0, 2e-3, 400 + i, randomTiming: true))).ToArray();
        var chaoticRun = Run(chaotic, bg);
        Check(chaoticRun.IsValid && chaoticRun.Metrics.KeptPct < 75 && chaoticRun.Metrics.HfAllDbFs > cleanRun.Metrics.HfAllDbFs + 3,
            $"non-repeating (chaotic) rattle: {100 - chaoticRun.Metrics.KeptPct:0}% non-repeating time, all-frame HF +{chaoticRun.Metrics.HfAllDbFs - cleanRun.Metrics.HfAllDbFs:0.0} dB");
        Check(PinkRubBuzzStandard.Compare(Run(chaotic, bg), null, new()).Verdict == "INVALID", "chaotic HF without a line standard → INVALID (cannot tell from a noisy room)");
        Check(!Run(clean, Background(3, 5e-5, 8, gated: true)).IsValid, "Windows-processed (decaying to zero) background → INVALID");
        Check(!Run(clean, Background(3, 1e-7, 8)).IsValid, "near-digital-silence background → INVALID");
        var louder = clean.Select((c, i) => i == 2 ? c.Select(v => v * 1.2f).ToArray() : c).ToArray();
        Check(!Run(louder, bg).IsValid, "bass differs > 1 dB between lượt → INVALID");
        Check(!Run(clean.Take(1).ToArray(), bg).IsValid, "one lượt only → INVALID");
        var clipped = clean.Select(c => c.ToArray()).ToArray(); clipped[0][Rate] = .99f;
        Check(!Run(clipped, bg).IsValid, "clipping → INVALID");

        // --- Line standard + allowed ratios ----------------------------------------------------------------
        string standards = Path.Combine(output, "standards");
        var limits = new PinkRubBuzzLimits();
        var std = PinkRubBuzzStandard.SaveAsStandard(Run(clean, bg), limits, standards, "check");
        Check(std.Verdict == "PASS" && std.SavedAsStandard && File.Exists(PinkRubBuzzStandard.PathFor(standards, std.ContextKey)), "valid good-unit run saved as line standard: " + std.Message);
        var loaded = PinkRubBuzzStandard.Load(standards, std.ContextKey)!;
        var clean2 = Run(Enumerable.Range(0, 3).Select(i => Capture(lf, 500 + i)).ToArray(), bg);
        Check(PinkRubBuzzStandard.Compare(clean2, loaded, limits).Verdict == "PASS", "another clean unit vs standard → PASS");
        var bad = PinkRubBuzzStandard.Compare(Run(rattleCaps, bg), loaded, limits);
        Check(bad.Verdict == "FAIL" && bad.DeltaRepeatPct > limits.RepeatMarginPct, $"rattle vs standard → FAIL (+{bad.DeltaRepeatPct:0} points > +{limits.RepeatMarginPct})");
        var chaoticVsStd = PinkRubBuzzStandard.Compare(Run(chaotic, bg), loaded, limits);
        Check(chaoticVsStd.Verdict == "FAIL" && chaoticVsStd.DeltaHfDb > limits.HfMarginDb, $"chaotic rattle vs standard → FAIL by all-frame HF (+{chaoticVsStd.DeltaHfDb:0.0} dB > +{limits.HfMarginDb})");
        var sparseNoise = clean.Select(c => c.ToArray()).ToArray(); Click(sparseNoise[0], Rate * 2); Click(sparseNoise[1], Rate * 4);
        Check(PinkRubBuzzStandard.Compare(Run(sparseNoise, bg), loaded, limits).Verdict == "PASS", "clean unit with a few room clicks vs standard → still PASS");
        Check(PinkRubBuzzStandard.Compare(Run(rattleCaps, bg), loaded, limits with { RepeatMarginPct = 100, HfMarginDb = 30 }).Verdict == "PASS", "allowed ratio is honoured (very loose tolerance → PASS)");
        double dr = PinkRubBuzzStandard.Compare(Run(rattleCaps, bg), loaded, limits).DeltaRepeatPct!.Value;
        Check(PinkRubBuzzStandard.Compare(Run(rattleCaps, bg), loaded, limits with { RepeatMarginPct = dr + 1, HfMarginDb = 30, ReviewFraction = .5 }).Verdict == "REVIEW", "review band near the limit → REVIEW");
        var moved = Enumerable.Range(0, 3).Select(i => Capture(lf.Select(v => v * 2f).ToArray(), 600 + i)).ToArray();
        Check(PinkRubBuzzStandard.Compare(Run(moved, bg), loaded, limits).Verdict == "INVALID", "bass at mic 6 dB off the standard (mic/speaker moved) → INVALID");
        var noisyRoom = Run(clean, Background(3, 1e-3, 9));
        Check(PinkRubBuzzStandard.Compare(noisyRoom, loaded, limits).Verdict == "INVALID", "room HF noise above the standard's → INVALID (re-measure)");
        Check(PinkRubBuzzStandard.Compare(Run(clean, bg), null, limits).Verdict == "PASS" && PinkRubBuzzStandard.Compare(Run(rattleCaps, bg), null, limits).Verdict == "FAIL",
            "no standard: absolute thresholds PASS ≤ 15% / FAIL ≥ 30%");
        Check(PinkRubBuzzStandard.Compare(Run(clean, bg), null, limits with { MinSensitivityDb = 90 }).Verdict == "INVALID", "insufficient sensitivity → INVALID");
        Check(PinkRubBuzzStandard.SaveAsStandard(Run(clean, Background(3, 5e-5, 8, gated: true)), limits, Path.Combine(output, "no-standard"), "x").Verdict == "INVALID"
            && !Directory.Exists(Path.Combine(output, "no-standard")), "invalid run is never saved as standard");
        Reject(() => new PinkRubBuzzLimits(RepeatMarginPct: 0).Validate(), "invalid tolerance rejected");
        Reject(() => new PinkRubBuzzLimits(AbsolutePassPct: 40, AbsoluteFailPct: 30).Validate(), "PASS threshold above FAIL rejected");
        var json = System.Text.Json.JsonSerializer.Deserialize<TestConfig>("""{"id":"IO005","RubBuzzMode":"PinkNoise","RubBuzzPinkLimits":{"HfMarginDb":6,"P95MarginDb":6,"EventsMarginPerSecond":2}}""")!;
        Check(json.PinkLimits.HfMarginDb == 6 && json.PinkLimits.RepeatMarginPct == 15 && json.PinkSettings.Repeats == 3, "v1.2 config JSON still loads (old margins ignored, new defaults)");

        // --- Real UMIK-1 captures (Claude live re-measure 2026-11-06) ----------------------------------------
        if (claudeArtifact != null)
        {
            string a = Path.GetFullPath(claudeArtifact);
            PinkRubBuzzMetrics Real(string folder, string bgName, params string[] names)
                => PinkRubBuzzAnalysis.Analyze(names.Select(f => ReadWav(Path.Combine(a, folder, f))).ToArray(), ReadWav(Path.Combine(a, folder, bgName)), Rate, settings);
            var s1 = Real("live", "bg-excl.wav", "cap-bl-m18-1.wav", "cap-bl-m18-2.wav", "cap-bl-m18-3.wav");
            Check(s1.IsValid && Math.Abs(s1.RepeatCoherencePct - 60) < 8, $"real rattling speaker −18 dBFS (session 1): RC {s1.RepeatCoherencePct:0.0}% ≈ 60% (Python prototype)");
            var s2 = Real("live-tol", "bg-excl.wav", "cap-a18-1.wav", "cap-a18-2.wav", "cap-a18-3.wav");
            Check(s2.IsValid && Math.Abs(s2.RepeatCoherencePct - 31.7) < 8 && s2.KeptPct < 95, $"real speaker −18 dBFS with room transients (session 2): RC {s2.RepeatCoherencePct:0.0}% ≈ 31.7%, kept {s2.KeptPct:0}%");
            var s3 = Real("live-tol", "bg-excl.wav", "cap-a30-1.wav", "cap-a30-2.wav", "cap-a30-3.wav");
            Check(s3.IsValid && s3.RepeatCoherencePct < 10, $"real speaker −30 dBFS (below rattle threshold): RC {s3.RepeatCoherencePct:0.0}% < 10%");
            Check(Math.Round(s1.RepeatCoherencePct, 1) == 59.5 && Math.Round(s2.RepeatCoherencePct, 1) == 31.6 && Math.Round(s3.RepeatCoherencePct, 1) == 1.8,
                "real pink regression unchanged: 59.5% / 31.6% / 1.8%");
            var verdict = PinkRubBuzzStandard.Compare(new PinkRubBuzzRun(settings, new string('B', 64), "real", Rate, s1), null, limits);
            Check(verdict.Verdict == "FAIL", "real rattling speaker without standard → FAIL (matches the operator's ear)");
            Check(!Real("live", "bg-shared.wav", "cap-bl-m18-1.wav", "cap-bl-m18-2.wav", "cap-bl-m18-3.wav").IsValid, "same captures with Windows-shared (gated) background → INVALID");
            File.WriteAllText(Path.Combine(output, "real-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { session1 = s1 with { Envelope = [] }, session2 = s2 with { Envelope = [] }, minus30 = s3 with { Envelope = [] } },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        if (codexArtifact != null)
        {
            string c = Path.Combine(Path.GetFullPath(codexArtifact), "target-minus18");
            var v12 = PinkRubBuzzAnalysis.Analyze(new[] { "capture-01.wav", "capture-02.wav", "capture-03.wav" }.Select(f => ReadWav(Path.Combine(c, f))).ToArray(),
                ReadWav(Path.Combine(c, "background.wav")), Rate, settings);
            Check(!v12.IsValid && v12.Reason.Contains("Windows"), "v1.2.1 shared-mode captures (fake −113 dBFS floor) → INVALID with Windows-processing reason");
        }

        // --- UI render --------------------------------------------------------------------------------------
        var application = new App(); application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        typeof(Application).GetField("_startupUri", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(application, null);
        var view = new RubBuzzView();
        var failRun = Run(rattleCaps, bg) with { ContextKey = std.ContextKey };
        view.Update(failRun, null, standards, limits);
        Check(failRun.Verdict == "FAIL", "view compares against the saved line standard");
        Render(view, Path.Combine(output, "pink-view-fail.png"), 1100, 980);
        var passView = new RubBuzzView(); passView.Update(Run(clean, bg) with { ContextKey = new string('C', 64) }, null, standards, limits);
        Render(passView, Path.Combine(output, "pink-view-no-standard.png"), 1100, 980);
        Check(File.Exists(Path.Combine(output, "pink-view-fail.png")), "pink view rendered");
        ChirpChecks(output, chirpArtifact);
        MergeChecks(output, failRun, bg);
        BackgroundAllowanceChecks(output, failRun, bg);
        ImpulsiveChecks(output);
        BroadbandPinkChecks(output);
        RubBuzzDiagnosisChecks.Run(Check, output);
        ManualDiagnosticsChecks(output);
    }

    // --- v1.4 chirp rattle ------------------------------------------------------------------------------------
    static ChirpRubBuzzRun ChirpRun(float[][] caps, float[] bg, ChirpRubBuzzSettings s)
        => new(s, new string('D', 64), "synthetic chirp", Rate, ChirpRubBuzzAnalysis.Analyze(caps, bg, Rate, s)) { Captures = caps, Background = bg };
    /// <summary>Speaker model: flat gain + random hiss; optional buzz = 4–12 kHz bursts at every waveform peak while the
    /// chirp is inside [lo, hi] Hz (identical on every lượt); optional foreign click in one lượt.</summary>
    static float[] ChirpCapture(ChirpRubBuzzSettings s, int seed, (double Lo, double Hi, double Amp)? buzz = null, int? clickAtSample = null)
    {
        var unit = ChirpRubBuzzStimulus.Unit(s, Rate); double peak = Math.Pow(10, s.PeakDbFs / 20) * 0.5, k = Math.Log(s.F2Hz / s.F1Hz);
        var hiss = HighPass(Noise(unit.Length, 9e-5, seed), 1000); var burst = HighPass(Noise(Rate / 500, 1, 777), 4000);
        var x = new float[unit.Length + Rate];
        for (int i = 0; i < unit.Length; i++) x[i + Rate / 2] = (float)(peak * unit[i]) + hiss[i];
        if (buzz is { } b)
            for (int i = 1; i < unit.Length - burst.Length; i++)
            {
                double f = s.F1Hz * Math.Exp(i / (double)Rate / s.DurationSeconds * k);
                if (f < b.Lo || f > b.Hi || !(unit[i] > 0.98 && unit[i - 1] <= 0.98)) continue;
                for (int j = 0; j < burst.Length; j++) x[i + Rate / 2 + j] += (float)(b.Amp * burst[j]);
            }
        if (clickAtSample is int c) for (int j = 0; j < 400; j++) x[c + j] += (float)(0.02 * Math.Sin(2 * Math.PI * 8000 * j / Rate) * Math.Exp(-j / 80.0));
        var floor = Noise(x.Length, 3e-5, seed + 5000);
        for (int i = 0; i < x.Length; i++) x[i] += floor[i];
        return x;
    }
    static void ChirpChecks(string output, string? chirpArtifact)
    {
        var s = new ChirpRubBuzzSettings();
        var stim = ChirpRubBuzzStimulus.Generate(s, 44100);
        Check(stim.Length == 3 * 44100 && Math.Abs(20 * Math.Log10(stim.Max(Math.Abs)) - s.PeakDbFs) < 0.1 && stim[0] == 0 && stim[^1] == 0, "chirp stimulus: 3 s, −12 dBFS peak, faded ends");
        Check(ChirpRubBuzzStimulus.Hash(s) != ChirpRubBuzzStimulus.Hash(s with { F2Hz = 3000 }), "chirp hash changes with settings");
        Reject(() => new ChirpRubBuzzSettings(PeakDbFs: -3).Validate(), "chirp above −6 dBFS peak rejected");
        Reject(() => new ChirpRubBuzzSettings(Repeats: 1).Validate(), "single chirp rejected (needs repeats)");
        var bg = Background(3, 3e-5, 11);
        var clean = new[] { ChirpCapture(s, 1), ChirpCapture(s, 2) };
        var cleanRun = ChirpRun(clean, bg, s);
        Check(cleanRun.IsValid && cleanRun.Metrics.PeakBumpDb < 4, $"clean speaker chirp: peak bump {cleanRun.Metrics.PeakBumpDb:0.0} dB < 4");
        var buzzed = new[] { ChirpCapture(s, 3, (220, 250, 3e-3)), ChirpCapture(s, 4, (220, 250, 3e-3)) };
        var buzzRun = ChirpRun(buzzed, bg, s);
        Check(buzzRun.IsValid && buzzRun.Metrics.PeakBumpDb > 10 && buzzRun.Metrics.PeakBumpHz is > 200 and < 270,
            $"buzz at 220–250 Hz: peak bump {buzzRun.Metrics.PeakBumpDb:0.0} dB at {buzzRun.Metrics.PeakBumpHz:0} Hz");
        double tClick = s.DurationSeconds * Math.Log(400 / s.F1Hz) / Math.Log(s.F2Hz / s.F1Hz);
        var clicked = new[] { ChirpCapture(s, 5, clickAtSample: Rate / 2 + (int)(tClick * Rate)), ChirpCapture(s, 6) };
        var clickRun = ChirpRun(clicked, bg, s);
        Check(clickRun.IsValid && clickRun.Metrics.PeakBumpDb < 4, $"room click in ONE lượt (at ~400 Hz) ignored: peak bump {clickRun.Metrics.PeakBumpDb:0.0} dB");
        Check(!ChirpRun(clean, Background(3, 3e-5, 12, gated: true), s).IsValid, "chirp with Windows-processed background → INVALID");
        Check(!ChirpRun(new[] { clean[0], clean[1].Select(v => v * 1.4f).ToArray() }, bg, s).IsValid, "chirp level differs between lượt → INVALID");
        var limits = new ChirpRubBuzzLimits();
        Check(ChirpRubBuzzStandard.Compare(ChirpRun(clean, bg, s), null, limits).Verdict == "PASS"
            && ChirpRubBuzzStandard.Compare(ChirpRun(buzzed, bg, s), null, limits).Verdict == "FAIL", "no chirp standard: absolute PASS ≤ 6 dB / FAIL ≥ 10 dB");
        string dir = Path.Combine(output, "chirp-standards");
        var std = ChirpRubBuzzStandard.SaveAsStandard(ChirpRun(clean, bg, s), limits, dir, "check");
        var loaded = ChirpRubBuzzStandard.Load(dir, std.ContextKey)!;
        Check(std.SavedAsStandard && loaded.Metrics.BumpDb.Length > 0, "good-unit chirp saved as line standard");
        Check(ChirpRubBuzzStandard.Compare(ChirpRun(new[] { ChirpCapture(s, 7), ChirpCapture(s, 8) }, bg, s), loaded, limits).Verdict == "PASS", "another clean unit vs chirp standard → PASS");
        var badVsStd = ChirpRubBuzzStandard.Compare(ChirpRun(buzzed, bg, s), loaded, limits);
        Check(badVsStd.Verdict == "FAIL" && badVsStd.DeltaBumpHz is > 200 and < 270, $"buzz vs chirp standard → FAIL (+{badVsStd.DeltaBumpDb:0.0} dB at {badVsStd.DeltaBumpHz:0} Hz)");
        Check(ChirpRubBuzzStandard.Compare(ChirpRun(buzzed, bg, s), loaded, limits with { BumpMarginDb = 40 }).Verdict == "PASS", "chirp allowed margin honoured");
        Reject(() => new ChirpRubBuzzLimits(AbsolutePassDb: 12, AbsoluteFailDb: 10).Validate(), "chirp PASS threshold above FAIL rejected");
        // Orchestration (Pink + Chirp in one acquisition)
        var both = new TestConfig { id = "IO005", TestRubBuzz = true, RubBuzzMode = "PinkNoiseChirp" };
        var plan = AcousticRubBuzzConfiguredTest.Plan(both, new AdditionalTestContext(44100, 48000, 1, "x"));
        Check(plan.Steps.Count == 6 && plan.Steps.Take(3).All(p => p.DurationSeconds == 6) && plan.Steps.Skip(3).All(p => p.DurationSeconds == 3), "Pink + Chirp plan: 3 pink lượt then 3 chirp lượt");
        Check(AcousticRubBuzzConfiguredTest.Analyze(both, new(Rate, bg, new[] { clean[0] }), new AdditionalTestContext(44100, 48000)).Status == "INVALID", "incomplete Pink + Chirp acquisition → INVALID");
        // "Tìm line chuẩn" with both signals: save both standards or neither (no half standard from a bad acquisition).
        var chirp3 = new[] { ChirpCapture(s, 21), ChirpCapture(s, 22), ChirpCapture(s, 23) };
        bool NoFiles(string d) => !Directory.Exists(d) || !Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any();
        string halfDir = Path.Combine(output, "both-standards-half");
        var deadPink = Enumerable.Range(0, 3).Select(_ => new float[6 * Rate]).ToArray();
        var half = AcousticRubBuzzConfiguredTest.Analyze(both, new(Rate, bg, deadPink.Concat(chirp3).ToArray()),
            new AdditionalTestContext(44100, Rate) { ReferenceDirectory = halfDir, SaveAsStandard = true });
        Check(half.Status == "INVALID" && half.ChirpRubBuzz is { IsValid: true, SavedAsStandard: false } && NoFiles(halfDir),
            "line standard Pink + Chirp: Pink unusable → valid Chirp NOT saved either (both or neither)");
        string fullDir = Path.Combine(output, "both-standards-full");
        var pinkLf = Lf(6, -34);
        var goodPink = Enumerable.Range(0, 3).Select(i => Capture(pinkLf, 600 + i)).ToArray();
        var full = AcousticRubBuzzConfiguredTest.Analyze(both, new(Rate, bg, goodPink.Concat(chirp3).ToArray()),
            new AdditionalTestContext(44100, Rate) { ReferenceDirectory = fullDir, SaveAsStandard = true });
        Check(full.Status == "PASS" && full.PinkRubBuzz is { SavedAsStandard: true } && full.ChirpRubBuzz is { SavedAsStandard: true }
            && PinkRubBuzzStandard.Load(fullDir, full.PinkRubBuzz!.ContextKey) != null && ChirpRubBuzzStandard.Load(fullDir, full.ChirpRubBuzz!.ContextKey) != null,
            $"line standard Pink + Chirp: both usable → both saved ({full.Status}: {full.Reason.Replace(Environment.NewLine, " | ")})");
        Check(AcousticRubBuzzConfiguredTest.Combine("PASS", "FAIL") == "FAIL" && AcousticRubBuzzConfiguredTest.Combine("INVALID", "PASS") == "INVALID"
            && AcousticRubBuzzConfiguredTest.Combine("REVIEW", "PASS") == "REVIEW", "combined verdict FAIL > INVALID > REVIEW > PASS");
        var legacy = System.Text.Json.JsonSerializer.Deserialize<TestConfig>("""{"id":"IO005","RubBuzzMode":"PinkNoise"}""")!;
        Check(legacy.UsesPinkRubBuzz && !legacy.UsesChirpRubBuzz && legacy.UsesAcousticRubBuzz, "v1.3 PinkNoise config unchanged (no chirp)");
        // Real UMIK-1 captures from 2026-11-06 (operator heard the buzz during the chirp)
        if (chirpArtifact != null)
        {
            string a = Path.GetFullPath(chirpArtifact);
            ChirpRubBuzzMetrics Real(string folder, ChirpRubBuzzSettings rs)
                => ChirpRubBuzzAnalysis.Analyze(new[] { "capture-1.wav", "capture-2.wav" }.Select(f => ReadWav(Path.Combine(a, folder, f))).ToArray(),
                    ReadWav(Path.Combine(a, folder, "background.wav")), Rate, rs);
            var c10 = Real("chirp-10s", new ChirpRubBuzzSettings(20, 2000, 10, -12, 2));
            Check(c10.IsValid && c10.PeakBumpDb > 18 && c10.PeakBumpHz is > 210 and < 260, $"real chirp 10 s: peak bump {c10.PeakBumpDb:0.0} dB at {c10.PeakBumpHz:0} Hz (prototype 28.1 dB @232)");
            var c3 = Real("chirp-3s", new ChirpRubBuzzSettings(20, 2000, 3, -12, 2));
            Check(c3.IsValid && c3.PeakBumpDb > 15 && c3.PeakBumpHz is > 210 and < 270, $"real chirp 3 s (EOL-like): peak bump {c3.PeakBumpDb:0.0} dB at {c3.PeakBumpHz:0} Hz (prototype 22.8 dB @246)");
            var sweep = new ChirpRubBuzzSettings(20, 20000, 524288 / 44100.0, -15, 2);
            var quiet = Real("sweep-live", sweep);
            Check(quiet.IsValid && quiet.PeakBumpDb < 6, $"real 512K sweep below rattle threshold: peak bump {quiet.PeakBumpDb:0.0} dB < 6 (room event in one lượt ignored)");
            var louder = Real("sweep-live-2-louder", sweep);
            Check(louder.IsValid && louder.PeakBumpHz is > 210 and < 260 && louder.PeakBumpDb > quiet.PeakBumpDb, $"real 512K sweep after volume up: buzz {louder.PeakBumpDb:0.0} dB at {louder.PeakBumpHz:0} Hz");
            Check(Math.Round(c10.PeakBumpDb, 1) == 28.1 && Math.Round(c10.PeakBumpHz) == 232 && Math.Round(c3.PeakBumpDb, 1) == 22.8 && Math.Round(c3.PeakBumpHz) == 246
                && Math.Round(quiet.PeakBumpDb, 1) == 0 && Math.Round(louder.PeakBumpDb, 1) == 7.8 && Math.Round(louder.PeakBumpHz) == 232,
                "real chirp/sweep regression unchanged: 28.1@232 / 22.8@246 / 0.0 / 7.8@232");
            Check(ChirpRubBuzzStandard.Compare(new ChirpRubBuzzRun(new ChirpRubBuzzSettings(20, 2000, 3, -12, 2), new string('E', 64), "real", Rate, c3), null, limits).Verdict == "FAIL",
                "real rattling speaker, 3 s chirp, no standard → FAIL (matches the operator's ear)");
            File.WriteAllText(Path.Combine(output, "chirp-real-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { c10, c3, quiet, louder }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        var view = new RubBuzzView();
        view.Update(null, ChirpRun(buzzed, bg, s) with { ContextKey = std.ContextKey }, dir, chirpLimits: limits);
        Render(view, Path.Combine(output, "chirp-view-fail.png"), 1100, 900);
        Check(File.Exists(Path.Combine(output, "chirp-view-fail.png")), "chirp view rendered");
    }
    static void Render(FrameworkElement view, string path, int width, int height)
    {
        // Keep the complete tall tab visible in a scaled host. WPF culls controls below the HWND viewport,
        // so rendering a tall control from an ordinary screen-height window loses its details/footer.
        view.Width = width; view.Height = height;
        var viewport = new System.Windows.Controls.Viewbox { Child = view, Stretch = Stretch.Uniform };
        var host = new Window { Content = viewport, Width = 850, Height = 850, Left = -4000, Top = 0,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = new SolidColorBrush(Color.FromRgb(18, 18, 20)) };
        // Match MeasurementDetailsWindow after a view is detached from its themed parent for a full-height render.
        host.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/SoncaAudioInspector;component/src/Themes/ImpulseTheme.xaml", UriKind.Relative) });
        host.Show(); host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var surface = (view as System.Windows.Controls.UserControl)?.Content as FrameworkElement ?? view;
        // Wrapped diagnosis rows can exceed the requested minimum height. Include the entire footer in the PNG.
        double contentBottom = surface is System.Windows.Controls.Panel contentPanel
            ? contentPanel.Children.OfType<FrameworkElement>().Max(child => System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(child).Bottom)
            : surface.DesiredSize.Height;
        height = Math.Max(height, (int)Math.Ceiling(contentBottom + 24));
        view.Height = height; host.UpdateLayout();
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (surface is System.Windows.Controls.Panel panel)
        {
            panel.Background = host.Background;
            File.WriteAllLines(path + ".layout.txt", panel.Children.OfType<FrameworkElement>().Select(child =>
                $"{child.GetType().Name} {child.Name}: {child.ActualWidth}×{child.ActualHeight}, desired={child.DesiredSize}, slot={System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(child)}, visible={child.IsVisible}"));
        }
        host.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream);
        viewport.Child = null;
        host.Content = null; host.Close();
    }
}
