using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SoncaAudioInspector;

public sealed record ImpulsePlotData(
    IReadOnlyList<TimeDomainPoint> Impulse,
    IReadOnlyList<TimeDomainPoint> Envelope,
    double PeakTransferGainDb,
    double ClarityC20Db,
    double CentreTimeSeconds);

/// <summary>Display data from the full ungated IR, independent of FEQ normalisation and FDW.</summary>
public static class ImpulsePlotAnalysis
{
    public static ImpulsePlotData Build(IReadOnlyList<double> impulse, int sampleRate, int directIndex)
    {
        if (sampleRate <= 0 || directIndex < 0 || directIndex >= impulse.Count || impulse.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("IR hiển thị không hợp lệ.");
        double peak = impulse.Select(Math.Abs).DefaultIfEmpty(0).Max();
        double[] envelope = EnvelopeDb(impulse);
        int stride = Math.Max(1, (int)Math.Ceiling(impulse.Count / 16000.0));
        var irPoints = new List<TimeDomainPoint>();
        var etcPoints = new List<TimeDomainPoint>();
        for (int start = 0; start < impulse.Count; start += stride)
        {
            int end = Math.Min(impulse.Count, start + stride), irIndex = start, etcIndex = start;
            for (int index = start + 1; index < end; index++)
            {
                if (Math.Abs(impulse[index]) > Math.Abs(impulse[irIndex])) irIndex = index;
                if (envelope[index] > envelope[etcIndex]) etcIndex = index;
            }
            irPoints.Add(new((irIndex - directIndex) / (double)sampleRate, peak > 0 ? impulse[irIndex] / peak : 0));
            etcPoints.Add(new((etcIndex - directIndex) / (double)sampleRate, envelope[etcIndex]));
        }
        double early = 0, late = 0, weighted = 0;
        for (int index = directIndex; index < impulse.Count; index++)
        {
            double time = (index - directIndex) / (double)sampleRate, energy = impulse[index] * impulse[index];
            if (time < .020) early += energy; else late += energy;
            weighted += time * energy;
        }
        double total = early + late;
        return new(irPoints, etcPoints, peak > 0 ? 20 * Math.Log10(peak) : double.NaN,
            early > 0 && late > 0 ? 10 * Math.Log10(early / late) : double.NaN,
            total > 0 ? weighted / total : double.NaN);
    }

    // Magnitude of the analytic signal, not abs(IR). Zero padding avoids joining
    // the end of the captured tail directly to the pre-arrival samples.
    public static double[] EnvelopeDb(IReadOnlyList<double> impulse)
    {
        if (impulse.Count == 0) return Array.Empty<double>();
        int fftSize = 1;
        while (fftSize < impulse.Count * 2) fftSize <<= 1;
        var spectrum = new Complex[fftSize];
        double peak = 0;
        for (int index = 0; index < impulse.Count; index++)
        {
            spectrum[index] = impulse[index];
            peak = Math.Max(peak, Math.Abs(impulse[index]));
        }
        Fourier.Forward(spectrum, FourierOptions.Matlab);
        for (int bin = 1; bin < fftSize / 2; bin++) spectrum[bin] *= 2;
        Array.Clear(spectrum, fftSize / 2 + 1, fftSize / 2 - 1);
        Fourier.Inverse(spectrum, FourierOptions.Matlab);
        return Enumerable.Range(0, impulse.Count).Select(index =>
            20 * Math.Log10(Math.Max(1e-12, peak > 0 ? spectrum[index].Magnitude / peak : 0))).ToArray();
    }
}
