using SoncaAudioInspector;
using System.Reflection;

const int sr = 48000;
float[] Tone(double f, double amp = .2) => Enumerable.Range(0, sr).Select(i => (float)(amp * Math.Sin(2 * Math.PI * f * i / sr))).ToArray();
foreach (var (actual, expected) in new[] { (12000.0,12000.0), (1003.21,1000.0), (6997.3,7000.0) })
{
    var t = AdvancedAudioMeasurement.AnalyzeTone(Tone(actual), sr, expected);
    Console.WriteLine($"TONE actual={actual} expected={expected} found={t.FundamentalFrequencyHz:F6} valid={t.IsValid} THD={t.ThdPercent:F6}% THDN={t.ThdNPercent:F4}% harmonics={t.Harmonics.Count}");
}
var rng = new Random(934);
var noise = Enumerable.Range(0, sr * 4).Select(_ => (float)((rng.NextDouble() * 2 - 1) * .1)).ToArray();
var pink = StandardAcousticMeasurement.AnalyzePinkNoise(noise, sr);
var quiet = StandardAcousticMeasurement.AnalyzePinkNoise(noise.Select(x=>x*.1f).ToArray(), sr);
Console.WriteLine($"PINK wrong-stimulus-white-noise validity={pink.Validity}; identical shape -20dB comparison={StandardAcousticMeasurement.ComparePinkNoiseWithGolden(quiet,pink).Status}; level delta={quiet.SignalLevelDbFs-pink.SignalLevelDbFs:F2}dB");
var corrupt = noise.ToArray(); corrupt[100] = float.NaN;
Console.WriteLine($"PINK NaN validity={StandardAcousticMeasurement.AnalyzePinkNoise(corrupt,sr).Validity}");
Console.WriteLine($"PINK NaN golden comparison={StandardAcousticMeasurement.ComparePinkNoiseWithGolden(StandardAcousticMeasurement.AnalyzePinkNoise(corrupt,sr),pink).Status}");
Console.WriteLine($"RTA NaN validity={AdvancedAudioMeasurement.AnalyzeRtaSpectrum(corrupt,sr).Validity}");
var imdNoise = AdvancedAudioMeasurement.AnalyzeSmpteImd(noise, sr);
Console.WriteLine($"IMD white noise validity={imdNoise.Validity} IMD={imdNoise.ImdPercent:F2}%");
var clipped = Tone(1000, 2).Select(x=>Math.Clamp(x,-1f,1f)*.2f).ToArray();
var clippedTone=AdvancedAudioMeasurement.AnalyzeTone(clipped,sr,1000);
Console.WriteLine($"ADC CLIP after software gain .2 peak={clippedTone.PeakSample:F2} clippedPercent={clippedTone.ClippedSamplePercent:F2} toneValid={clippedTone.IsValid}");
var fit = typeof(StandardAcousticMeasurement).GetMethod("FitDecay", BindingFlags.NonPublic|BindingFlags.Static)!;
var partialCurve = Enumerable.Range(0, 101).Select(i=>new DecayCurvePoint(i*.001,-5-i*.05)).ToArray();
var partial = (DecayEstimate)fit.Invoke(null,new object[]{partialCurve,"T20",-5.0,-25.0})!;
Console.WriteLine($"RT60 partial curve -5..-10dB requested T20 -5..-25dB valid={partial.IsValid} RT60={partial.Rt60Seconds:F3} R2={partial.RSquared:F3}");
