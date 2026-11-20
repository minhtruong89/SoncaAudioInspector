using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace SoncaAudioInspector;

// Standalone v1.1 stimulus. Does not change the legacy pink-noise generators.
public sealed record PinkNoiseSettings(double LowHz = 20, double HighHz = 20000, double LevelDbFs = -18, bool RandomNoise = false)
{
    public void Validate(int sampleRate)
    {
        if (sampleRate < 8000 || !double.IsFinite(LowHz) || !double.IsFinite(HighHz)
            || LowHz < 10 || HighHz <= LowHz || HighHz >= sampleRate / 2.0
            || !double.IsFinite(LevelDbFs) || LevelDbFs < -60 || LevelDbFs > -18)
            throw new ArgumentException("Pink noise: dải tần phải nằm dưới Nyquist; mức RMS từ -60 đến -18 dBFS.");
    }
}

public static class PinkNoiseSignal
{
    public const int DefaultSampleRate = 44100;
    public const int PeriodSamples = 131072;
    public static float[] CreatePeriod(PinkNoiseSettings settings, int sampleRate)
    {
        settings.Validate(sampleRate);
        var spectrum = new Complex[PeriodSamples];
        var random = new Random(1105);
        for (int k = 1; k < PeriodSamples / 2; k++)
        {
            double hz = k * (double)sampleRate / PeriodSamples;
            if (hz < settings.LowHz || hz > settings.HighHz) continue;
            spectrum[k] = Complex.FromPolarCoordinates(1 / Math.Sqrt(hz), random.NextDouble() * 2 * Math.PI);
            spectrum[PeriodSamples - k] = Complex.Conjugate(spectrum[k]);
        }
        Fourier.Inverse(spectrum, FourierOptions.Matlab);
        double rms = Math.Sqrt(spectrum.Sum(v => v.Real * v.Real) / PeriodSamples);
        if (rms == 0) throw new ArgumentException("Dải tần quá hẹp cho độ phân giải FFT.");
        double scale = Math.Pow(10, settings.LevelDbFs / 20) / rms;
        var samples = spectrum.Select(v => (float)(v.Real * scale)).ToArray();
        if (samples.Any(v => Math.Abs(v) >= .98))
            throw new ArgumentException("Không đủ headroom; giảm mức RMS.");
        return samples;
    }

    public static void SaveWave(string path, PinkNoiseSettings settings, int sampleRate = DefaultSampleRate)
    {
        if (settings.RandomNoise)
        {
            var provider = new PinkNoiseRandomProvider(settings, sampleRate, 1, null);
            var samples = new float[PeriodSamples * 12];
            provider.Read(samples, 0, samples.Length);
            double rms = Math.Sqrt(samples.Sum(v => (double)v * v) / samples.Length);
            double scale = Math.Pow(10, settings.LevelDbFs / 20) / rms;
            if (samples.Any(v => Math.Abs(v * scale) >= .98)) throw new ArgumentException("Không đủ headroom; giảm mức RMS.");
            using var randomWriter = new WaveFileWriter(path, new WaveFormat(sampleRate, 24, 1));
            var bytes = new byte[samples.Length * 3];
            for (int i = 0; i < samples.Length; i++)
            {
                int value = (int)Math.Round(samples[i] * scale * 8388607);
                bytes[i * 3] = (byte)value; bytes[i * 3 + 1] = (byte)(value >> 8); bytes[i * 3 + 2] = (byte)(value >> 16);
            }
            randomWriter.Write(bytes, 0, bytes.Length);
            return;
        }
        var period = CreatePeriod(settings, sampleRate);
        // Exact periods keep RMS and spectrum reproducible; PCM24 for normal players.
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 24, 1));
        var data = new byte[period.Length * 3];
        for (int i = 0; i < period.Length; i++)
        {
            int value = (int)Math.Round(period[i] * 8388607);
            data[i * 3] = (byte)value; data[i * 3 + 1] = (byte)(value >> 8); data[i * 3 + 2] = (byte)(value >> 16);
        }
        for (int repeat = 0; repeat < 12; repeat++) writer.Write(data, 0, data.Length);
    }
}

public sealed class PinkNoiseLoopProvider(float[] period, int sampleRate, int channels, int? channel) : ISampleProvider
{
    private int _position;
    private long _frames;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / WaveFormat.Channels;
        for (int i = 0; i < frames; i++)
        {
            // Short startup ramp; the periodic joins themselves require no crossfade.
            float sample = period[_position] * (float)Math.Min(1, _frames++ / (sampleRate * .02));
            _position = (_position + 1) % period.Length;
            for (int c = 0; c < WaveFormat.Channels; c++)
                buffer[offset + i * WaveFormat.Channels + c] = channel == null || channel == c ? sample : 0;
        }
        Array.Clear(buffer, offset + frames * WaveFormat.Channels, count - frames * WaveFormat.Channels);
        return count;
    }
}
