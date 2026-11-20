using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private async Task RunPinkNoiseAsync(PinkNoiseSettings settings, CancellationToken cancellationToken)
    {
        CloseRoutingScope();
        using var persistence = ResultPersistencePolicy.BeginMeasurement();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null) throw new InvalidOperationException("Ngõ âm thanh đang bận; dừng bài đo trước khi phát pink noise.");
        StopRoutingHeadroomMonitor();
        var playback = SelectedPlaybackDevice ?? throw new InvalidOperationException("Chưa chọn ngõ phát ở Audio Routing.");
        const int sampleRate = PinkNoiseSignal.DefaultSampleRate;
        int channels = 2;
        if (!_audioEngine.UseExclusivePlayback)
        {
            if (AudioEngine.IsWindowsMonoAudioEnabled()) throw new InvalidOperationException("Tắt Windows Mono audio để giữ đúng kênh phát.");
            using var client = playback.AudioClient;
            channels = client.MixFormat.Channels;
        }
        int? channel = _audioEngine.PlaybackChannel;
        if (channel >= channels) throw new InvalidOperationException("Kênh phát không có trên thiết bị.");
        ISampleProvider provider = await Task.Run<ISampleProvider>(() => settings.RandomNoise
            ? new PinkNoiseRandomProvider(settings, sampleRate, channels, channel)
            : new PinkNoiseLoopProvider(PinkNoiseSignal.CreatePeriod(settings, sampleRate), sampleRate, channels, channel), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new WasapiOut(playback, _audioEngine.UseExclusivePlayback ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, false, 100);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception != null) stopped.TrySetException(e.Exception); else stopped.TrySetResult(); };
        output.Init(provider);
        output.Play();
        try { await stopped.Task.WaitAsync(cancellationToken); }
        finally { output.Stop(); }
    }
}
