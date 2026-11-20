using System.Threading;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private CancellationTokenSource? _autoDiagnosticsCancellation;

    private async Task RunTrackedConfiguredTestsAsync(AutoTestCaseItem test, MMDevice playback, MMDevice recording, bool acquisitionValid)
    {
        var task = RunConfiguredTestsAsync(test, playback, recording, acquisitionValid);
        _activeMeasurementTask = task;
        try { await task; }
        finally { if (_activeMeasurementTask == task) _activeMeasurementTask = null; }
    }

    private async Task RunConfiguredTestsAsync(AutoTestCaseItem test, MMDevice playback, MMDevice recording, bool acquisitionValid)
    {
        var config = test.Config;
        if (!config.HasAdditionalTests) return;
        using var cancellation = new CancellationTokenSource();
        _autoDiagnosticsCancellation = cancellation;
        var results = new List<ConfiguredTestResult>();
        void Add(ConfiguredTestResult result)
        {
            results.Add(result); test.AdditionalResults = results.ToArray(); AppendLog("Kiểm tra bổ sung", result.Display);
        }
        try
        {
            try { config.ValidateAdditionalTests(); }
            catch (Exception ex) { Add(new("Cấu hình bổ sung", "INVALID", ex.Message)); return; }
            cancellation.Token.ThrowIfCancellationRequested();
            if (config.TestEffect)
                Add(acquisitionValid ? ConfiguredAcousticTests.Effect(_testRunner.LastSweepResult?.ImpulseEffects,
                    _testRunner.ImpulseEffectsSettings, _standardImpulseEffects)
                    : new("Effect", "INVALID", "Bản thu chính chưa hợp lệ; kiểm tra route, mức thu, clipping và nhiễu nền."));
            if (config.TestSoundLevel)
            {
                var points = new List<DiagnosisResponsePoint>();
                for (int i = 0; i < Math.Min(_freqs.Count, _dbValues.Count); i++)
                    if (TryGetComparableStandardTarget(_freqs[i], out double target))
                        points.Add(new(_freqs[i], _dbValues[i], target, config.SoundLevelToleranceDb));
                Add(ConfiguredAcousticTests.SoundLevel(points, acquisitionValid, HasComparableStandardCurve(),
                    _testRunner.NormalizeFrequencyResponseToOneKilohertz, _testRunner.LastOneKilohertzLevelDbFs,
                    _standardOneKilohertzLevelDbFs ?? double.NaN, config.SoundLevelToleranceDb,
                    config.EvaluationRange, ResponseLimitSettings.Capture(config, ParseDoubleSafe(TxtFreqTolerance?.Text ?? "", 3.0))));
            }
            if (config.TestAirLeak || config.TestLooseParticle)
            {
                var mechanical = await ConfiguredAcousticTests.RunMechanicalTestsAsync(config, acquisitionValid ? _testRunner.PlaybackFrequencyScale : 1.0,
                    async (request, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (!acquisitionValid) return new MechanicalCaptureData(null, null, 0, PrebuiltUnavailable:
                            MechanicalTestResult.Unavailable(request.Kind, "BẢN THU CHÍNH CHƯA ĐẠT", "Bản thu chính chưa hợp lệ; chưa phát tone bổ sung. Kiểm tra route và đo lại."));
                        var running = new List<ConfiguredTestResult>();
                        if (config.TestAirLeak) running.Add(new("Rò khí", "RUNNING", "Đang thu nền và phát tone…"));
                        if (config.TestLooseParticle) running.Add(new("Hạt rác", "RUNNING", "Đang thu nền và phát tone…"));
                        test.AdditionalResults = results.Concat(running).ToArray();
                        return await CaptureMechanicalSamplesAsync(request, playback, recording, token);
                    }, AppendLog, cancellation.Token);
                foreach (var result in mechanical) Add(result);
            }
            foreach (var additionalTest in AdditionalTestRegistry.GetEnabled(config))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!acquisitionValid)
                {
                    Add(new(additionalTest.Metadata.DisplayName, "INVALID", "Bản thu chính chưa hợp lệ; chưa phát tone bổ sung. Kiểm tra route và đo lại."));
                    continue;
                }
                test.AdditionalResults = results.Append(new ConfiguredTestResult(additionalTest.Metadata.DisplayName, "RUNNING", config.UsesAcousticRubBuzz ? "Đang thu nền và phát pink/chirp…" : "Đang thu nền và phát tone bước…")).ToArray();
                try
                {
                    string route = $"Out: {playback.FriendlyName} · In: {recording.FriendlyName}";
                    using var pinkLevels = config.UsesAcousticRubBuzz ? new WindowsAudioLevels(playback, recording, config) : null;
                    var context = new AdditionalTestContext(_audioEngine.PlaybackSampleRate, Math.Max(32000, _audioEngine.RecordingSampleRate), _testRunner.PlaybackFrequencyScale, route)
                    { AcquisitionKey = config.UsesAcousticRubBuzz ? PinkRubBuzzContext(config, playback, recording) : "",
                        ReferenceDirectory = config.UsesAcousticRubBuzz ? PinkRubBuzzDirectory : "" };
                    var result = await AdditionalTestRegistry.RunAsync(additionalTest, config, context, async (plan, token) =>
                    {
                        if (!_audioEngine.UseExclusivePlayback && AudioEngine.IsWindowsMonoAudioEnabled())
                            throw new InvalidOperationException("Tắt Windows Mono audio trước khi đo để giữ đúng kênh phát/thu.");
                        string contextKey = DiagnosticContextKey();
                        // Pink rattle needs an unprocessed capture path (Windows effects gate the floor and HF).
                        _audioEngine.UseExclusiveCapture = config.UsesAcousticRubBuzz;
                        RubBuzzCapturedAudio background; BackgroundAllowance? accepted = null;
                        if (config.UsesAcousticRubBuzz)
                        {
                            // Same station allowance and noisy-floor dialog as the manual Rè tab.
                            (background, accepted) = await RubBuzzManualCapture.GateBackgroundAsync(plan.BackgroundSeconds, async (seconds, ct) =>
                            {
                                var bg = await _audioEngine.CaptureSilenceAsync(recording, null, seconds, ct);
                                return new RubBuzzCapturedAudio(bg.DutSamples, bg.DutSampleRate);
                            }, LoadRubBuzzBackgroundAllowance, AskRubBuzzBackgroundAsync, () => { }, token);
                        }
                        else
                        {
                            var bg = await _audioEngine.CaptureSilenceAsync(recording, null, plan.BackgroundSeconds, token);
                            token.ThrowIfCancellationRequested();
                            var quiet = SignalDiagnostics.CheckBackground(bg.DutSamples, bg.DutSampleRate);
                            if (!quiet.IsQuiet)
                                throw new InvalidOperationException($"Nền cao/thiếu hoặc có xung ngoại lai; chưa phát tone — {quiet.Reason}. Chờ yên rồi đo lại.");
                            background = new(bg.DutSamples, bg.DutSampleRate);
                        }
                        additionalTest.Validate(config, context with { RecordingSampleRate = background.SampleRate });
                        var captures = new List<float[]>();
                        foreach (var step in plan.Steps)
                        {
                            token.ThrowIfCancellationRequested();
                            if (_audioEngine.PlaybackSampleRate != context.PlaybackSampleRate || DiagnosticContextKey() != contextKey)
                                throw new InvalidOperationException("Route/gain/định dạng phát đã đổi giữa các bước; đo lại.");
                            var captured = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep,
                                step.PhysicalFrequencyHz, step.DurationSeconds, cancellationToken: token,
                                recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: step.Samples);
                            token.ThrowIfCancellationRequested();
                            if (_audioEngine.RecordingSampleRate != background.SampleRate || DiagnosticContextKey() != contextKey)
                                throw new InvalidOperationException("Route/gain/định dạng thu đã đổi giữa các bước; đo lại.");
                            captures.Add(captured);
                        }
                        return new AdditionalTestCaptureData(background.SampleRate, background.Samples, captures, route) { Background = accepted };
                    }, cancellation.Token);
                    Add(result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Results list: the cause plus the first operator step; the Exclusive hint only for device-open failures.
                    var help = RubBuzzFailureGuide.Explain(ex);
                    bool deviceError = config.UsesAcousticRubBuzz && (ex is System.Runtime.InteropServices.COMException or NotSupportedException or System.IO.IOException
                        or UnauthorizedAccessException || AudioSessionDiagnostics.IsDeviceInUse(ex));
                    Add(new(additionalTest.Metadata.DisplayName, "INVALID", (deviceError ? "Không mở được mic Exclusive: " : "Không hoàn tất bản thu: ")
                        + GetMeasurementErrorMessage(ex) + (help != null ? " · " + help.Compact : "")));
                }
                finally { _audioEngine.UseExclusiveCapture = false; _audioEngine.StopAllAudio(); }
            }
        }
        catch (OperationCanceledException) { Add(AdditionalTestRegistry.CancelledResult()); }
        finally { _autoDiagnosticsCancellation = null; }
    }
}
