using System.IO;
using System.Text.Json;
using System.Windows;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private CapturePathEnhancementStatus CurrentCapturePathEnhancements()
        => CapturePathEnhancements.Read(SelectedRecordingDevice?.ID, SelectedRecordingDevice?.FriendlyName ?? "Chưa chọn mic");
    private void RefreshCapturePathEnhancementWarning()
    {
        if (TxtCaptureEnhancementWarning == null) return;
        var status = CurrentCapturePathEnhancements();
        TxtCaptureEnhancementWarning.Text = status.Warning;
        TxtCaptureEnhancementWarning.ToolTip = status.Tooltip;
        TxtCaptureEnhancementWarning.Visibility = status.Warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private string PinkRubBuzzDirectory => Path.Combine(GetUserDataDirectory(), "standards", "pink-rubbuzz");
    private string PinkRubBuzzContext(TestConfig config, MMDevice playback, MMDevice recording)
        => FormatPinkRubBuzzContext(config, playback.ID, recording.ID,
            (System.Windows.Application.Current?.MainWindow as MainWindow)?.ComboModels.SelectedItem?.ToString(),
            _audioEngine.PlaybackSampleRate, _audioEngine.RecordingGain, _audioEngine.UseExclusivePlayback,
            ReadEndpointSampleRate(playback), ReadEndpointSampleRate(recording), _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
            playback.AudioEndpointVolume.MasterVolumeLevelScalar, recording.AudioEndpointVolume.MasterVolumeLevelScalar);
    // Preserve the existing serialized property names/order. Logical RecordingIn labels never identify a physical capture.
    internal static string FormatPinkRubBuzzContext(TestConfig config, string actualPlayback, string actualRecording, string? model,
        int playbackSampleRate, double engineRecordingGain, bool useExclusivePlayback, int? playbackEndpointRate, int? recordingEndpointRate,
        int? actualOutChannel, int? actualInChannel, float outputVolume, float inputVolume)
        => JsonSerializer.Serialize(new { actualPlayback, actualRecording,
            test = TestSequenceOrder.EffectiveIdentity(config), model, config.ModelVolume,
            config.RecordingGain, config.PlaybackChannel, config.RecordingChannel, config.MicrophoneCalibration,
            PlaybackSampleRate = playbackSampleRate, engineRecordingGain, UseExclusivePlayback = useExclusivePlayback,
            playbackEndpointRate, recordingEndpointRate, actualOutChannel, actualInChannel, outputVolume, inputVolume });

    private static string RubBuzzBackgroundAllowancePath => Path.Combine(GetUserDataDirectory(), BackgroundAllowance.FileName);
    private static BackgroundAllowance LoadRubBuzzBackgroundAllowance() => BackgroundAllowance.Load(RubBuzzBackgroundAllowancePath);

    /// <summary>Noisy Rè background: the operator may wait, raise this station's allowance, or cancel. True = capture the background again.</summary>
    private async Task<bool> AskRubBuzzBackgroundAsync(BackgroundCheck check, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AppendLog("Rè · nền", check.Reason);
        return await Dispatcher.InvokeAsync(() =>
        {
            Window? owner = _measurementDetailsWindow is { IsVisible: true } details ? details : Window.GetWindow(this);
            var dialog = new RubBuzzBackgroundDialog(check);
            if (owner is { IsVisible: true }) dialog.Owner = owner;
            dialog.ShowDialog();
            if (dialog.Result == RubBuzzBackgroundDialog.Choice.SaveAndRetry && dialog.Allowance != check.Allowance)
            {
                dialog.Allowance.Save(RubBuzzBackgroundAllowancePath);
                AppendLog("Rè · nền cho phép", dialog.Allowance.Describe);
            }
            return dialog.Result != RubBuzzBackgroundDialog.Choice.Cancel;
        }).Task;
    }

    private void ShowRubBuzzFailure(Exception error)
    {
        var help = RubBuzzFailureGuide.Explain(error, RubBuzzActiveSessions);
        if (help == null) return;
        AppendLog("Rè · lỗi", help.Title + ": " + help.Message);
        Window? owner = _measurementDetailsWindow is { IsVisible: true } details ? details : Window.GetWindow(this);
        ModernMessageBox.ShowPersistentWarning(owner!, help.Body, help.Title);
    }

    private string RubBuzzActiveSessions()
    {
        try
        {
            var route = ReadRubBuzzRoute();
            var ids = new HashSet<string>(new[] { route?.PlaybackId, route?.RecordingId }.OfType<string>(), StringComparer.OrdinalIgnoreCase);
            return RubBuzzActiveSessions(ids);
        }
        catch { return ""; }
    }

    private MeasurementRouteInfo? CurrentMeasurementRoute()
    {
        var playback = SelectedPlaybackDevice; var recording = SelectedRecordingDevice;
        if (playback == null || recording == null) return null;
        return new(playback.ID, playback.FriendlyName, _audioEngine.PlaybackChannel + 1, GetSelectedPlaybackSampleRate(),
            recording.FriendlyName, _audioEngine.RecordingChannel + 1,
            ReadEndpointFormat(recording)?.SampleRate ?? ReadEndpointSampleRate(recording) ?? _audioEngine.RecordingSampleRate);
    }

    private async Task<RubBuzzManualResult> RunRubBuzzManualCaptureAsync(RubBuzzSoundChoice choice, CancellationToken token)
    {
        var route = ReadRubBuzzRoute();
        if (route is not { IsMatched: true })
        {
            await ReleaseManualRubBuzzAsync();
            throw new InvalidOperationException("Ngõ phát/thu không khớp bài đã chọn.");
        }
        if (_rubBuzzPreparation is { } preparation)
        {
            if (!preparation.Route.SameEndpoints(route))
            {
                await ReleaseManualRubBuzzAsync();
                throw new InvalidOperationException("Route không khớp với bài đã chuẩn bị.");
            }
            RubBuzzManualResult? result = null;
            using var cancel = token.Register(preparation.Lifetime.Cancel);
            if (!preparation.Start.TrySetResult(async ct => { result = await CaptureManualRubBuzzAsync(route, choice, ct, levelsPrepared: true); }))
                throw new InvalidOperationException("Đang đo hoặc khôi phục loa.");
            await preparation.Completion;
            return result ?? throw new InvalidOperationException("Đã dừng trước khi thu.");
        }
        CloseRoutingScope();
        using var persistence = ResultPersistencePolicy.BeginMeasurement();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null)
            throw new InvalidOperationException("Đang đo/chuyển model; chờ hoàn tất trước khi đo rè.");
        StopRoutingHeadroomMonitor();
        return await CaptureManualRubBuzzAsync(route, choice, token);
    }

    private async Task<RubBuzzManualResult> CaptureManualRubBuzzAsync(RubBuzzTestRoute testRoute,
        RubBuzzSoundChoice choice, CancellationToken token, bool levelsPrepared = false)
    {
        using var devices = new MMDeviceEnumerator();
        using var playback = devices.GetDevice(testRoute.PlaybackId!);
        using var recording = devices.GetDevice(testRoute.RecordingId!);
        var config = RubBuzzManualCapture.Configuration(testRoute.Test, choice);
        config.ModelVolume = ModelTestVolume.Merge(_activeInOutConfig?.TestVolume, config.TestVolume);
        using var levels = levelsPrepared ? null : new WindowsAudioLevels(playback, recording, config);
        var previous = (_audioEngine.PlaybackSampleRate, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
            _audioEngine.UseExclusivePlayback, _audioEngine.UseExclusiveCapture, _audioEngine.RecordingGain);
        string route = $"Đo thủ công · {config.id} · {testRoute.Label}";
        try
        {
            _audioEngine.UseExclusivePlayback = false;
            _audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
            _audioEngine.PlaybackChannel = config.PlaybackChannel - 1;
            _audioEngine.RecordingChannel = config.RecordingChannel - 1;
            _audioEngine.RecordingGain = config.RecordingGain ?? 1;
            if (AudioEngine.IsWindowsMonoAudioEnabled())
                throw new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát/thu.");
            string acquisition = PinkRubBuzzContext(config, playback, recording);
            var context = new AdditionalTestContext(_audioEngine.PlaybackSampleRate, Math.Max(32000, _audioEngine.RecordingSampleRate),
                _testRunner.PlaybackFrequencyScale, route) { AcquisitionKey = acquisition, ReferenceDirectory = PinkRubBuzzDirectory,
                    AbsoluteOnly = config.SpeakerStage == C360ModelDefinition.AcousticRubBuzzStage };
            string? preparedSpeaker = levelsPrepared ? SoncaBleManager.Instance.ConnectedDeviceAddress : null;
            void ValidateContext()
            {
                if (!testRoute.SameEndpoints(ReadRubBuzzRoute())
                    || preparedSpeaker != null && (!SoncaBleManager.Instance.IsConnected || SoncaBleManager.Instance.ConnectedDeviceAddress != preparedSpeaker)
                    || GetSelectedPlaybackSampleRate() != context.PlaybackSampleRate || PinkRubBuzzContext(config, playback, recording) != acquisition)
                    throw new InvalidOperationException("Route/gain/model/rate đã đổi; không dùng dữ liệu dở dang.");
            }
            var task = WithRubBuzzSharedFallbackAsync(playback, recording, exclusive => RubBuzzManualCapture.RunAsync(config, choice,
                exclusive ? context : context with { AcquisitionKey = acquisition + ":shared" },
                (seconds, ct) => CaptureRubBuzzBackgroundAsync(recording, seconds, ct),
                async (step, ct) =>
                {
                    var samples = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0,
                        step.DurationSeconds, cancellationToken: ct, recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: step.Samples);
                    return new(samples, _audioEngine.RecordingSampleRate);
                }, ValidateContext, exclusive => _audioEngine.UseExclusiveCapture = exclusive, _audioEngine.StopAllAudio, token,
                LoadRubBuzzBackgroundAllowance, AskRubBuzzBackgroundAsync,
                _measurementDetailsWindow?.RubBuzz.BroadbandSettings,
                _measurementDetailsWindow?.RubBuzz.ImpulsiveSettings ?? new(HighPassMultiple: Math.Clamp(config.EffectiveRubBuzzHighPassMultiple, 5, 40)),
                useExclusive: exclusive), token);
            _activeMeasurementTask = task;
            var capturePath = CapturePathEnhancements.Read(recording.ID, recording.FriendlyName);
            try
            {
                var result = await task;
                return result with { CapturePathEnhancements = capturePath,
                    Diagnosis = RubBuzzDiagnosis.Build(result.Pink, result.Chirp, result.Impulsive?.ToDiagnosisEvidence(),
                        result.Broadband?.ToDiagnosisEvidence(), result.Levels, capturePath) };
            }
            finally { if (_activeMeasurementTask == task) _activeMeasurementTask = null; }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException(route + "\nKhông hoàn tất đo Rè: " + GetMeasurementErrorMessage(ex), ex); }
        finally
        {
            _audioEngine.StopAllAudio();
            (_audioEngine.PlaybackSampleRate, _audioEngine.PlaybackChannel, _audioEngine.RecordingChannel,
                _audioEngine.UseExclusivePlayback, _audioEngine.UseExclusiveCapture, _audioEngine.RecordingGain) = previous;
        }
    }

    private async Task<RubBuzzCapturedAudio> CaptureRubBuzzBackgroundAsync(MMDevice recording, double seconds, CancellationToken token)
    {
        try
        {
            var bg = await _audioEngine.CaptureSilenceAsync(recording, null, seconds, token);
            return new(bg.DutSamples, bg.DutSampleRate);
        }
        catch (Exception ex) when (AudioSessionDiagnostics.IsDeviceInUse(ex))
        { throw new AudioEndpointInUseException($"THU ← {recording.FriendlyName}", ex); }
    }

    private Task<T> WithRubBuzzSharedFallbackAsync<T>(MMDevice playback, MMDevice recording, Func<bool, Task<T>> capture, CancellationToken token)
        => RubBuzzCaptureFallback.RunAsync(capture, ex =>
        {
            _audioEngine.StopAllAudio();
            _audioEngine.UseExclusivePlayback = _audioEngine.UseExclusiveCapture = false;
            string endpoint = RubBuzzCaptureFallback.BusyEndpoint(ex) ?? AudioQaRouteDetails.Format("đang đo", playback.FriendlyName, recording.FriendlyName);
            AppendLog("Rè WASAPI", $"0x8889000A · {endpoint} đang bị giữ ở Exclusive. Thử lại từ bản thu nền ở Shared.");
            var ids = new HashSet<string>([playback.ID, recording.ID], StringComparer.OrdinalIgnoreCase);
            string sessions = RubBuzzActiveSessions(ids);
            if (sessions.Length > 0) AppendLog("Rè WASAPI · Phiên âm thanh đang mở", sessions);
        }, token);

    private static string RubBuzzActiveSessions(IReadOnlySet<string> ids)
        => string.Join("\n", AudioSessionDiagnostics.Scan(ids).AttentionSessions.Select(s => s.Description));
}
