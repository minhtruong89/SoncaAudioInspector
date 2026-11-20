using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public enum RubBuzzAudioKind { Stimulus, Background, Microphone, Rattle, TrackingResidual, BroadbandResidual, LevelStimulus, LevelMicrophone }
public sealed record RubBuzzListeningItem(string Label, bool IsChirp, RubBuzzAudioKind Kind, int Repeat, float[] Samples, int SampleRate)
{
    public override string ToString() => Label;
}
public sealed record RubBuzzResiduals(float[][] Samples, double Gain);

/// <summary>Listening copies only. Never used by the measurement analysis or line standards.</summary>
public static class RubBuzzListeningAudio
{
    public static double? ChirpFrequency(ChirpRubBuzzSettings settings, double positionSeconds, double captureStartSeconds)
    {
        double t = positionSeconds - captureStartSeconds;
        return t < 0 || t > settings.DurationSeconds ? null
            : settings.F1Hz * Math.Exp(t / settings.DurationSeconds * Math.Log(settings.F2Hz / settings.F1Hz));
    }
    public static RubBuzzResiduals Normalize(float[][] samples)
    {
        long count = samples.Sum(x => (long)x.Length);
        double power = samples.Sum(x => x.Sum(v => float.IsFinite(v) ? (double)v * v : 0));
        double rms = Math.Sqrt(power / Math.Max(1, count));
        double gain = rms > 1e-15 ? Math.Pow(10, -26 / 20.0) / rms : 0;
        return new(samples.Select(x => x.Select(v => (float)(.9 * Math.Tanh((float.IsFinite(v) ? v : 0) * gain / .9))).ToArray()).ToArray(), gain);
    }
    public static RubBuzzResiduals Pink(PinkRubBuzzRun run) => Normalize(run.Captures.Select(c =>
        PinkRubBuzzAnalysis.BandPass(c, run.SampleRate, 1500, .45 * run.SampleRate, 8).Select(v => (float)v).ToArray()).ToArray());

    public static float[] ChirpResidual(float[] samples, int rate, ChirpRubBuzzSettings settings, double captureStartSeconds)
    {
        const int n = 2048, hop = 256;
        var window = Enumerable.Range(0, n).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / n)).ToArray();
        var output = new double[samples.Length]; var weights = new double[samples.Length];
        var fft = new Complex[n];
        for (int start = -n + hop; start < samples.Length; start += hop)
        {
            double t = (start + n / 2.0) / rate - captureStartSeconds;
            if (t <= .15 || t >= settings.DurationSeconds - .15) continue;
            double f = settings.F1Hz * Math.Exp(t / settings.DurationSeconds * Math.Log(settings.F2Hz / settings.F1Hz));
            double cutoff = Math.Max(10 * f, 1000), top = Math.Min(18000, .45 * rate);
            for (int i = 0; i < n; i++) fft[i] = start + i >= 0 && start + i < samples.Length && float.IsFinite(samples[start + i])
                ? samples[start + i] * window[i] : 0;
            Fourier.Forward(fft, FourierOptions.Matlab);
            for (int i = 0; i < n; i++)
            {
                double hz = Math.Min(i, n - i) * rate / (double)n;
                if (hz < cutoff || hz > top) fft[i] = 0;
            }
            Fourier.Inverse(fft, FourierOptions.Matlab);
            for (int i = 0; i < n; i++) if (start + i >= 0 && start + i < output.Length)
            { output[start + i] += fft[i].Real * window[i]; weights[start + i] += window[i] * window[i]; }
        }
        return output.Select((v, i) => (float)(weights[i] > .01 ? v / weights[i] : 0)).ToArray();
    }
    public static RubBuzzResiduals Chirp(ChirpRubBuzzRun run) => Normalize(run.Captures.Select((c, i) =>
        ChirpResidual(c, run.SampleRate, run.Settings, run.Metrics.CaptureStartSeconds.ElementAtOrDefault(i))).ToArray());

    public static List<RubBuzzListeningItem> Items(PinkRubBuzzRun? pink, ChirpRubBuzzRun? chirp)
    {
        var items = new List<RubBuzzListeningItem>();
        void Add(bool isChirp, string name, float[][] captures, float[] bg, int rate, float[] stimulus, int playbackRate, Func<RubBuzzResiduals> residuals)
        {
            if (captures.Length == 0 && bg.Length == 0) return;
            items.Add(new(name + " · Tín hiệu phát", isChirp, RubBuzzAudioKind.Stimulus, -1, stimulus, playbackRate));
            if (bg.Length > 0) items.Add(new(name + " · Nền", isChirp, RubBuzzAudioKind.Background, -1, bg, rate));
            for (int i = 0; i < captures.Length; i++) items.Add(new($"{name} · Mic lượt {i + 1}", isChirp, RubBuzzAudioKind.Microphone, i, captures[i], rate));
            var filtered = residuals();
            for (int i = 0; i < filtered.Samples.Length; i++) items.Add(new($"{name} · Chỉ phần rè lượt {i + 1}", isChirp, RubBuzzAudioKind.Rattle, i, filtered.Samples[i], rate));
        }
        if (pink != null) Add(false, "Pink", pink.Captures, pink.Background, pink.SampleRate,
            PinkRubBuzzStimulus.Generate(pink.Settings, pink.PlaybackSampleRate), pink.PlaybackSampleRate, () => Pink(pink));
        if (chirp != null) Add(true, "Chirp", chirp.Captures, chirp.Background, chirp.SampleRate,
            ChirpRubBuzzStimulus.Generate(chirp.Settings, chirp.PlaybackSampleRate), chirp.PlaybackSampleRate, () => Chirp(chirp));
        return items;
    }
}
