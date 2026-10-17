using System.Numerics;
using SoncaAudioInspector;

internal static class RewRegressionChecks
{
    public static void Run()
    {
        foreach (int rate in new[] { 44100, 48000 })
        foreach (int length in new[] { 524288, 1048576 })
        {
            var settings = new LogSweepSettings(rate, 18, Math.Min(22000, rate * .47), length / (double)rate, .2);
            if (StandardAcousticMeasurement.GenerateLogSweep(settings).Length != length)
                throw new Exception($"Length {length} at {rate} Hz changed.");
            Console.WriteLine($"PASS Length {length}: {length / (double)rate:F9} seconds at {rate} Hz.");
        }

        // A delayed nonlinear path is a known 1% H2 at 1 kHz. It must survive
        // the harmonic IR gate, rather than being discarded by a 20 ms cap.
        foreach (int length in new[] { 524288, 1048576 })
        {
            const int rate = 48000;
            var settings = new LogSweepSettings(rate, 18, 22000, length / (double)rate, .2,
                PointsPerOctave: 48, DistortionEvaluationFrequenciesHz: new[] { 80.0, 1000.0, 4000.0 });
            float[] sweep = StandardAcousticMeasurement.GenerateLogSweep(settings);
            var capture = new float[length + rate];
            for (int i = 0; i < sweep.Length; i++)
            {
                capture[1200 + i] += sweep[i];
                capture[1200 + rate / 20 + i] += .1f * sweep[i] * sweep[i];
            }
            var result = StandardAcousticMeasurement.AnalyzeLogSweep(sweep, capture, settings);
            var point = result.SweepHarmonics.Single(p => p.FundamentalFrequencyHz == 1000);
            double h2 = 100 * Math.Pow(10, point.H2Dbc / 20);
            Console.WriteLine($"Length {length}: delayed H2 = {h2:F6}% (expected 1%), THD = {point.ThdPercent:F6}%.");
            if (Math.Abs(h2 - 1) > .08)
                throw new Exception($"Delayed harmonic energy lost for {length}: {h2:F6}%.");
            double expectedDbFs = 20 * Math.Log10(.2 / Math.Sqrt(2));
            if (Math.Abs(point.FundamentalLevelDb - expectedDbFs) > .1)
                throw new Exception($"Fundamental level depends on Length: {point.FundamentalLevelDb:F3} vs {expectedDbFs:F3} dBFS.");
            GC.Collect();
        }

        // Only harmonics inside BOTH the measured sweep and 95% Nyquist count.
        var transfers = new Complex[10][];
        for (int order = 1; order <= 9; order++)
            transfers[order] = Enumerable.Repeat(new Complex(order == 1 ? 1 : .01, 0), 65536).ToArray();
        var narrow = new LogSweepSettings(48000, 20, 5000, 2, .2,
            DistortionEvaluationFrequenciesHz: new[] { 2000.0 });
        var at2k = StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(transfers, narrow)
            .Single(p => p.FundamentalFrequencyHz == 2000);
        if (!double.IsNaN(at2k.H3Dbc) || Math.Abs(at2k.ThdPercent - 1) > 1e-6)
            throw new Exception($"Out-of-band harmonics entered THD: H3={at2k.H3Dbc}, THD={at2k.ThdPercent}.");
        Console.WriteLine("PASS measured harmonic bandwidth and per-frequency THD energy sum.");
        var calibration = new MicrophoneCalibration();
        calibration.Points.Add((1000, 0, 0)); calibration.Points.Add((2000, 6.020599913, 0));
        var wide = narrow with { EndFrequencyHz = 20000, DistortionEvaluationFrequenciesHz = new[] { 1000.0 } };
        var raw = StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(transfers, wide, calibration).Single(p => p.FundamentalFrequencyHz == 1000);
        var calibrated = StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(transfers, wide with { ApplyDistortionCalibration = true }, calibration).Single(p => p.FundamentalFrequencyHz == 1000);
        if (Math.Abs(raw.H2Dbc + 40) > 1e-8 || Math.Abs(calibrated.H2Dbc + 46.020599913) > 1e-6)
            throw new Exception("Distortion Use cal files does not match its selected state.");
        for (int bin = 0; bin < transfers[1].Length; bin++)
            if (bin * 48000.0 / transfers[1].Length > 1500) transfers[1][bin] = new Complex(2, 0);
        var fundamentalReference = StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(transfers, wide).Single(p => p.FundamentalFrequencyHz == 1000);
        var harmonicReference = StandardAcousticMeasurement.BuildSweepHarmonicPointsFromTransfers(transfers, wide with { UseHarmonicFrequencyAsDistortionReference = true }).Single(p => p.FundamentalFrequencyHz == 1000);
        if (Math.Abs(fundamentalReference.H2Dbc + 40) > 1e-8 || Math.Abs(harmonicReference.H2Dbc + 46.020599913) > 1e-6)
            throw new Exception("Distortion harmonic-frequency reference is incorrect.");
        Console.WriteLine("PASS independent distortion calibration and harmonic-frequency reference.");
    }
}
