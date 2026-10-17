using SoncaAudioInspector;

internal static class ImpulsePlotChecks
{
    public static void Run()
    {
        foreach (int rate in new[] { 44100, 48000 })
        {
            var samples = new double[rate * 3];
            samples[rate] = 1; samples[rate + rate / 5] = .5; samples[rate + rate * 2 / 5] = .25;
            samples[rate / 2] = .01;
            var data = ImpulsePlotAnalysis.Build(samples, rate, rate);
            if (Math.Abs(data.Impulse[0].TimeSeconds + 1) > .001 || data.Impulse.MaxBy(p => Math.Abs(p.Value))!.TimeSeconds != 0)
                throw new Exception("Full pre-arrival window or direct t=0 was lost.");
            var echo = data.Impulse.First(p => Math.Abs(p.TimeSeconds - .2) < .00001);
            if (Math.Abs(20 * Math.Log10(Math.Abs(echo.Value)) + 6.020599913) > 1e-6)
                throw new Exception("Display decimation lost the echo peak or its real relative level.");
            if (Math.Abs(data.ClarityC20Db - 10 * Math.Log10(1 / .3125)) > 1e-8
                || Math.Abs(data.CentreTimeSeconds - .075 / 1.3125) > 1e-8)
                throw new Exception("C20 / centre time includes pre-arrival energy or has the wrong time reference.");
            var quiet = ImpulsePlotAnalysis.Build(samples.Select(v => v * .1).ToArray(), rate, rate);
            if (Math.Abs(quiet.PeakTransferGainDb - data.PeakTransferGainDb + 20) > 1e-9
                || quiet.Envelope.Zip(data.Envelope).Any(p => Math.Abs(p.First.Value - p.Second.Value) > 1e-6))
                throw new Exception("View normalisation lost its gain metadata or modified the relative envelope.");
            // A constant-amplitude sinusoid has a constant analytic envelope,
            // including at real-signal zero crossings.
            var tone = Enumerable.Range(0, rate).Select(i => Math.Sin(2 * Math.PI * 1000 * i / rate)).ToArray();
            double[] envelope = ImpulsePlotAnalysis.EnvelopeDb(tone);
            int crossing = rate / 2;
            if (Math.Abs(tone[crossing]) > 1e-9 || Math.Abs(envelope[crossing]) > .05
                || Enumerable.Range(rate / 4, rate / 2).Max(i => Math.Abs(envelope[i])) > .06)
                throw new Exception("ETC is not the Hilbert envelope (or has excessive interior ripple).");
            Console.WriteLine($"PASS {rate} Hz: full pre-IR, exact echo peak, analytic ETC, C20/TS, gain-independent display.");
        }
    }
}
