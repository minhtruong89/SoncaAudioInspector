using SoncaAudioInspector;

const int rate = 48000;
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
float[] Signal(double frequency, double amplitude = 0.2, double noise = 0, double second = 0)
{
    var random = new Random(17);
    return Enumerable.Range(0, rate * 4).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate + 0.43)
        + noise * (random.NextDouble() * 2 - 1) + second * Math.Sin(2 * Math.PI * (frequency + 5) * i / rate))).ToArray();
}
FeedbackTone[] Capture(double shift, double scale = 1) => FeedbackShiftMeasurement.Frequencies
    .Select(f => FeedbackShiftMeasurement.Analyze(Signal(f * scale + shift), rate, f)).ToArray();
var off = Capture(0, 1.0001);
Check(off.All(x => x.IsValid), "unshifted capture with clock error accepted");
var on = Capture(5, 1.0001);
Check(on.All(x => x.IsValid), "shifted tones locked");
Check(Math.Abs(on[1].MeasuredHz - 1005.1) < 0.03, "sub-Hz frequency estimate");
Check(FeedbackShiftMeasurement.Evaluate(on, 5, 0.5).Code == "PASS", "known +5 Hz shift");
Check(FeedbackShiftMeasurement.Evaluate(on, null, 0.5).Code == "ACTIVE", "unknown specification never PASS");
Check(FeedbackShiftMeasurement.Evaluate(Capture(-5, 1.0001), -5, 0.5).Code == "PASS", "negative shift");
Check(FeedbackShiftMeasurement.Evaluate(Capture(-5, 1.0001), 5, 0.5).Code == "FAIL", "wrong direction");
Check(FeedbackShiftMeasurement.Evaluate(off, 5, 0.5).Code == "FAIL_NO_SHIFT", "bypass or dead shifter");
Check(FeedbackShiftMeasurement.Evaluate(Capture(0, 1.0003), 5, 0.5).Code == "FAIL_NO_SHIFT", "clock drift is not fixed shift");
Check(FeedbackShiftMeasurement.Evaluate(Capture(0, 1.001), null, 0.5).Code == "INCONCLUSIVE", "large clock change rejected");
Check(FeedbackShiftMeasurement.Evaluate(on.Take(2).ToArray(), 5, 0.5).Code == "INVALID", "incomplete capture rejected");
Check(FeedbackShiftMeasurement.Evaluate(on.Reverse().ToArray(), 5, 0.5).Code == "INVALID", "mismatched tone order rejected");
Check(FeedbackShiftMeasurement.Evaluate(on, 5, 5).Code == "INVALID", "limits cannot include zero shift");
Check(!FeedbackShiftMeasurement.Analyze(new float[rate * 4], rate, 1000).IsValid, "silence rejected");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000, 0.0001), rate, 1000).IsValid, "weak capture rejected");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000, 1), rate, 1000).IsValid, "clipping rejected");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1234), rate, 1000).IsValid, "unrelated tone rejected");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000, 0.05, 0.2), rate, 1000).IsValid, "low SNR rejected");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000, 0.2, 0, 0.2), rate, 1000).IsValid, "dry/wet mixture rejected");
var unstable = Signal(1000);
for (int i = rate * 2; i < unstable.Length; i++) unstable[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 1002 * i / rate));
Check(!FeedbackShiftMeasurement.Analyze(unstable, rate, 1000).IsValid, "time-varying shift rejected");
var bad = Signal(1000); bad[20] = float.NaN;
Check(!FeedbackShiftMeasurement.Analyze(bad, rate, 1000).IsValid, "nonfinite capture rejected");
var uneven = on.ToArray(); uneven[1] = uneven[1] with { MeasuredHz = uneven[1].MeasuredHz + 3 };
Check(FeedbackShiftMeasurement.Evaluate(uneven, 5, 0.5).Code == "INCONCLUSIVE", "frequency-dependent deviation rejected");
Check(FeedbackShiftMeasurement.Evaluate(Capture(5.49, 1.0001), 5, 0.5).Code == "INCONCLUSIVE", "inside-limit edge is not unconditional PASS");
Check(FeedbackShiftMeasurement.Evaluate(Capture(5.51, 1.0001), 5, 0.5).Code == "INCONCLUSIVE", "outside-limit edge requires repeat");
Check(FeedbackShiftMeasurement.Evaluate(Capture(0.72, 1.0001), null, 0.5).Code == "INCONCLUSIVE", "detection edge requires repeat");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000).Take(rate * 3).ToArray(), rate, 1000).IsValid, "truncated capture cannot count as completed");
Check(!FeedbackShiftMeasurement.Analyze(Signal(1000, 0.2, 0, 0.04), rate, 1000).IsValid, "smaller dry/wet leak rejected");
var corrupt = on.ToArray(); corrupt[0] = corrupt[0] with { Purity = double.NaN };
Check(FeedbackShiftMeasurement.Evaluate(corrupt, 5, 0.5).Code == "INVALID", "nonfinite quality metadata rejected");

NAudio.Wave.WaveFormat Extended(int bits, bool floating)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0xFFFE); writer.Write((ushort)2); writer.Write(rate);
    writer.Write(rate * 2 * bits / 8); writer.Write((ushort)(2 * bits / 8)); writer.Write((ushort)bits);
    writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(3);
    writer.Write(new Guid(floating ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
    byte[] bytes = stream.ToArray();
    IntPtr pointer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
    try
    {
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return NAudio.Wave.WaveFormat.MarshalFromPtr(pointer);
    }
    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer); }
}
var pcm32 = BitConverter.GetBytes(1073741824).Concat(BitConverter.GetBytes(-1073741824)).ToArray();
var decoded = CaptureSampleDecoder.Decode(pcm32, pcm32.Length, Extended(32, false), 1, 0);
Check(Math.Abs(decoded[0] - 0.5) < 1e-6, "extensible PCM32 read as integer, not float");
Check(CaptureSampleDecoder.Decode(pcm32, pcm32.Length, Extended(32, false), 1, 1)[0] == -0.5f, "right channel selected before averaging");
Check(CaptureSampleDecoder.Decode(pcm32, pcm32.Length, Extended(32, false), 1)[0] == 0, "legacy mono mix preserved");
var floats = BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-0.75f)).ToArray();
Check(CaptureSampleDecoder.Decode(floats, floats.Length, Extended(32, true), 1, 1)[0] == -0.75f, "extensible float decode");
Check(CaptureSampleDecoder.Decode(new byte[] { 0, 64, 0, 192 }, 4, new NAudio.Wave.WaveFormat(rate, 16, 2), 1, 0)[0] == 0.5f, "PCM16 decode");
Check(CaptureSampleDecoder.Decode(new byte[] { 0, 0, 64, 0, 0, 192 }, 6, new NAudio.Wave.WaveFormat(rate, 24, 2), 1, 1)[0] == -0.5f, "PCM24 signed decode");
bool invalidChannel = false;
try { CaptureSampleDecoder.Decode(pcm32, pcm32.Length, Extended(32, false), 1, 2); } catch (ArgumentException) { invalidChannel = true; }
Check(invalidChannel, "invalid input channel rejected");
Check(FeedbackShiftMeasurement.Evaluate(off, null, 0.5).Code == "FAIL_NO_SHIFT", "missing shifter is a fault even without target shift");
var silentRows = FeedbackShiftMeasurement.Frequencies.Select(f => FeedbackShiftMeasurement.Analyze(new float[rate * 4], rate, f)).ToArray();
Check(FeedbackShiftMeasurement.Evaluate(silentRows, null, 0.5).Code == "INVALID", "silence is not missing-shifter fault");
Console.WriteLine($"{checks} checks passed.");
