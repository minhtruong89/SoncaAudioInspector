using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoncaAudioInspector;

public sealed partial class C360AudioBench
{
    /// <summary>IO009: closes the loop measurement mic → app gain → MIC IN → speaker → mic, ramps the loop gain until a howl,
    /// once with FBX off and once with FBX on (initial FBX restored). The ramp stops and mutes the instant a howl is confirmed.</summary>
    private async Task<C360ElectronicResult> CaptureFeedbackGainAsync(C360ElectronicSetup setup, CancellationToken token)
    {
        var settings = setup.TestConfiguration?.FeedbackGain ?? new FeedbackGainSettings(); settings.Validate();
        var state = await ReadAsync(token);
        if (!state.TryGetValue(SoncaBleMixerProfile.InputCapabilities, out ushort flags) || !SoncaBleMixerProfile.SupportsOption(flags, SoncaMixerOption.MicFbx))
            return new(C360ElectronicStage.MicFeedbackGain, "INVALID", "Chưa đọc được tùy chọn Mic FBX từ loa.");
        bool initial = (flags & SoncaBleMixerProfile.OptionMask(SoncaMixerOption.MicFbx)) != 0;
        string identity = DeviceIdentity;
        async Task SetFbx(bool enabled, CancellationToken cancellation)
        {
            if (DeviceIdentity != identity || !await ble.SetOptionAsync(SoncaMixerOption.MicFbx, enabled)) throw new InvalidOperationException("Chưa xác nhận đổi Mic FBX từ loa.");
            var read = await ReadAsync(cancellation);
            if (((read[SoncaBleMixerProfile.InputCapabilities] & 1) != 0) != enabled) throw new InvalidOperationException("Mic FBX đọc lại chưa khớp.");
        }
        engine.StopAllAudio();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDevice(setup.AnalogPlaybackId);
            using var mic = enumerator.GetDevice(setup.LineOutCaptureId);
            if (render.State != DeviceState.Active || mic.State != DeviceState.Active) throw new InvalidOperationException("Kiểm tra ngõ MIC IN (sound card) và mic đo.");
            await SetFbx(false, token);
            var off = await RunFeedbackLoopAsync(mic, render, false, settings, token);
            await Task.Delay(1500, token); // let the room and the speaker's suppressor settle
            await SetFbx(true, token);
            var on = await RunFeedbackLoopAsync(mic, render, true, settings, token);
            var result = new FeedbackGainResult(off, on, settings.MaximumGainDb);
            ResultPersistencePolicy.CreateDirectory(directory);
            ResultPersistencePolicy.WriteAllText(Path.Combine(directory, "MicFeedbackGain-result.json"), JsonSerializer.Serialize(new { settings, result, result.AddedDb }, Json));
            return new(C360ElectronicStage.MicFeedbackGain, FeedbackGainDiagnostics.Status(result, settings), FeedbackGainDiagnostics.Summary(result, settings)) { FeedbackGain = result };
        }
        finally
        {
            try { await SetFbx(initial, CancellationToken.None); } catch (Exception ex) when (ex is InvalidOperationException) { }
        }
    }

    private static async Task<FeedbackGainRun> RunFeedbackLoopAsync(MMDevice micDevice, MMDevice renderDevice, bool fbx, FeedbackGainSettings settings, CancellationToken token)
    {
        using var capture = new WasapiCapture(micDevice, true, 20);
        var source = capture.WaveFormat;
        var mix = renderDevice.AudioClient.MixFormat;
        var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels))
            { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(400) };
        using var output = new WasapiOut(renderDevice, AudioClientShareMode.Shared, true, 30);
        var detector = new HowlDetector(source.SampleRate, settings);
        var steps = new List<FeedbackGainStep>(); var gate = new object();
        var block = new List<float>(HowlDetector.BlockSize * 2);
        double gainDb = settings.StartGainDb, linear = 0, position = 0; float previous = 0;
        double? howlAt = null; double howlHz = 0, peakDbFs = -200;
        double ratio = (double)source.SampleRate / mix.SampleRate;
        capture.DataAvailable += (_, e) =>
        {
            var mono = ToMono(e.Buffer, e.BytesRecorded, source);
            lock (gate)
            {
                // Linear resampling to the render rate, gain, hard limit at −6 dBFS, all render channels.
                var rendered = new List<float>();
                while (position < mono.Length - 1)
                {
                    int i = (int)Math.Floor(position); double f = position - i;
                    double a = i < 0 ? previous : mono[i], b = mono[i + 1];
                    float v = (float)Math.Clamp((a + (b - a) * f) * linear, -.5, .5);
                    for (int c = 0; c < mix.Channels; c++) rendered.Add(v);
                    position += ratio;
                }
                position -= mono.Length; previous = mono.Length > 0 ? mono[^1] : previous;
                var bytes = new byte[rendered.Count * 4]; Buffer.BlockCopy(rendered.ToArray(), 0, bytes, 0, bytes.Length);
                buffer.AddSamples(bytes, 0, bytes.Length);
                block.AddRange(mono);
                while (block.Count >= HowlDetector.BlockSize)
                {
                    var current = block.GetRange(0, HowlDetector.BlockSize).ToArray(); block.RemoveRange(0, HowlDetector.BlockSize);
                    bool howl = detector.Push(current, gainDb);
                    steps.Add(detector.Last); peakDbFs = Math.Max(peakDbFs, detector.Last.LevelDbFs);
                    if (howl && howlAt == null) { howlAt = gainDb; howlHz = detector.Last.PeakHz; linear = 0; buffer.ClearBuffer(); }
                }
            }
        };
        output.Init(buffer); output.Play(); capture.StartRecording();
        try
        {
            for (double g = settings.StartGainDb; g <= settings.MaximumGainDb + 1e-9; g += settings.StepDb)
            {
                lock (gate) { if (howlAt != null) break; gainDb = g; linear = Math.Pow(10, g / 20); }
                await Task.Delay(TimeSpan.FromSeconds(settings.StepSeconds), token);
            }
            lock (gate) { if (howlAt == null) linear = Math.Pow(10, gainDb / 20); }
            await Task.Delay(300, token); // detection of the last step
        }
        finally
        {
            lock (gate) { linear = 0; buffer.ClearBuffer(); }
            capture.StopRecording(); output.Stop();
        }
        lock (gate) return new(fbx, howlAt, howlHz, peakDbFs, steps.ToArray());
    }

    private static float[] ToMono(byte[] data, int count, WaveFormat format)
    {
        int channels = Math.Max(1, format.Channels), bytes = format.BitsPerSample / 8, frames = count / (bytes * channels);
        var mono = new float[frames];
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytes;
                sum += isFloat && bytes == 4 ? BitConverter.ToSingle(data, o)
                    : bytes == 2 ? BitConverter.ToInt16(data, o) / 32768.0
                    : bytes == 3 ? ((data[o] | data[o + 1] << 8 | (sbyte)data[o + 2] << 16) / 8388608.0)
                    : BitConverter.ToInt32(data, o) / 2147483648.0;
            }
            mono[f] = (float)(sum / channels);
        }
        return mono;
    }
}
