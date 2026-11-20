using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record BroadbandPinkSettings(double LowHz = 20, double HighHz = 20000,
    double LevelRmsDbFs = -20, double CrestTargetDb = 12, int Repeats = 3,
    int PeriodSamples = 131072, int Periods = 2, int Seed = 1114)
{
    public void Validate(int rate)
    {
        if (rate is not (44100 or 48000) || !double.IsFinite(LowHz) || !double.IsFinite(HighHz)
            || LowHz < 10 || HighHz <= LowHz || HighHz >= rate / 2.0
            || !double.IsFinite(LevelRmsDbFs) || LevelRmsDbFs < -60 || LevelRmsDbFs > -18
            || !double.IsFinite(CrestTargetDb) || CrestTargetDb is < 6 or > 15
            || Repeats is < 2 or > 5 || PeriodSamples is < 16384 or > 262144
            || (PeriodSamples & (PeriodSamples - 1)) != 0 || Periods is < 2 or > 4)
            throw new ArgumentException("Pink dải rộng: 44,1/48 kHz; dải dưới Nyquist; RMS −60…−18 dBFS; crest 6…15 dB; 2–5 lượt; 2–4 chu kỳ FFT.");
        if (LevelRmsDbFs + CrestTargetDb >= -.2)
            throw new ArgumentException("Pink dải rộng không đủ headroom; giảm mức RMS hoặc crest mục tiêu.");
    }
}

public sealed record BroadbandPinkBandLevel(double FrequencyHz, double LevelDbFs);
public sealed record BroadbandPinkStimulusInfo(double RmsDbFs, double PeakDbFs, double AchievedCrestDb,
    double TargetCrestDb, BroadbandPinkBandLevel[] ThirdOctaveLevels);

/// <summary>Periodic FFT pink, |X(f)| ∝ f^−1/2. Clipping changes only phases: each iteration restores
/// every excited FFT-bin magnitude, including exact zeros outside the configured band. IEC/AES-inspired
/// signal shape only; no claim of conformity with either document. A period is repeated bit-for-bit.</summary>
public static class BroadbandPinkStimulus
{
    public const string Label = "Pink dải rộng (dạng IEC 60268-1 / AES2 — không khẳng định tuân thủ)";
    public static float[] CreatePeriod(BroadbandPinkSettings settings, int rate)
    {
        settings.Validate(rate);
        int n = settings.PeriodSamples;
        var f = new Complex[n]; var magnitude = new double[n / 2]; var random = new Random(settings.Seed);
        for (int k = 1; k < n / 2; k++)
        {
            double hz = k * (double)rate / n;
            if (hz < settings.LowHz || hz > settings.HighHz) continue;
            magnitude[k] = 1 / Math.Sqrt(hz);
            f[k] = Complex.FromPolarCoordinates(magnitude[k], random.NextDouble() * 2 * Math.PI);
            f[n - k] = Complex.Conjugate(f[k]);
        }
        Fourier.Inverse(f, FourierOptions.Matlab);
        double target = Math.Pow(10, settings.CrestTargetDb / 20);
        for (int iteration = 0; iteration < 160; iteration++)
        {
            double rms = Math.Sqrt(f.Sum(z => z.Real * z.Real) / n), peak = f.Max(z => Math.Abs(z.Real));
            if (rms <= 0) throw new ArgumentException("Dải pink quá hẹp cho chu kỳ FFT.");
            if (peak / rms <= target * 1.005) break;
            double limit = rms * target * .985;
            for (int i = 0; i < n; i++) f[i] = Math.Clamp(f[i].Real, -limit, limit);
            Fourier.Forward(f, FourierOptions.Matlab); f[0] = f[n / 2] = 0;
            for (int k = 1; k < n / 2; k++)
            {
                f[k] = magnitude[k] == 0 ? Complex.Zero : Complex.FromPolarCoordinates(magnitude[k], f[k].Phase);
                f[n - k] = Complex.Conjugate(f[k]);
            }
            Fourier.Inverse(f, FourierOptions.Matlab);
        }
        double scale = Math.Pow(10, settings.LevelRmsDbFs / 20) / Math.Sqrt(f.Sum(z => z.Real * z.Real) / n);
        var result = f.Select(z => (float)(z.Real * scale)).ToArray();
        if (result.Any(v => Math.Abs(v) >= .98)) throw new ArgumentException("Pink dải rộng không đủ headroom thực đo; giảm mức RMS.");
        return result;
    }
    public static float[] Generate(BroadbandPinkSettings settings, int rate)
    {
        float[] period = CreatePeriod(settings, rate), result = new float[period.Length * settings.Periods];
        for (int i = 0; i < result.Length; i++) result[i] = period[i % period.Length];
        return result;
    }
    public static BroadbandPinkStimulusInfo Describe(float[] stimulus, int rate, BroadbandPinkSettings settings)
    {
        int n = settings.PeriodSamples;
        if (stimulus.Length < n) throw new ArgumentException("Thiếu một chu kỳ pink.");
        var f = new Complex[n]; double power = 0, peak = 0;
        for (int i = 0; i < n; i++) { f[i] = stimulus[i]; power += (double)stimulus[i] * stimulus[i]; peak = Math.Max(peak, Math.Abs(stimulus[i])); }
        Fourier.Forward(f, FourierOptions.Matlab);
        var bands = new List<BroadbandPinkBandLevel>();
        for (double fc = 1000 * Math.Pow(2, -17 / 3.0); fc < settings.HighHz * 1.13; fc *= Math.Pow(2, 1 / 3.0))
        {
            double sum = 0, lo = fc / Math.Pow(2, 1 / 6.0), hi = fc * Math.Pow(2, 1 / 6.0);
            for (int k = Math.Max(1, (int)Math.Ceiling(lo * n / rate)); k < Math.Min(n / 2, hi * n / rate); k++) sum += 2 * f[k].Magnitude * f[k].Magnitude / (n * (double)n);
            bands.Add(new(fc, DbPower(sum)));
        }
        double rmsDb = DbPower(power / n), peakDb = 20 * Math.Log10(Math.Max(1e-15, peak));
        return new(rmsDb, peakDb, peakDb - rmsDb, settings.CrestTargetDb, bands.ToArray());
    }
    private static double DbPower(double value) => 10 * Math.Log10(Math.Max(1e-30, value));
}
