using System.Windows;
using System.Windows.Input;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

/// <summary>Ctrl+Shift+T: guided test of the subwoofer rear-panel knobs/buttons on the selected route.</summary>
public partial class AudioRouting
{
    private SubwooferControlsWindow? _subwooferControlsWindow;
    private IDisposable? _subwooferSession;

    internal static bool IsSubwooferControlsShortcut(Key key, ModifierKeys modifiers, bool repeat)
        => key == Key.T && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && !repeat;

    private void ShowSubwooferControls()
    {
        if (_subwooferControlsWindow == null)
        {
            _subwooferControlsWindow = new SubwooferControlsWindow(new SubwooferControlsHost
            {
                BeginSession = BeginSubwooferSession, MeasureAsync = MeasureSubwooferAsync, RouteProvider = CurrentMeasurementRoute,
                DataDirectory = GetUserDataDirectory
            }) { Owner = Window.GetWindow(this) };
            _subwooferControlsWindow.Closed += (_, _) => { _subwooferControlsWindow = null; _subwooferSession?.Dispose(); };
            _subwooferControlsWindow.Show();
        }
        _subwooferControlsWindow.Activate();
    }

    /// <summary>One routing workflow for the whole guided test: route, rate and channels stay locked while the operator
    /// turns knobs between steps, and the sample-rate question is asked once.</summary>
    private IDisposable BeginSubwooferSession()
    {
        if (_subwooferSession != null) return _subwooferSession;
        CloseRoutingScope();
        var workflow = TryBeginRoutingWorkflow()
            ?? throw new InvalidOperationException("Thiết bị đang đo/chuyển model; chờ hoàn tất trước khi test núm subwoofer.");
        try
        {
            StopRoutingHeadroomMonitor();
            if (SelectedPlaybackDevice == null || SelectedRecordingDevice == null)
                throw new InvalidOperationException("Chọn ngõ phát (vào subwoofer) và ngõ thu (mic) ở Audio Routing trước.");
            if (AudioEngine.IsWindowsMonoAudioEnabled())
                throw new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát/thu.");
            int oldRate = _audioEngine.PlaybackSampleRate;
            _audioEngine.PlaybackSampleRate = GetSelectedPlaybackSampleRate();
            var lease = new SubwooferSessionLease(this, workflow, oldRate);
            _subwooferSession = lease;
            return lease;
        }
        catch { workflow.Dispose(); throw; }
    }

    private sealed class SubwooferSessionLease(AudioRouting owner, IDisposable workflow, int oldRate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { owner._audioEngine.StopAllAudio(); owner._audioEngine.PlaybackSampleRate = oldRate; }
            finally
            {
                if (ReferenceEquals(owner._subwooferSession, this)) owner._subwooferSession = null;
                workflow.Dispose();
            }
        }
    }

    private async Task<SubwooferResponse> MeasureSubwooferAsync(SubwooferSweepSettings settings, IProgress<string>? progress, CancellationToken token)
    {
        if (_subwooferSession == null) throw new InvalidOperationException("Bấm Bắt đầu trước khi đo.");
        if (_activeMeasurementTask != null) throw new InvalidOperationException("Đang đo; chờ lượt hiện tại xong.");
        settings.Validate();
        var playback = SelectedPlaybackDevice ?? throw new InvalidOperationException("Chưa chọn ngõ phát.");
        var recording = SelectedRecordingDevice ?? throw new InvalidOperationException("Chưa chọn ngõ thu.");
        var task = Run();
        _activeMeasurementTask = task;
        try { return await task; }
        finally { if (_activeMeasurementTask == task) _activeMeasurementTask = null; }

        async Task<SubwooferResponse> Run()
        {
            float[] stimulus = SubwooferSweep.Generate(settings, _audioEngine.PlaybackSampleRate);
            string key = SubwooferRouteKey(playback, recording);
            string route = $"Ngõ phát {playback.FriendlyName} / {(_audioEngine.PlaybackChannel + 1)?.ToString() ?? "L+R"} · "
                + $"Ngõ thu {recording.FriendlyName} / {(_audioEngine.RecordingChannel + 1)?.ToString() ?? "Trộn"}";
            var captures = new List<SubwooferCapture>();
            try
            {
                for (int i = 0; i < settings.Repeats; i++)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report($"Đang phát sweep {settings.StartHz:0}→{settings.EndHz:0} Hz · lượt {i + 1}/{settings.Repeats}…");
                    var samples = await _audioEngine.PlayAndRecordAsync(playback, recording, SignalType.Sweep, 0, settings.TotalSeconds,
                        cancellationToken: token, recordingChannel: _audioEngine.RecordingChannel, customSweepSamples: stimulus);
                    token.ThrowIfCancellationRequested();
                    if (SubwooferRouteKey(playback, recording) != key)
                        throw new InvalidOperationException("Ngõ, rate hoặc âm lượng Windows đổi trong lúc đo; đo lại bước này.");
                    captures.Add(new(samples, _audioEngine.RecordingSampleRate));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            { throw new InvalidOperationException("Không đo được sweep subwoofer: " + GetMeasurementErrorMessage(ex), ex); }
            finally { _audioEngine.StopAllAudio(); }
            progress?.Report("Đang phân tích…");
            return await Task.Run(() => SubwooferControlAnalysis.Analyze(captures, settings, key, route), token);
        }
    }

    /// <summary>Everything that changes the level or timing between two steps besides the knob under test.</summary>
    private string SubwooferRouteKey(MMDevice playback, MMDevice recording)
        => string.Join("|", playback.ID, recording.ID, _audioEngine.PlaybackSampleRate, _audioEngine.PlaybackChannel,
            _audioEngine.RecordingChannel, _audioEngine.UseExclusivePlayback, _audioEngine.UseExclusiveCapture,
            playback.AudioEndpointVolume.MasterVolumeLevelScalar.ToString("0.000"), playback.AudioEndpointVolume.Mute,
            recording.AudioEndpointVolume.MasterVolumeLevelScalar.ToString("0.000"), recording.AudioEndpointVolume.Mute);
}
