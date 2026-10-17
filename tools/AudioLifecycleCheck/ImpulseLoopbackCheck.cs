using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class ImpulseLoopbackCheck
{
    public static void SyncStandardRates()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
        try
        {
            foreach (var device in devices.Where(d => d.DataFlow == DataFlow.Capture || FastTrackDeviceSetup.IsFastTrack(d)))
            {
                double volume = device.AudioEndpointVolume.MasterVolumeLevelScalar; bool mute = device.AudioEndpointVolume.Mute;
                int before = FastTrackDeviceSetup.ReadMixSampleRate(device.ID);
                bool success = AudioSampleRateSetup.EnsureStandard(device, out string message);
                int after = FastTrackDeviceSetup.ReadMixSampleRate(device.ID);
                if (device.AudioEndpointVolume.MasterVolumeLevelScalar != volume || device.AudioEndpointVolume.Mute != mute)
                    throw new Exception("Endpoint volume/mute changed during sync.");
                Console.WriteLine(JsonSerializer.Serialize(new { device.FriendlyName, flow = device.DataFlow.ToString(), before, after, success, message, volume, mute }));
                if (!success || after != 44100) throw new Exception("44.1 kHz sync/readback failed.");
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    public static void RestoreCaptureRate(int sampleRate)
    {
        using var enumerator = new MMDeviceEnumerator();
        var inputs = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        try
        {
            var input = inputs.Single(FastTrackDeviceSetup.IsFastTrack);
            typeof(AudioEngine).Assembly.GetType("SoncaAudioInspector.WindowsEndpointPolicy")!
                .GetMethod("SetSampleRate", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { input.ID, sampleRate });
            int actual = FastTrackDeviceSetup.ReadMixSampleRate(input.ID);
            if (actual != sampleRate) throw new Exception("Capture restore readback failed.");
            Console.WriteLine($"RESTORED {input.FriendlyName} capture {actual} Hz; no volume/mute/gain writes.");
        }
        finally { foreach (var input in inputs) input.Dispose(); }
    }

    public static async Task RunAsync(string directory, int playbackRate = 48000, bool alignCaptureRate = false)
    {
        Directory.CreateDirectory(directory);
        var options = new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        using var engine = new AudioEngine();
        var outputs = engine.GetPlaybackDevices(); var inputs = engine.GetRecordingDevices();
        MMDevice? rateAdjustedInput = null; int originalCaptureRate = 0;
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "available-endpoints", outputs = outputs.Select(d => d.FriendlyName), inputs = inputs.Select(d => d.FriendlyName) }));
            var analogOutputs = outputs.Where(d => d.FriendlyName.Contains("analog", StringComparison.OrdinalIgnoreCase)).ToArray();
            var analogInputs = inputs.Where(d => d.FriendlyName.Contains("analog", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (analogOutputs.Length == 0 || analogInputs.Length == 0)
            {
                using var enumerator = new MMDeviceEnumerator();
                var known = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All).ToList();
                try { Console.WriteLine(JsonSerializer.Serialize(new { stage = "analog-unavailable-no-stimulus", known = known.Where(d => d.FriendlyName.Contains("analog", StringComparison.OrdinalIgnoreCase) || d.FriendlyName.Contains("FastTrack", StringComparison.OrdinalIgnoreCase)).Select(d => new { name = d.FriendlyName, state = d.State.ToString() }) })); }
                finally { foreach (var device in known) device.Dispose(); }
                return;
            }
            using var endpointEnumerator = new MMDeviceEnumerator();
            var historicalOutputs = endpointEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
            string identity;
            try { identity = FastTrackDeviceSetup.LearnPlayback12Identity(historicalOutputs.Where(FastTrackDeviceSetup.IsFastTrack)
                .Select(d => (d.FriendlyName, FastTrackDeviceSetup.GetPlaybackIdentity(d))), ""); }
            finally { foreach (var device in historicalOutputs) device.Dispose(); }
            var output = analogOutputs.Length == 1 ? analogOutputs[0]
                : analogOutputs.Single(d => !string.IsNullOrEmpty(identity) && FastTrackDeviceSetup.GetPlaybackIdentity(d) == identity);
            var input = analogInputs.Length == 1 ? analogInputs[0]
                : analogInputs.Single(d => d.FriendlyName.Contains("FastTrack", StringComparison.OrdinalIgnoreCase));
            if (alignCaptureRate)
            {
                originalCaptureRate = FastTrackDeviceSetup.ReadMixSampleRate(input.ID);
                rateAdjustedInput = input;
                if (!FastTrackDeviceSetup.TrySetRecordingSampleRate(input, out string message)) throw new InvalidOperationException(message);
                Console.WriteLine($"Temporary capture format {originalCaptureRate} -> {FastTrackDeviceSetup.ReadMixSampleRate(input.ID)} Hz; restoring in finally.");
            }
            var before = new { playbackVolume = output.AudioEndpointVolume.MasterVolumeLevelScalar, playbackMute = output.AudioEndpointVolume.Mute,
                recordingVolume = input.AudioEndpointVolume.MasterVolumeLevelScalar, recordingMute = input.AudioEndpointVolume.Mute };
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "endpoints", output = output.FriendlyName, outputId = output.ID,
                identity = FastTrackDeviceSetup.GetPlaybackIdentity(output), input = input.FriendlyName, inputId = input.ID, before }));
            File.WriteAllText(Path.Combine(directory, "endpoints-before.json"), JsonSerializer.Serialize(before, options));
            engine.PlaybackSampleRate = playbackRate; engine.RecordingGain = 1;
            Console.WriteLine($"Actual mode: {(engine.UseExclusivePlayback ? "WASAPI Exclusive" : "WASAPI Shared")}, playback {playbackRate} Hz; no gain/volume/mute writes.");
            var noise = new double[2];
            for (int channel = 0; channel < 2; channel++)
            {
                engine.PlaybackChannel = channel; engine.RecordingChannel = channel;
                float[] samples = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, .6,
                    customSweepSamples: new float[playbackRate / 2]);
                noise[channel] = Db(samples);
                Save(Path.Combine(directory, $"noise-in{channel + 1}.wav"), samples, engine.RecordingSampleRate);
                Console.WriteLine($"Noise IN{channel + 1}: {noise[channel]:F2} dBFS, capture {engine.RecordingSampleRate} Hz.");
            }
            var good = new List<(int Output, int Input, double Level)>();
            foreach (double probeDb in new[] { -36.0, -24.0 })
            {
            for (int outChannel = 0; outChannel < 2; outChannel++)
            for (int inChannel = 0; inChannel < 2; inChannel++)
            {
                engine.PlaybackChannel = outChannel; engine.RecordingChannel = inChannel;
                int toneSamples = playbackRate / 2;
                var tone = Enumerable.Range(0, toneSamples).Select(i => (float)(Math.Pow(10, probeDb / 20) * Math.Sin(2 * Math.PI * 1000 * i / (double)playbackRate)
                    * Math.Min(1, Math.Min(i / (playbackRate * .01), (toneSamples - 1 - i) / (playbackRate * .01))))).ToArray();
                var samples = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, 1.0, customSweepSamples: tone);
                Save(Path.Combine(directory, $"probe{probeDb:0}-out{outChannel + 1}-in{inChannel + 1}.wav"), samples, engine.RecordingSampleRate);
                int window = engine.RecordingSampleRate / 5, best = 0; double bestPower = 0;
                for (int start = 0; start + window <= samples.Length; start += Math.Max(1, window / 4))
                {
                    double power = samples.Skip(start).Take(window).Sum(v => (double)v * v);
                    if (power > bestPower) { bestPower = power; best = start; }
                }
                var segment = samples.Skip(best).Take(window).ToArray();
                var quality = AdvancedAudioMeasurement.AnalyzeTone(segment, engine.RecordingSampleRate, 1000);
                double level = Db(segment), snr = level - noise[inChannel];
                Console.WriteLine($"Probe OUT{outChannel + 1}->IN{inChannel + 1} at {probeDb:0} dBFS peak: {level:F2} dBFS, SNR {snr:F1} dB, f={quality.FundamentalFrequencyHz:F3}, peak={samples.Max(v => Math.Abs(v)):F5}.");
                if (samples.Any(v => !float.IsFinite(v)) || samples.Max(v => Math.Abs(v)) >= .9)
                    throw new InvalidOperationException("Overload or invalid samples during low-level probe; stopped without increasing stimulus.");
                if (quality.IsValid && Math.Abs(quality.FundamentalFrequencyHz - 1000) <= 5 && snr >= 15)
                    good.Add((outChannel, inChannel, level));
            }
            if (good.Count > 0) break;
            }
            foreach (var route in good.GroupBy(r => r.Output).Select(g => g.OrderByDescending(r => r.Level).First()))
            {
                engine.PlaybackChannel = route.Output; engine.RecordingChannel = route.Input;
                var settings = new LogSweepSettings(playbackRate, 18, Math.Min(22000, playbackRate * .47), 131072.0 / playbackRate, Math.Pow(10, -24.0 / 20), 192, 3);
                var excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
                var capture = await engine.PlayAndRecordAsync(output, input, SignalType.Sweep, 0, settings.DurationSeconds + 3, customSweepSamples: excitation);
                int rate = engine.RecordingSampleRate;
                Save(Path.Combine(directory, $"sweep-out{route.Output + 1}-in{route.Input + 1}.wav"), capture, rate);
                var analysisSettings = settings with { SampleRate = rate };
                var reference = rate == settings.SampleRate ? excitation : StandardAcousticMeasurement.GenerateLogSweep(analysisSettings);
                var result = StandardAcousticMeasurement.AnalyzeLogSweep(reference, capture, analysisSettings, measuredPreSweepNoiseFloorDbFs: noise[route.Input]);
                var saved = result with { AlignedTransferFunction = Array.Empty<System.Numerics.Complex>(), HarmonicTransferFunctions = Array.Empty<System.Numerics.Complex[]>() };
                File.WriteAllText(Path.Combine(directory, $"result-out{route.Output + 1}-in{route.Input + 1}.json"), JsonSerializer.Serialize(saved, options));
                Console.WriteLine($"Sweep OUT{route.Output + 1}->IN{route.Input + 1}: {result.Validity}, SNR {result.EstimatedSnrDb:F1} dB, delay {result.DirectArrivalMs:F3} ms. {result.ImpulseEffects?.Describe()}");
                if (result.IsClipped) throw new InvalidOperationException("Sweep clipping: stopped further playback.");
            }
            var after = new { playbackVolume = output.AudioEndpointVolume.MasterVolumeLevelScalar, playbackMute = output.AudioEndpointVolume.Mute,
                recordingVolume = input.AudioEndpointVolume.MasterVolumeLevelScalar, recordingMute = input.AudioEndpointVolume.Mute };
            File.WriteAllText(Path.Combine(directory, "endpoints-after.json"), JsonSerializer.Serialize(after, options));
            if (JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after)) throw new Exception("Endpoint controls changed during check.");
            Console.WriteLine($"{(good.Count > 0 ? "PASS" : "INCONCLUSIVE")} endpoint volume/mute preserved; {good.Count} routes carried the probe. Direct electrical loopback is not a DSP-effect qualification.");
        }
        finally
        {
            engine.Stop();
            try
            {
                if (rateAdjustedInput != null && FastTrackDeviceSetup.ReadMixSampleRate(rateAdjustedInput.ID) != originalCaptureRate)
                {
                    typeof(AudioEngine).Assembly.GetType("SoncaAudioInspector.WindowsEndpointPolicy")!
                        .GetMethod("SetSampleRate", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { rateAdjustedInput.ID, originalCaptureRate });
                    if (FastTrackDeviceSetup.ReadMixSampleRate(rateAdjustedInput.ID) != originalCaptureRate) throw new Exception("Capture rate restore readback failed.");
                    Console.WriteLine($"RESTORED capture format {originalCaptureRate} Hz.");
                }
            }
            finally { foreach (var device in outputs.Concat(inputs)) device.Dispose(); }
        }
    }

    private static double Db(float[] samples) => 10 * Math.Log10(Math.Max(1e-30, samples.Select(v => (double)v * v).DefaultIfEmpty(0).Average()));
    private static void Save(string path, float[] samples, int rate) { using var wav = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1)); wav.WriteSamples(samples, 0, samples.Length); }
}
