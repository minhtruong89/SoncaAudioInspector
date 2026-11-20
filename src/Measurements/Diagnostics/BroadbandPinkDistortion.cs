using System.Numerics;
using System.Text.Json.Serialization;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record BroadbandPinkAlignment(int Repeat, double LagSamples, double ClockOffsetPpm,
    double Correlation, double DriftResidualSamples)
{
    public double RepeatFractionalShiftSamples { get; init; }
}
public sealed record BroadbandPinkBand(double FrequencyHz, double LowHz, double HighHz, double OutputDbFs,
    double NoiseDbFs, double NonlinearDbFs, double NonlinearRelativeDb, double Coherence, double SnrDb, bool Sensitive)
{
    public string Sensitivity => Sensitive ? "Đủ độ nhạy" : "không đủ độ nhạy";
}
public sealed record BroadbandPinkResult(bool IsValid, string Reason)
{
    [JsonIgnore] public bool PersistenceAllowed { get; init; } = ResultPersistencePolicy.CanPersist;
    public string Version { get; init; } = BroadbandPinkDistortion.Version;
    public BroadbandPinkSettings Settings { get; init; } = new();
    public int SampleRate { get; init; }
    public int PlaybackSampleRate { get; init; }
    public string CaptureMode { get; init; } = "WASAPI Exclusive";
    public bool Clipped { get; init; }
    public BroadbandPinkStimulusInfo? Stimulus { get; init; }
    public BroadbandPinkAlignment[] Alignment { get; init; } = [];
    public BroadbandPinkBand[] Bands { get; init; } = [];
    public BroadbandPinkBand? HighFrequency { get; init; }
    public BroadbandPinkBand? LowMid { get; init; }
    public double? HighFrequencyNlKurtosis { get; init; }
    public double? HighFrequencyNlCrestDb { get; init; }
    public string Interpretation { get; init; } = "Phần dư so mô hình tuyến tính tốt nhất; gồm sai số mô hình và nhiễu. Một seed tuần hoàn không tách duy nhất mọi méo xác định; không dùng làm PASS/FAIL.";
    [JsonIgnore] public float[] NonlinearResidual { get; init; } = [];
    [JsonIgnore] public float[][] Captures { get; init; } = [];
    [JsonIgnore] public float[] Background { get; init; } = [];
    [JsonIgnore] public float[] StimulusSamples { get; init; } = [];
    public BroadbandDistortionEvidence ToDiagnosisEvidence() => new(IsValid && HighFrequency?.Sensitive == true,
        HighFrequency?.NonlinearRelativeDb, LowMid?.NonlinearRelativeDb, HighFrequencyNlKurtosis, HighFrequency?.SnrDb);
}

/// <summary>Best linear approximation (BLA), not physical source identification. DAC/ADC ratio comes
/// from early/late correlation, with middle-window residual as an independent drift diagnostic. A 64-tap,
/// 2048-phase Blackman-windowed sinc corrects both fractional delay and clock ratio before averaging.
/// H = ΣY X*/Σ|X|² uses 8192-point Hann Welch frames, followed by a 129-tap finite-window least-squares
/// refinement. Its impulse is limited to ±64 samples so a
/// full-bin periodic transfer function cannot simply absorb every deterministic nonlinear component.
/// Consequently long room responses and sharp filters can remain in NL; the label and validity preserve
/// that limitation. Noise is unbiased inter-repeat sample variance, never cross-clock waveform subtraction.</summary>
public static class BroadbandPinkDistortion
{
    public const string Version = "rubbuzz-diagnostics-1.0.0";
    private const int FftSize = 8192, Radius = 32, Phases = 2048, ImpulseRadius = 256;
    private static readonly double[][] SincTable = CreateSincTable();
    private static readonly double[] Window = Enumerable.Range(0, FftSize).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / (FftSize - 1))).ToArray();
    private static double PowerDb(double p) => 10 * Math.Log10(Math.Max(1e-30, p));
    private static int PowerOfTwo(int n) { int p = 1; while (p < n) p <<= 1; return p; }
    private static double[][] CreateSincTable()
    {
        var table = new double[Phases][];
        for (int p = 0; p < Phases; p++)
        {
            table[p] = new double[Radius * 2]; double sum = 0;
            for (int j = 0; j < Radius * 2; j++)
            {
                double x = j - Radius + 1 - p / (double)Phases;
                double sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double w = .42 + .5 * Math.Cos(Math.PI * x / Radius) + .08 * Math.Cos(2 * Math.PI * x / Radius);
                sum += table[p][j] = sinc * w;
            }
            for (int j = 0; j < Radius * 2; j++) table[p][j] /= sum;
        }
        return table;
    }
    /// <summary>Local 64-tap polyphase sinc for ratios close to unity and 44.1→48 kHz upsampling.
    /// Downsampling from 48→44.1 needs a separate anti-alias filter and is deliberately rejected.</summary>
    public static float[] ResampleSinc(float[] source, double sourceStep, int count, double start = 0)
    {
        if (!double.IsFinite(sourceStep) || sourceStep <= 0 || sourceStep > 1.002 || count < 0)
            throw new ArgumentException("Sinc: chỉ hỗ trợ upsample hoặc hiệu chỉnh clock ±2000 ppm.");
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double position = start + i * sourceStep; int center = (int)Math.Floor(position);
            int phase = Math.Clamp((int)Math.Round((position - center) * Phases), 0, Phases - 1);
            double value = 0; var coefficients = SincTable[phase];
            for (int j = 0; j < Radius * 2; j++) { int k = center + j - Radius + 1; if ((uint)k < source.Length) value += source[k] * coefficients[j]; }
            output[i] = (float)value;
        }
        return output;
    }
    private static int CoarseLag(float[] reference, float[] capture, int rate)
    {
        const int step = 4;
        int na = reference.Length / step, nb = capture.Length / step, n = PowerOfTwo(na + nb);
        var a = new Complex[n]; var b = new Complex[n];
        for (int i = 0; i < na; i++) { double s = 0; for (int j = 0; j < step; j++) s += reference[i * step + j]; a[i] = s; }
        for (int i = 0; i < nb; i++) { double s = 0; for (int j = 0; j < step; j++) s += capture[i * step + j]; b[i] = s; }
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < n; i++) b[i] *= Complex.Conjugate(a[i]);
        Fourier.Inverse(b, FourierOptions.Matlab);
        int best = 0; double largest = double.NegativeInfinity;
        int max = Math.Min(nb - 1, rate / step);
        for (int lag = -rate / 10 / step; lag <= max; lag++) if (b[(lag + n) % n].Real > largest) { largest = b[(lag + n) % n].Real; best = lag; }
        return best * step;
    }
    private static (double Delay, double Correlation) LocalLag(float[] x, float[] y, int start, int initial)
    {
        const int length = 16384;
        int a0 = Math.Clamp(start, 0, Math.Max(0, x.Length - length));
        int y0 = a0 + initial;
        if (y0 < 0 || y0 + length > y.Length) return (initial, 0);
        int n = length; var a = new Complex[n]; var b = new Complex[n]; double xx = 0, yy = 0;
        for (int i = 0; i < n; i++)
        {
            double w = .5 - .5 * Math.Cos(2 * Math.PI * i / (n - 1));
            a[i] = x[a0 + i] * w; b[i] = y[y0 + i] * w; xx += a[i].Real * a[i].Real; yy += b[i].Real * b[i].Real;
        }
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int k = 0; k < n; k++) b[k] *= Complex.Conjugate(a[k]);
        // Continuous Fourier correlation; optimize its peak rather than parabolic sample interpolation.
        double Value(double delay)
        {
            double value = 0;
            for (int k = 1; k < n / 2; k++)
            {
                double angle = 2 * Math.PI * k * delay / n;
                value += 2 * (b[k].Real * Math.Cos(angle) - b[k].Imaginary * Math.Sin(angle));
            }
            return value / n;
        }
        double best = 0, peak = double.NegativeInfinity;
        for (int shift = -24; shift <= 24; shift++) { double v = Value(shift); if (v > peak) { peak = v; best = shift; } }
        double lo = best - 1, hi = best + 1;
        for (int i = 0; i < 24; i++) { double u = lo + (hi - lo) / 3, v = hi - (hi - lo) / 3; if (Value(u) > Value(v)) hi = v; else lo = u; }
        double offset = (lo + hi) / 2;
        return (initial + offset, Math.Clamp(Value(offset) / Math.Sqrt(Math.Max(1e-30, xx * yy)), -1, 1));
    }
    public static BroadbandPinkResult Analyze(float[][] captures, float[] background, int captureRate,
        float[] stimulus, int playbackRate, BroadbandPinkSettings? settings = null)
    {
        settings ??= new(); settings.Validate(playbackRate);
        BroadbandPinkResult Invalid(string reason) => new(false, reason)
        {
            Settings = settings, SampleRate = captureRate, PlaybackSampleRate = playbackRate,
            Captures = captures, Background = background, StimulusSamples = stimulus
        };
        if (captureRate < playbackRate || captureRate > 96000) return Invalid("Cần rate thu ≥ rate phát, tối đa 96 kHz.");
        if (captures.Length < 2 || stimulus.Length < FftSize * 5 || background.Length < captureRate) return Invalid("Cần ≥ 2 lượt, tín hiệu đủ dài và nền ≥ 1 s.");
        if (captures.Append(background).Append(stimulus).Any(x => x.Any(v => !float.IsFinite(v)))) return Invalid("Dữ liệu có NaN/Infinity.");
        if (background.All(v => v == 0)) return Invalid("Nền số 0; kiểm tra đường thu hoặc xử lý Windows.");
        if (captures.Any(x => x.Length < stimulus.Length * (double)captureRate / playbackRate)) return Invalid("Lượt thu thiếu tín hiệu.");
        bool clipped = captures.Any(x => x.Any(v => Math.Abs(v) >= .98));
        if (clipped) return Invalid("Clipping ngõ thu; giảm gain mic.") with { Clipped = true };
        int length = (int)Math.Round(stimulus.Length * (double)captureRate / playbackRate);
        float[] reference = ResampleSinc(stimulus, playbackRate / (double)captureRate, length);
        var aligned = new float[captures.Length][]; var alignment = new List<BroadbandPinkAlignment>();
        for (int r = 0; r < captures.Length; r++)
        {
            int initial = CoarseLag(reference, captures[r], captureRate);
            int earlyAt = captureRate / 2, lateAt = length - captureRate / 2 - 16384;
            var early = LocalLag(reference, captures[r], earlyAt, initial);
            var late = LocalLag(reference, captures[r], lateAt, initial);
            double ratio = 1 + (late.Delay - early.Delay) / (lateAt - (double)earlyAt);
            double lag = early.Delay - earlyAt * (ratio - 1);
            var middle = LocalLag(reference, captures[r], length / 2, initial);
            double residual = middle.Delay - (lag + length / 2.0 * (ratio - 1));
            alignment.Add(new(r + 1, lag, (ratio - 1) * 1e6, Math.Min(early.Correlation, late.Correlation), residual));
            if (Math.Abs(ratio - 1) > .002 || lag < -Radius || lag + (length - 1) * ratio > captures[r].Length + Radius)
                return Invalid("Lệch clock/độ trễ ngoài vùng có dữ liệu; không trừ waveform khác clock.") with { Alignment = alignment.ToArray(), Clipped = clipped };
            aligned[r] = ResampleSinc(captures[r], ratio, length, lag);
        }
        // The stimulus fit includes the system's group delay and has a small clock-dependent bias.
        // Remove remaining fractional delay directly between repeats before the synchronous average.
        for (int r = 1; r < aligned.Length; r++)
        {
            var correction = LocalLag(aligned[0], aligned[r], length / 2, 0);
            aligned[r] = ResampleSinc(aligned[r], 1, length, correction.Delay);
            alignment[r] = alignment[r] with { RepeatFractionalShiftSamples = correction.Delay };
        }
        // All repeats are now on the same capture-rate reference grid (inverse of resampling x to each
        // measured capture clock). The nominal stimulus itself has already been sinc-resampled above.
        var deterministic = new float[length];
        for (int i = 0; i < length; i++)
        {
            double mean = 0; for (int r = 0; r < aligned.Length; r++) mean += aligned[r][i] / (double)aligned.Length;
            deterministic[i] = (float)mean;
        }
        int trim = Math.Max(captureRate / 4, ImpulseRadius * 2), end = length - trim;
        Complex[] h = FitLinear(reference, deterministic, trim, end);
        float[] linear = Predict(reference, h);
        // Welch window leakage biases an otherwise exact short linear path most visibly in the
        // lowest pink bands. Two finite-FIR least-squares residual refinements remove that bias;
        // they cannot fit an arbitrary per-bin periodic NL transfer function.
        for (int iteration = 0; iteration < 2; iteration++)
        {
            var error = new float[length]; for (int i = 0; i < length; i++) error[i] = deterministic[i] - linear[i];
            RefineShortImpulse(reference, error, h, trim, end);
            linear = Predict(reference, h);
        }
        var nonlinear = new float[length]; for (int i = 0; i < length; i++) nonlinear[i] = deterministic[i] - linear[i];
        var psdY = Spectrum(deterministic, trim, end); var psdN = new double[FftSize / 2 + 1];
        foreach (float[] repeat in aligned)
        {
            var difference = new float[length];
            for (int i = trim; i < end; i++) difference[i] = repeat[i] - deterministic[i];
            var p = Spectrum(difference, trim, end);
            for (int k = 0; k < psdN.Length; k++) psdN[k] += p[k] / (aligned.Length - 1);
        }
        var psdNl = Spectrum(nonlinear, trim, end); var psdBg = Spectrum(background, 0, background.Length);
        var (xx, yy, xy) = CrossSpectrum(reference, deterministic, trim, end);
        BroadbandPinkBand Band(double center, double lo, double hi)
        {
            double output = 0, noisePower = 0, nl = 0, room = 0, coherent = 0;
            int first = Math.Max(1, (int)Math.Ceiling(lo * FftSize / captureRate)), last = Math.Min(FftSize / 2, (int)Math.Ceiling(hi * FftSize / captureRate));
            for (int k = first; k < last; k++)
            {
                output += psdY[k]; noisePower += psdN[k]; nl += psdNl[k]; room += psdBg[k];
                double coherence = xy[k].Magnitude * xy[k].Magnitude / Math.Max(1e-30, xx[k] * yy[k]);
                coherent += psdY[k] * Math.Clamp(coherence, 0, 1);
            }
            // Sensitivity uses the worse of room and inter-repeat noise, not the reduced noise of D.
            double floor = Math.Max(room, noisePower), snr = PowerDb(output) - PowerDb(floor);
            return new(center, lo, hi, PowerDb(output), PowerDb(floor), PowerDb(nl), PowerDb(nl) - PowerDb(output),
                output > 0 ? coherent / output : 0, snr, snr >= 10);
        }
        var bands = new List<BroadbandPinkBand>();
        for (double fc = 1000 * Math.Pow(2, -17 / 3.0); fc < settings.HighHz * 1.13; fc *= Math.Pow(2, 1 / 3.0))
            bands.Add(Band(fc, fc / Math.Pow(2, 1 / 6.0), Math.Min(captureRate * .49, fc * Math.Pow(2, 1 / 6.0))));
        float[] croppedNl = nonlinear[trim..end];
        double[] hf = PinkRubBuzzAnalysis.BandPass(croppedNl, captureRate, 4000, Math.Min(12000, captureRate * .45), 8);
        int skip = Math.Min(hf.Length / 4, captureRate / 10); double second = 0, fourth = 0, peakValue = 0; int count = hf.Length - skip;
        for (int i = skip; i < hf.Length; i++) { double p = hf[i] * hf[i]; second += p; fourth += p * p; peakValue = Math.Max(peakValue, Math.Abs(hf[i])); }
        second /= Math.Max(1, count); fourth /= Math.Max(1, count);
        bool valid = !clipped && alignment.All(a => a.Correlation >= .6 && Math.Abs(a.DriftResidualSamples) <= .25);
        return new(valid, clipped ? "Clipping ngõ thu; giảm gain mic." : valid ? "Phân tích chẩn đoán; xem độ nhạy từng dải và giới hạn mô hình tuyến tính." : "Căn chỉnh/clock chưa đủ ổn định; chưa kết luận méo.")
        {
            Settings = settings, SampleRate = captureRate, PlaybackSampleRate = playbackRate, Clipped = clipped,
            Stimulus = BroadbandPinkStimulus.Describe(stimulus, playbackRate, settings), Alignment = alignment.ToArray(), Bands = bands.ToArray(),
            HighFrequency = Band(Math.Sqrt(4000 * 12000), 4000, 12000), LowMid = Band(Math.Sqrt(200 * 2000), 200, 2000),
            HighFrequencyNlKurtosis = second > 1e-30 ? fourth / (second * second) : null,
            HighFrequencyNlCrestDb = second > 1e-30 ? 20 * Math.Log10(peakValue / Math.Sqrt(second)) : null,
            NonlinearResidual = croppedNl, Captures = captures, Background = background, StimulusSamples = stimulus
        };
    }
    private static (double[] Xx, double[] Yy, Complex[] Xy) CrossSpectrum(float[] x, float[] y, int start, int end)
    {
        var xx = new double[FftSize / 2 + 1]; var yy = new double[xx.Length]; var xy = new Complex[xx.Length];
        var a = new Complex[FftSize]; var b = new Complex[FftSize]; int frames = 0;
        for (int at = start; at + FftSize <= end; at += FftSize / 2)
        {
            for (int i = 0; i < FftSize; i++) { a[i] = x[at + i] * Window[i]; b[i] = y[at + i] * Window[i]; }
            Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab); frames++;
            for (int k = 0; k < xx.Length; k++) { xx[k] += a[k].Magnitude * a[k].Magnitude; yy[k] += b[k].Magnitude * b[k].Magnitude; xy[k] += b[k] * Complex.Conjugate(a[k]); }
        }
        if (frames > 0) for (int k = 0; k < xx.Length; k++) { xx[k] /= frames; yy[k] /= frames; xy[k] /= frames; }
        return (xx, yy, xy);
    }
    private static Complex[] FitLinear(float[] x, float[] y, int start, int end)
    {
        var (xx, _, xy) = CrossSpectrum(x, y, start, end); var h = new Complex[FftSize];
        double regularization = xx.Max() * 1e-12;
        for (int k = 0; k <= FftSize / 2; k++) h[k] = xy[k] / Math.Max(regularization, xx[k]);
        for (int k = 1; k < FftSize / 2; k++) h[FftSize - k] = Complex.Conjugate(h[k]);
        Fourier.Inverse(h, FourierOptions.Matlab);
        for (int i = 65; i < FftSize - 64; i++) h[i] = 0;
        return h;
    }
    private static float[] Predict(float[] x, Complex[] impulse)
    {
        int n = PowerOfTwo(x.Length + ImpulseRadius * 2); var a = new Complex[n]; var b = new Complex[n];
        for (int i = 0; i < x.Length; i++) a[i] = x[i];
        for (int i = 0; i <= ImpulseRadius; i++) b[i] = impulse[i];
        for (int i = 1; i <= ImpulseRadius; i++) b[n - i] = impulse[FftSize - i];
        Fourier.Forward(a, FourierOptions.Matlab); Fourier.Forward(b, FourierOptions.Matlab);
        for (int i = 0; i < n; i++) a[i] *= b[i];
        Fourier.Inverse(a, FourierOptions.Matlab);
        var result = new float[x.Length]; for (int i = 0; i < result.Length; i++) result[i] = (float)a[i].Real;
        return result;
    }
    private static void RefineShortImpulse(float[] x, float[] error, Complex[] h, int start, int end)
    {
        const int radius = 64, size = radius * 2 + 1;
        var matrix = new double[size, size]; var rhs = new double[size]; var prefix = new double[x.Length + 1];
        // Exact finite-window Gram matrix. A Toeplitz approximation is biased at the window edges;
        // prefix sums for each lag difference compute all cells in O(N*taps), not O(N*taps²).
        for (int d = 0; d < size; d++)
        {
            prefix[0] = 0;
            for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + (i >= d ? (double)x[i] * x[i - d] : 0);
            for (int j = 0; j < size - d; j++)
            {
                int lag = j - radius;
                matrix[j, j + d] = matrix[j + d, j] = prefix[end - lag] - prefix[start - lag];
            }
        }
        for (int j = 0; j < size; j++)
        {
            int lag = j - radius; double sum = 0;
            for (int i = start; i < end; i++) sum += (double)error[i] * x[i - lag];
            rhs[j] = sum;
        }
        double ridge = matrix[radius, radius] * 1e-8;
        for (int i = 0; i < size; i++) matrix[i, i] += ridge;
        // Cholesky, positive-definite autocorrelation + ridge. A singular path simply skips refinement.
        for (int i = 0; i < size; i++)
            for (int j = 0; j <= i; j++)
            {
                double value = matrix[i, j]; for (int k = 0; k < j; k++) value -= matrix[i, k] * matrix[j, k];
                if (i == j) { if (value <= 0) return; matrix[i, j] = Math.Sqrt(value); }
                else matrix[i, j] = value / matrix[j, j];
            }
        for (int i = 0; i < size; i++) { for (int j = 0; j < i; j++) rhs[i] -= matrix[i, j] * rhs[j]; rhs[i] /= matrix[i, i]; }
        for (int i = size - 1; i >= 0; i--) { for (int j = i + 1; j < size; j++) rhs[i] -= matrix[j, i] * rhs[j]; rhs[i] /= matrix[i, i]; }
        for (int j = 0; j < size; j++) { int lag = j - radius; h[lag < 0 ? FftSize + lag : lag] += rhs[j]; }
    }
    private static double[] Spectrum(float[] x, int start, int end)
    {
        var power = new double[FftSize / 2 + 1]; var a = new Complex[FftSize]; int frames = 0;
        for (int at = start; at + FftSize <= end; at += FftSize / 2)
        {
            for (int i = 0; i < FftSize; i++) a[i] = x[at + i] * Window[i];
            Fourier.Forward(a, FourierOptions.Matlab); frames++;
            for (int k = 0; k < power.Length; k++) power[k] += a[k].Magnitude * a[k].Magnitude;
        }
        double scale = 2 / (Math.Max(1, frames) * FftSize * Window.Sum(v => v * v));
        for (int k = 0; k < power.Length; k++) power[k] *= scale;
        return power;
    }
}
