using SoncaAudioInspector;
using System.Reflection;

// Read-only numerical audit of the current analyzers. No devices or app settings are touched.
const int sr = 48000;
float[] Tone(double f, double amplitude = .2, int seconds = 1) => Enumerable.Range(0, sr * seconds)
    .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * f * i / sr))).ToArray();
var rng = new Random(934);
var noise = Enumerable.Range(0, sr * 4).Select(_ => (float)((rng.NextDouble() * 2 - 1) * .1)).ToArray();
var corrupt = noise.ToArray(); corrupt[100] = float.NaN;
Console.WriteLine($"INVALID input: pink NaN={StandardAcousticMeasurement.AnalyzePinkNoise(corrupt, sr).Validity}; RTA NaN={AdvancedAudioMeasurement.AnalyzeRtaSpectrum(corrupt, sr).Validity}; IMD NaN={AdvancedAudioMeasurement.AnalyzeSmpteImd(corrupt, sr).Validity}");
var imdNoise = AdvancedAudioMeasurement.AnalyzeSmpteImd(noise, sr);
Console.WriteLine($"WRONG stimulus: white noise IMD result={imdNoise.Validity}, value={imdNoise.ImdPercent:F2}%");

var h2 = Tone(1000);
for (int i = 0; i < h2.Length; i++) h2[i] += (float)(.002 * Math.Sin(2 * Math.PI * 2000 * i / sr));
var quality = AdvancedAudioMeasurement.AnalyzeTone(h2, sr, 1000);
Console.WriteLine($"HARMONIC probe: expected THD=1%, SFDR=40 dBc (H2 largest spur); actual THD={quality.ThdPercent:F5}%, SFDR={quality.SfdrDb:F2} dB, residual spur={quality.LargestSpurFrequencyHz:F2} Hz");

foreach (double position in new[] { .05, .50, .95 })
{
    float[] pulse = Tone(80);
    int begin = (int)(position * sr), length = sr / 200; // Same 5 ms, 2.5 kHz burst, at three locations.
    for (int j = 0; j < length; j++)
        pulse[begin + j] += (float)(.03 * Math.Sin(2 * Math.PI * 2500 * j / sr) * Math.Sin(Math.PI * j / (length - 1)));
    var rub = AdvancedAudioMeasurement.AnalyzeRubBuzz(pulse, sr, 80, 500, 10000);
    Console.WriteLine($"TRANSIENT same burst at {position:F2}s: valid={rub.IsValid}, RubBuzz={rub.RubBuzzPercent:F6}%, residual peak={rub.ResidualPeakDbc:F2} dBc, events={rub.TransientEvents.Count}, transient peak={rub.TransientEvents.Select(e => e.PeakDbc).DefaultIfEmpty(-180).Max():F2} dBc");
}

var methods = BindingFlags.Static | BindingFlags.NonPublic;
var views = typeof(StandardAcousticMeasurement).GetMethod("BuildTimeDomainViews", methods)!;
double[] impulse = new double[sr * 3 + 64]; impulse[64] = 1;
var result = ((IReadOnlyList<TimeDomainPoint>, IReadOnlyList<TimeDomainPoint>, IReadOnlyList<TimeDomainPoint>))views.Invoke(null, new object[] { impulse, sr, 64 })!;
Console.WriteLine($"TIME VIEWS: input peak=1 at t=0; returned impulse peak={result.Item1.Max(p => Math.Abs(p.Value)):F3}, ETC peak={result.Item3.Max(p => p.Value):F1} dB");

var edc = typeof(StandardAcousticMeasurement).GetMethod("BuildEnergyDecayCurve", methods)!;
var fit = typeof(StandardAcousticMeasurement).GetMethod("FitDecay", methods)!;
foreach ((double rt, double seconds) in new[] { (1.2, 3.0), (12.0, 3.0) })
{
    // Exactly exponential squared energy, no random background; known amplitude decay of 60 dB per rt.
    double[] decay = Enumerable.Range(0, (int)(seconds * sr)).Select(i => Math.Pow(10, -3.0 * i / (sr * rt))).ToArray();
    var curve = (IReadOnlyList<DecayCurvePoint>)edc.Invoke(null, new object[] { decay, sr, 0 })!;
    var fitted = (DecayEstimate)fit.Invoke(null, new object[] { curve, "T20", -5.0, -25.0 })!;
    Console.WriteLine($"DECAY known RT60={rt:F1}s, captured={seconds:F1}s: T20={fitted.Rt60Seconds:F3}s, valid={fitted.IsValid}, R2={fitted.RSquared:F4}");
}

var settings = new LogSweepSettings(DurationSeconds: .8, Amplitude: .2);
float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
float[] recorded = new float[excitation.Length + sr * 3];
int direct = sr / 50, reflection = sr / 20;
for (int i = 0; i < excitation.Length; i++)
{
    recorded[i + direct] += excitation[i] * .25f;
    recorded[i + reflection] -= excitation[i] * .50f;
}
var sweep = StandardAcousticMeasurement.AnalyzeLogSweep(excitation, recorded, settings);
Console.WriteLine($"ARRIVAL positive direct=20ms, stronger inverted reflection=50ms: reported={sweep.DirectArrivalMs:F3}ms, polarity={sweep.Polarity}, validity={sweep.Validity}, directPathClaim={sweep.HasPlausibleDirectArrival}");

var nonlinearRecorded = new float[excitation.Length + sr / 10];
for (int i = 0; i < excitation.Length; i++)
    nonlinearRecorded[i + sr / 50] = (float)(excitation[i] + 0.5 * excitation[i] * excitation[i]);
var nonlinearSweep = StandardAcousticMeasurement.AnalyzeLogSweep(excitation, nonlinearRecorded, settings);
var harmonicAt1k = nonlinearSweep.SweepHarmonics.MinBy(point => Math.Abs(point.FundamentalFrequencyHz - 1000))!;
Console.WriteLine($"ESS nonlinear y=x+0.5x^2: H2@1k={harmonicAt1k.H2Dbc:F2} dBc, H3={harmonicAt1k.H3Dbc:F2} dBc, THD={harmonicAt1k.ThdPercent:F3}%, points={nonlinearSweep.SweepHarmonics.Count}");
