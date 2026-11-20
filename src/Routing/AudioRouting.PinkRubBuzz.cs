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
            var ids = new HashSet<string>(new[] { SelectedPlaybackDevice?.ID, SelectedRecordingDevice?.ID }.OfType<string>(), StringComparer.OrdinalIgnoreCase);
            return string.Join("\n", AudioSessionDiagnostics.Scan(ids).AttentionSessions.Select(s => s.Description));
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
        CloseRoutingScope();
        using var persistence = ResultPersistencePolicy.BeginMeasurement();
        // TryBeginRoutingWorkflow already refuses an active measurement/Auto Test and then marks this workflow active,
        // so IsModelTransitionBusy is always true here; testing it again refused every manual Rè run before playback.
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null)
            throw new InvalidOperationException("Đang đo/chuyển model; chờ hoàn tất trước khi đo rè.");
        StopRoutingHeadroomMonitor();
        var playback = SelectedPlaybackDevice ?? throw new InvalidOperationException("Chưa chọn ngõ phát.");
        var recording = SelectedRecordingDevice ?? throw new InvalidOperationException("Chưa chọn ngõ thu.");
        var source = _reviewedAutoTestCase?.Config ?? _appliedTestConfig ?? _currentRunningTestCase?.Config;
        var config = RubBuzzManualCapture.Configuration(source, choice);
        config.ModelVolume = ModelTestVolume.Merge(_activeInOutConfig?.TestVolume, config.TestVolume);
        // Use the visible routing channels, including for C360. No mixer/BLE operations here.
        config.PlaybackChannel = _audioEngine.PlaybackChannel + 1; config.RecordingChannel = _audioEngine.RecordingChannel + 1;
        using var levels = new WindowsAudioLevels(playback, recording, config);
        int oldRate = _audioEngine.PlaybackSampleRate;
        bool oldExclusivePlayback = _audioEngine.UseExclusivePlayback;
        try
        {
            _audioEngine.UseExclusivePlayback = false;
            _audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
            if (AudioEngine.IsWindowsMonoAudioEnabled())
                throw new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát/thu.");
            string acquisition = PinkRubBuzzContext(config, playback, recording);
            string route = $"Chẩn đoán thủ công · {config.id} · Ngõ phát {playback.FriendlyName} / {config.PlaybackChannel?.ToString() ?? "L+R"} · Ngõ thu {recording.FriendlyName} / {config.RecordingChannel?.ToString() ?? "Trộn"}";
            var context = new AdditionalTestContext(_audioEngine.PlaybackSampleRate, Math.Max(32000, _audioEngine.RecordingSampleRate),
                _testRunner.PlaybackFrequencyScale, route) { AcquisitionKey = acquisition, ReferenceDirectory = PinkRubBuzzDirectory };
            void ValidateContext()
            {
                if (SelectedPlaybackDevice?.ID != playback.ID || SelectedRecordingDevice?.ID != recording.ID
                    || GetSelectedPlaybackSampleRate() != context.PlaybackSampleRate || PinkRubBuzzContext(config, playback, recording) != acquisition)
                    throw new InvalidOperationException("Route/gain/model/rate đã đổi; không dùng dữ liệu dở dang.");
            }
            var task = RubBuzzManualCapture.RunAsync(config, choice, context,
                async (seconds, ct) =>
                {
                    var bg = await _audioEngine.CaptureSilenceAsync(recording, null, seconds, ct);
                    return new(bg.DutSamples, bg.DutSampleRate);
                },
                async (step, ct) =>
                {
                    var samples = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0,
                        step.DurationSeconds, cancellationToken: ct, recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: step.Samples);
                    return new(samples, _audioEngine.RecordingSampleRate);
                }, ValidateContext, exclusive => _audioEngine.UseExclusiveCapture = exclusive, _audioEngine.StopAllAudio, token,
                LoadRubBuzzBackgroundAllowance, AskRubBuzzBackgroundAsync,
                _measurementDetailsWindow?.RubBuzz.BroadbandSettings,
                _measurementDetailsWindow?.RubBuzz.ImpulsiveSettings ?? new(HighPassMultiple: Math.Clamp(config.EffectiveRubBuzzHighPassMultiple, 5, 40)));
            _activeMeasurementTask = task;
            var capturePath = CurrentCapturePathEnhancements();
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
        catch (Exception ex)
        {
            // Only device-open failures get the Exclusive hint; a refused background or a changed route is not an Exclusive problem.
            bool deviceError = ex is System.Runtime.InteropServices.COMException or NotSupportedException or IOException or UnauthorizedAccessException
                || AudioSessionDiagnostics.IsDeviceInUse(ex);
            throw new InvalidOperationException((deviceError
                ? "Không mở được mic ở chế độ Exclusive (đóng app khác đang dùng mic; mic cần hỗ trợ WASAPI Exclusive): "
                : "Không hoàn tất đo Rè: ") + GetMeasurementErrorMessage(ex), ex);
        }
        finally
        {
            _audioEngine.UseExclusiveCapture = false; _audioEngine.StopAllAudio();
            _audioEngine.UseExclusivePlayback = oldExclusivePlayback; _audioEngine.PlaybackSampleRate = oldRate;
        }
    }
}
