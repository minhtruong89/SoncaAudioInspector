using NAudio.Dsp;
using NAudio.Wave;

namespace SoncaAudioInspector;

// Paul Kellet pink filter, followed by second-order Butterworth HP/LP.
// REW documents this topology for random Sub Cal (30/80 Hz), rather than PN.
public sealed class PinkNoiseRandomProvider : ISampleProvider
{
    private sealed class Source
    {
        private readonly Random _random = new(1105);
        private readonly BiQuadFilter _high, _low;
        private double b0, b1, b2, b3, b4, b5, b6;
        public Source(PinkNoiseSettings settings, int rate)
        {
            _high = BiQuadFilter.HighPassFilter(rate, (float)settings.LowHz, (float)(1 / Math.Sqrt(2)));
            _low = BiQuadFilter.LowPassFilter(rate, (float)settings.HighHz, (float)(1 / Math.Sqrt(2)));
        }
        public float Next()
        {
            double white = _random.NextDouble() * 2 - 1;
            b0 = .99886 * b0 + white * .0555179;
            b1 = .99332 * b1 + white * .0750759;
            b2 = .969 * b2 + white * .1538520;
            b3 = .86650 * b3 + white * .3104856;
            b4 = .55000 * b4 + white * .5329522;
            b5 = -.7616 * b5 - white * .0168980;
            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * .5362;
            b6 = white * .115926;
            return _low.Transform(_high.Transform((float)pink));
        }
    }
    private readonly Source _source;
    private readonly double _scale;
    private readonly int? _channel;
    private long _frames;
    public WaveFormat WaveFormat { get; }
    public PinkNoiseRandomProvider(PinkNoiseSettings settings, int sampleRate, int channels, int? channel)
    {
        settings.Validate(sampleRate);
        if (sampleRate != 44100) throw new ArgumentException("Pink random dùng bộ lọc chuẩn tại 44,1 kHz.");
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        _channel = channel;
        var reference = new Source(settings, sampleRate);
        for (int i = 0; i < sampleRate * 2; i++) reference.Next();
        double power = 0;
        for (int i = 0; i < sampleRate * 30; i++) { double v = reference.Next(); power += v * v; }
        _scale = Math.Pow(10, settings.LevelDbFs / 20) / Math.Sqrt(power / (sampleRate * 30));
        _source = new Source(settings, sampleRate);
        for (int i = 0; i < sampleRate * 2; i++) _source.Next();
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / WaveFormat.Channels;
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(_source.Next() * _scale * Math.Min(1, _frames++ / (WaveFormat.SampleRate * .02)));
            if (Math.Abs(value) >= .98) throw new InvalidOperationException("Đỉnh pink noise quá cao; dừng phát và giảm mức RMS.");
            for (int c = 0; c < WaveFormat.Channels; c++)
                buffer[offset + i * WaveFormat.Channels + c] = _channel == null || _channel == c ? value : 0;
        }
        Array.Clear(buffer, offset + frames * WaveFormat.Channels, count - frames * WaveFormat.Channels);
        return count;
    }
}
