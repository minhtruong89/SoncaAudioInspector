using System.Numerics;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public interface IC360PortProbeCapture
{
    Task<C360PortProbeReading> CapturePortProbeAsync(C360ElectronicSetup setup, CancellationToken token);
}

public sealed record C360PortProbeReading(double LevelDbFs, double MatchedPeakToRmsDb, string? Error = null)
{
    // Owner 2026-11-19: random peaks in the handoff were 9–13 dB, real sweeps 32+ dB.
    // The absolute correlated level also rejects quiet crosstalk and quantisation noise.
    public const double MinimumMatchedDb = 24, MinimumLevelDbFs = -55;
    public bool Heard => Error == null && LevelDbFs >= MinimumLevelDbFs && MatchedPeakToRmsDb >= MinimumMatchedDb;
    public override string ToString() => Error ?? $"{LevelDbFs:F1} dBFS · matched peak/RMS {MatchedPeakToRmsDb:F1} dB";
}

public static class C360PortProbe
{
    public static float[] Burst(int rate, double levelDbFs)
    {
        const double duration = .3, startHz = 180;
        double exponent = Math.Log(4500 / startHz) / duration, gain = Math.Pow(10, levelDbFs / 20);
        var samples = new float[(int)Math.Round(rate * duration)];
        for (int i = 0; i < samples.Length; i++)
        {
            double time = i / (double)rate;
            double edge = Math.Min(1, Math.Min(i, samples.Length - i - 1) / (.015 * rate));
            samples[i] = (float)(gain * Math.Sin(2 * Math.PI * startHz * (Math.Exp(exponent * time) - 1) / exponent)
                * .5 * (1 - Math.Cos(Math.PI * edge)));
        }
        return samples;
    }

    public static C360PortProbeReading Match(float[] stimulus, float[] captured)
    {
        if (stimulus.Length == 0 || captured.Length < stimulus.Length || captured.Any(x => !float.IsFinite(x))
            || captured.Any(x => Math.Abs(x) >= .995f))
            return new(double.NaN, double.NaN, "Bản thu thiếu dữ liệu, không hữu hạn hoặc clipping.");
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)(captured.Length + stimulus.Length));
        var input = new Complex[size];
        var template = new Complex[size];
        double energy = 0;
        for (int i = 0; i < captured.Length; i++) input[i] = captured[i];
        for (int i = 0; i < stimulus.Length; i++) { template[i] = stimulus[i]; energy += (double)stimulus[i] * stimulus[i]; }
        var fft = new SweepFourier();
        fft.Forward(input, captured.Length);
        fft.Forward(template, stimulus.Length);
        for (int i = 0; i < size; i++) input[i] *= Complex.Conjugate(template[i]);
        fft.Inverse(input);
        double peak = 0, sumSquares = 0;
        int count = captured.Length - stimulus.Length + 1;
        for (int i = 0; i < count; i++)
        {
            double value = input[i].Real;
            peak = Math.Max(peak, Math.Abs(value));
            sumSquares += value * value;
        }
        return new(20 * Math.Log10(peak / Math.Sqrt(energy * stimulus.Length)),
            20 * Math.Log10(peak / Math.Max(1e-20, Math.Sqrt(sumSquares / count))));
    }

    public static async Task<string?> RunAsync(IC360ElectronicBench bench, C360ElectronicSetup setup, C360PortSync sync,
        IReadOnlyDictionary<ushort, ushort> baseline, Action<string> log, CancellationToken token)
    {
        const string line = "LINE IN", mic = "MIC IN";
        string configuredLine = sync.EndpointFor(line), configuredMic = sync.EndpointFor(mic);
        if (sync.Probed || configuredLine.Length == 0 || configuredMic.Length == 0
            || configuredLine.Equals(configuredMic, StringComparison.OrdinalIgnoreCase)) return null;
        sync.Probed = true;
        string? detected = null;
        var probe = new C360StageActionBench(bench, async (route, ct) =>
        {
            var readings = new List<(string Endpoint, C360PortProbeReading Reading)>();
            foreach (string endpoint in new[] { configuredLine, configuredMic })
            {
                bench.StopAudio();
                var candidate = route with { AnalogPlaybackId = endpoint, LinePlaybackId = endpoint };
                C360PortProbeReading reading;
                try
                {
                    reading = bench is IC360PortProbeCapture capture ? await capture.CapturePortProbeAsync(candidate, ct)
                        : FromSweep(await bench.CaptureAsync(C360ElectronicStage.LineIn, candidate, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { reading = new(double.NaN, double.NaN, ex.Message); }
                readings.Add((endpoint, reading));
                log($"{sync.Name(endpoint)} · {reading}");
            }
            if (readings.All(r => r.Reading.Error == null) && readings.Count(r => r.Reading.Heard) == 1)
                detected = readings.Single(r => r.Reading.Heard).Endpoint;
        }) { Configuration = setup.TestConfiguration };
        var restore = new C360BleRestoreState
        {
            DeviceIdentity = bench.DeviceIdentity,
            BaselineState = baseline.ToDictionary(p => p.Key, p => p.Value)
        };
        try { await new C360ElectronicSuite().RunResumableAsync(probe, setup, restore, null, token, [C360ElectronicStage.LineIn]); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await restore.RestorePendingAsync(bench);
            token.ThrowIfCancellationRequested();
            log("Chưa chuẩn bị được phép dò LINE IN; giữ cấu hình của phiên. " + ex.Message);
            return null;
        }
        if (detected == null)
        {
            log("Chưa xác định được một cặp LINE IN duy nhất; giữ cấu hình của phiên.");
            return null;
        }
        bool moved = detected.Equals(configuredMic, StringComparison.OrdinalIgnoreCase);
        if (moved) sync.Adopt(line, detected);
        else sync.Confirm(line, detected);
        // The other pair is assigned by elimination. This does not prove that its MIC cable is connected.
        string report = $"LINE IN = {sync.Name(detected)} · MIC IN = {sync.Name(sync.EndpointFor(mic))}"
            + (moved ? " (khác config → đã tự đổi)" : " (khớp config)");
        log(report);
        return report;
    }

    internal static C360PortProbeReading FromSweep(C360ElectronicResult result) => result.Measurement is { } m
        ? new(m.SignalLevelDbFs, m.ImpulsePeakToBackgroundDb, m.IsClipped ? "Clipping" : null)
        : new(double.NegativeInfinity, double.NegativeInfinity);
}

// Runs a scoped operation after the suite verifies the requested speaker state.
internal sealed class C360StageActionBench(IC360ElectronicBench inner,
    Func<C360ElectronicSetup, CancellationToken, Task> action) : IC360ElectronicBench, IC360StageSettings
{
    public TestConfig ConfigurationFor(C360ElectronicStage stage) => Configuration!;
    public TestConfig? Configuration { get; init; }
    public string DeviceIdentity => inner.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => inner.WriteAsync(register, value, token);
    public void StopAudio() => inner.StopAudio();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        await action(setup, token);
        return new(stage, "PASS", "");
    }
}

public sealed partial class C360AudioBench : IC360PortProbeCapture
{
    public async Task<C360PortProbeReading> CapturePortProbeAsync(C360ElectronicSetup setup, CancellationToken token)
    {
        if (AudioEngine.IsWindowsMonoAudioEnabled()) throw new InvalidOperationException("Windows Mono audio đang bật.");
        using var devices = new MMDeviceEnumerator();
        using var playback = devices.GetDevice(setup.PlaybackId(C360ElectronicStage.LineIn));
        using var recording = devices.GetDevice(setup.LineOutCaptureId);
        using var levels = new WindowsAudioLevels(playback, recording, setup.TestConfiguration);
        var previous = (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel,
            engine.UseExclusivePlayback, engine.UseExclusiveCapture, engine.RecordingGain);
        try
        {
            engine.PlaybackSampleRate = setup.SampleRate;
            engine.PlaybackChannel = setup.AnalogPlaybackChannel;
            engine.RecordingChannel = setup.CaptureChannel;
            engine.UseExclusivePlayback = engine.UseExclusiveCapture = false;
            engine.RecordingGain = 1;
            double level = Math.Min(-12, setup.PlaybackLevel(C360ElectronicStage.LineIn));
            var stimulus = C360PortProbe.Burst(setup.SampleRate, level);
            var samples = await engine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, .65,
                cancellationToken: token, customSweepSamples: stimulus, recordingChannel: setup.CaptureChannel);
            int actualRate = engine.RecordingSampleRate;
            if (actualRate != setup.SampleRate) stimulus = C360PortProbe.Burst(actualRate, level);
            return await Task.Run(() => C360PortProbe.Match(stimulus, samples), token);
        }
        finally
        {
            engine.StopAllAudio();
            (engine.PlaybackSampleRate, engine.PlaybackChannel, engine.RecordingChannel,
                engine.UseExclusivePlayback, engine.UseExclusiveCapture, engine.RecordingGain) = previous;
        }
    }
}
