using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoncaAudioInspector;

public sealed record RubBuzzRenderDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Independent WASAPI Shared replay. Does not use AudioEngine or change endpoint volume.</summary>
public sealed class RubBuzzAudioPlayer : IDisposable
{
    private WasapiOut? _output;
    private MMDevice? _device;
    private int _bytesPerSecond;
    private double _duration;
    public string? Error { get; private set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double PositionSeconds => Math.Min(_duration, _output == null ? 0 : _output.GetPosition() / (double)Math.Max(1, _bytesPerSecond));
    public static RubBuzzRenderDevice[] ActiveDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d =>
        { using (d) return new RubBuzzRenderDevice(d.ID, d.FriendlyName); }).ToArray();
    }
    public static RubBuzzRenderDevice? DefaultDevice(IEnumerable<RubBuzzRenderDevice> devices, string? testId)
        => devices.Where(d => !string.Equals(d.Id, testId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Name.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("tai nghe", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            ?? devices.FirstOrDefault();
    public static bool NeedsConfirmation(RubBuzzListeningItem item, string renderId, string? testId)
        => item.Kind is RubBuzzAudioKind.Microphone or RubBuzzAudioKind.Rattle or RubBuzzAudioKind.TrackingResidual
            or RubBuzzAudioKind.BroadbandResidual or RubBuzzAudioKind.LevelMicrophone && string.Equals(renderId, testId, StringComparison.OrdinalIgnoreCase);
    public void Play(RubBuzzListeningItem item, string deviceId)
    {
        Stop(); Error = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator(); _device = enumerator.GetDevice(deviceId);
            using var client = _device.AudioClient;
            var format = client.MixFormat;
            ISampleProvider samples = new MemorySamples(item.Samples, item.SampleRate);
            if (item.SampleRate != format.SampleRate) samples = new WdlResamplingSampleProvider(samples, format.SampleRate);
            samples = new RenderChannels(samples, format.Channels);
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 80);
            var output = _output;
            output.PlaybackStopped += (_, e) => { if (ReferenceEquals(_output, output) && e.Exception != null) Error = "Không phát được: " + e.Exception.Message; };
            var wave = samples.ToWaveProvider(); _bytesPerSecond = wave.WaveFormat.AverageBytesPerSecond;
            _duration = item.Samples.Length / (double)item.SampleRate;
            output.Init(wave); output.Play();
        }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        var output = _output; _output = null;
        try { output?.Stop(); } finally { output?.Dispose(); _device?.Dispose(); _device = null; }
    }
    public void Dispose() => Stop();
    private sealed class MemorySamples(float[] samples, int rate) : ISampleProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        public int Read(float[] buffer, int offset, int count)
        { int n = Math.Min(count, samples.Length - _position); Array.Copy(samples, _position, buffer, offset, n); _position += n; return n; }
    }
    private sealed class RenderChannels(ISampleProvider source, int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            var mono = new float[count / channels]; int n = source.Read(mono, 0, mono.Length);
            for (int i = 0; i < n; i++) for (int c = 0; c < channels; c++) buffer[offset + i * channels + c] = mono[i];
            return n * channels;
        }
    }
}
