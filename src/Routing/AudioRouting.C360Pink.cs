using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    // Outside the response-reference/repeat wrappers: their PASS may never hide a pink REVIEW.
    private sealed class C360PinkBench(IC360ElectronicBench inner,
        Func<C360ElectronicResult, C360ElectronicSetup, CancellationToken, Task<C360ElectronicResult>> analyze) : IC360ElectronicBench
    {
        public string DeviceIdentity => inner.DeviceIdentity;
        public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
        public Task WriteAsync(ushort register, ushort value, CancellationToken token) => inner.WriteAsync(register, value, token);
        public void StopAudio() => inner.StopAudio();
        public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        {
            var result = await inner.CaptureAsync(stage, setup, token); token.ThrowIfCancellationRequested();
            return C360ModelDefinition.IsAcoustic(stage) && setup.TestConfiguration?.UsesAcousticRubBuzz == true
                && setup.TestConfiguration.IsRubBuzzEnabled ? await analyze(result, setup, token) : result;
        }
    }
    private async Task<C360ElectronicResult> CaptureC360PinkRubBuzzAsync(C360ElectronicResult result, C360ElectronicSetup setup, CancellationToken token, bool saveAsStandard, bool saveIfMissing = false)
    {
        var config = setup.TestConfiguration!;
        if (result.Measurement == null || result.Measurement.IsClipped || result.Measurement.Validity == "INVALID")
            return result with { Status = "INVALID", AdditionalResults = [new("Rè Pink", "INVALID", "Bản thu mic chính chưa hợp lệ; chưa phát pink.")] };
        using var devices = new MMDeviceEnumerator();
        using var output = devices.GetDevice(setup.AnalogPlaybackId);
        using var input = devices.GetDevice(setup.LineOutCaptureId);
        using var levels = new WindowsAudioLevels(output, input, config);
        int oldRate = _audioEngine.PlaybackSampleRate;
        int? oldOut = _audioEngine.PlaybackChannel, oldIn = _audioEngine.RecordingChannel;
        try
        {
            _audioEngine.PlaybackSampleRate = setup.SampleRate;
            _audioEngine.PlaybackChannel = setup.AnalogPlaybackChannel; _audioEngine.RecordingChannel = setup.CaptureChannel;
            string Context() => PinkRubBuzzContext(config, output, input) + JsonSerializer.Serialize(new
            { mixer = SoncaMixerControls.Fingerprint(SoncaBleManager.Instance.MixerState, SoncaBleManager.Instance.FirmwareInfoPayload, 0) });
            string acquisition = Context();
            var context = new AdditionalTestContext(setup.SampleRate, Math.Max(32000, result.Measurement.SampleRate), 1,
                $"C360 · {config.id} · {output.FriendlyName} / {setup.AnalogPlaybackChannel + 1} → MIC {input.FriendlyName} / {setup.CaptureChannel + 1}")
                { AcquisitionKey = acquisition, ReferenceDirectory = PinkRubBuzzDirectory, SaveAsStandard = saveAsStandard, SaveIfMissing = saveIfMissing && !saveAsStandard };
            // IO008 (owner 2026-11-19): Rè needs no line standard, only the absolute Pink < 30 % / Chirp thresholds.
            if (C360ModelDefinition.Stage(config) == C360ElectronicStage.AcousticRubBuzz)
                context = context with { AbsoluteOnly = true, SaveAsStandard = false, SaveIfMissing = false };
            var plan = AcousticRubBuzzConfiguredTest.Plan(config, context);
            // Unprocessed capture path: Windows capture effects gate the floor and HF (v1.2 failure mode).
            _audioEngine.UseExclusiveCapture = true;
            // Same station allowance and noisy-floor dialog as the manual Rè tab.
            var (background, accepted) = await RubBuzzManualCapture.GateBackgroundAsync(plan.BackgroundSeconds, async (seconds, ct) =>
            {
                var bg = await _audioEngine.CaptureSilenceAsync(input, null, seconds, ct);
                return new RubBuzzCapturedAudio(bg.DutSamples, bg.DutSampleRate);
            }, LoadRubBuzzBackgroundAllowance, AskRubBuzzBackgroundAsync,
                () => { if (Context() != acquisition) throw new InvalidOperationException("Gain/DSP/route đổi trước lượt pink; đo lại."); }, token);
            var captures = new List<float[]>();
            foreach (var step in plan.Steps)
            {
                token.ThrowIfCancellationRequested();
                if (Context() != acquisition) throw new InvalidOperationException("Gain/DSP/route đổi trước lượt pink; đo lại.");
                captures.Add(await _audioEngine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, step.DurationSeconds,
                    cancellationToken: token, recordingChannel: setup.CaptureChannel, customSweepSamples: step.Samples));
                token.ThrowIfCancellationRequested();
                if (Context() != acquisition || _audioEngine.RecordingSampleRate != background.SampleRate)
                    throw new InvalidOperationException("Gain/DSP/route/rate đổi trong lượt pink; không kết luận.");
            }
            var pink = await Task.Run(() => AcousticRubBuzzConfiguredTest.Analyze(config,
                new(background.SampleRate, background.Samples, captures, context.RouteLabel) { Background = accepted }, context), token);
            token.ThrowIfCancellationRequested();
            return result with { Status = result.Status == "PASS" ? pink.Status : result.Status,
                Summary = result.Summary + Environment.NewLine + pink.Display, AdditionalResults = [pink] };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string reason = ex.Message + (RubBuzzFailureGuide.Explain(ex) is { } help ? " · " + help.Compact : "");
            return result with { Status = "INVALID", Summary = result.Summary + "\nPink Rè: " + reason,
                AdditionalResults = [new("Rè Pink", "INVALID", reason)] };
        }
        finally { _audioEngine.UseExclusiveCapture = false; _audioEngine.StopAllAudio(); _audioEngine.PlaybackSampleRate = oldRate; _audioEngine.PlaybackChannel = oldOut; _audioEngine.RecordingChannel = oldIn; }
    }
}
