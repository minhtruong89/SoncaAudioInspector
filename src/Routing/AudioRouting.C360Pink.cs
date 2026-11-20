using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

/// <summary>Owner 2026-11-19 (A7 / §7.5c): keeps IO008 Pink/Chirp summary and attempts consistent with the final verdict,
/// never logging "1/1 lượt đạt" from the sweep pre-check when Pink/Chirp did not run or was INVALID/FAIL.</summary>
public static class C360PinkSummary
{
    private static readonly Regex SweepVotePrefix = new(@"^\d+/\d+\s+lượt đạt[^.]*\.\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static C360ElectronicResult Combine(C360ElectronicResult sweepResult, string finalStatus, string rubBuzzDetail, ConfiguredTestResult additionalResult)
    {
        string precheck = SweepVotePrefix.Replace(sweepResult.Summary ?? "", "").Trim();
        string header = finalStatus switch
        {
            "PASS" => "ĐẠT (Rè · Pink + Chirp).",
            "FAIL" => "KHÔNG ĐẠT (Rè · Pink + Chirp).",
            "REVIEW" => "CẦN DUYỆT (Rè · Pink + Chirp).",
            _ => "ĐO KHÔNG HỢP LỆ (Rè chưa đo xong / chưa hợp lệ)."
        };
        string summary = string.IsNullOrEmpty(precheck)
            ? header + Environment.NewLine + rubBuzzDetail
            : $"{header} {precheck}{Environment.NewLine}{rubBuzzDetail}";
        var attempts = sweepResult.Attempts.Count == 0
            ? sweepResult.Attempts
            : sweepResult.Attempts.Select((a, i) => i == sweepResult.Attempts.Count - 1
                ? a with { Status = finalStatus, Summary = summary } : a).ToArray();
        return sweepResult with { Status = finalStatus, Summary = summary, AdditionalResults = [additionalResult], Attempts = attempts };
    }
}

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
                && setup.TestConfiguration.IsRubBuzzEnabled
                && result.AdditionalResults.All(a => a.PinkRubBuzz == null && a.ChirpRubBuzz == null)
                ? await analyze(result, setup, token) : result;
        }
    }
    private async Task<C360ElectronicResult> CaptureC360PinkRubBuzzAsync(C360ElectronicResult result, C360ElectronicSetup setup, CancellationToken token, bool saveAsStandard, bool saveIfMissing = false)
    {
        // The pre-check can retry another endpoint. Use its actual route and do not open the original pair again.
        var config = setup.TestConfiguration!;
        if (result.Acquisition is { PlaybackDeviceId.Length: > 0 } actual)
            setup = setup with { AnalogPlaybackId = actual.PlaybackDeviceId, LineOutCaptureId = actual.RecordingDeviceId };
        if (result.Measurement == null || result.Measurement.IsClipped || result.Measurement.Validity == "INVALID")
            return C360PinkSummary.Combine(result, "INVALID", "Rè Pink: Bản thu mic chính chưa hợp lệ; chưa phát pink.",
                new("Rè Pink", "INVALID", "Bản thu mic chính chưa hợp lệ; chưa phát pink."));
        using var devices = new MMDeviceEnumerator();
        using var output = devices.GetDevice(setup.AnalogPlaybackId);
        using var input = devices.GetDevice(setup.LineOutCaptureId);
        using var levels = new WindowsAudioLevels(output, input, config);
        var previous = (_audioEngine.PlaybackSampleRate, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
            _audioEngine.UseExclusivePlayback, _audioEngine.UseExclusiveCapture);
        try
        {
            _audioEngine.PlaybackSampleRate = setup.SampleRate;
            _audioEngine.PlaybackChannel = setup.AnalogPlaybackChannel; _audioEngine.RecordingChannel = setup.CaptureChannel;
            _audioEngine.UseExclusivePlayback = false;
            string Context() => PinkRubBuzzContext(config, output, input) + JsonSerializer.Serialize(new
            { mixer = SoncaMixerControls.Fingerprint(SoncaBleManager.Instance.MixerState, SoncaBleManager.Instance.FirmwareInfoPayload, 0) });
            string acquisition = Context();
            var context = new AdditionalTestContext(setup.SampleRate, Math.Max(32000, result.Measurement.SampleRate), 1,
                $"C360 · {config.id} · {AudioQaRouteDetails.Format(config.PlaybackOut, output.FriendlyName, input.FriendlyName)}")
                { AcquisitionKey = acquisition, ReferenceDirectory = PinkRubBuzzDirectory, SaveAsStandard = saveAsStandard, SaveIfMissing = saveIfMissing && !saveAsStandard };
            // IO008 (owner 2026-11-19): Rè needs no line standard, only the absolute Pink < 30 % / Chirp thresholds.
            if (C360ModelDefinition.Stage(config) == C360ElectronicStage.AcousticRubBuzz)
                context = context with { AbsoluteOnly = true, SaveAsStandard = false, SaveIfMissing = false };
            var plan = AcousticRubBuzzConfiguredTest.Plan(config, context);
            // Unprocessed capture path: Windows capture effects gate the floor and HF (v1.2 failure mode).
            var pink = await WithRubBuzzSharedFallbackAsync(output, input, async exclusive =>
            {
                _audioEngine.UseExclusiveCapture = exclusive;
                var analysisContext = exclusive ? context : context with { AcquisitionKey = acquisition + ":shared" };
                var (background, accepted) = await RubBuzzManualCapture.GateBackgroundAsync(plan.BackgroundSeconds,
                    (seconds, ct) => CaptureRubBuzzBackgroundAsync(input, seconds, ct), LoadRubBuzzBackgroundAllowance, AskRubBuzzBackgroundAsync,
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
                return await Task.Run(() => AcousticRubBuzzConfiguredTest.Analyze(config,
                    new(background.SampleRate, background.Samples, captures, context.RouteLabel)
                        { Background = accepted, CaptureMode = exclusive ? "WASAPI Exclusive" : "WASAPI Shared" }, analysisContext), token);
            }, token);
            token.ThrowIfCancellationRequested();
            string finalStatus = result.Status == "PASS" ? pink.Status : result.Status;
            return C360PinkSummary.Combine(result, finalStatus, pink.Display, pink);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string reason = ex.Message + (RubBuzzFailureGuide.Explain(ex) is { } help ? " · " + help.Compact : "");
            return C360PinkSummary.Combine(result, "INVALID", "Pink Rè: " + reason, new("Rè Pink", "INVALID", reason));
        }
        finally
        {
            _audioEngine.StopAllAudio();
            (_audioEngine.PlaybackSampleRate, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
                _audioEngine.UseExclusivePlayback, _audioEngine.UseExclusiveCapture) = previous;
        }
    }
}
