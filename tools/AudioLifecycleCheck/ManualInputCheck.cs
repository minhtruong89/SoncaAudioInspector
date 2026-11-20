using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class ManualInputCheck
{
    public static async Task Line(string directory)
    {
        Directory.CreateDirectory(directory);
        using var engine = new AudioEngine { PlaybackSampleRate = 44100, PlaybackChannel = 0, RecordingChannel = 1 };
        var outputs = engine.GetPlaybackDevices(); var inputs = engine.GetRecordingDevices();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var history = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All).ToList();
            string identity;
            try { identity = FastTrackDeviceSetup.LearnPlayback12Identity(history.Where(FastTrackDeviceSetup.IsFastTrack).Select(d => (d.FriendlyName, FastTrackDeviceSetup.GetPlaybackIdentity(d))), ""); }
            finally { foreach (var d in history) d.Dispose(); }
            foreach (var endpoint in outputs.Concat(inputs)) Console.WriteLine($"Endpoint {endpoint.DataFlow}: {endpoint.FriendlyName}; identity={FastTrackDeviceSetup.GetPlaybackIdentity(endpoint)}");
            var output = outputs.FirstOrDefault(d => FastTrackDeviceSetup.IsFastTrack(d) && d.FriendlyName.Contains("Analog Connector 1/2", StringComparison.OrdinalIgnoreCase))
                ?? outputs.SingleOrDefault(d => !string.IsNullOrWhiteSpace(identity) && FastTrackDeviceSetup.GetPlaybackIdentity(d) == identity);
            var input = inputs.SingleOrDefault(FastTrackDeviceSetup.IsFastTrack);
            if (output == null || input == null) { Console.WriteLine("INCONCLUSIVE: no unambiguous active FastTrack 1/2 render/capture pair; no stimulus emitted."); return; }
            var runner = new TestRunner(engine) { PlaybackLevelDbFs = -24, UseLogSweepFrequencyResponse = true, EnableNoiseDiagnostics = true,
                LogSweepDurationSeconds = 65536.0 / 44100, RestrictFeqToEvaluationRange = true, EvaluationMaxFrequencyHz = 18000,
                LogSweepVerificationRuns = 1 };
            runner.OnLogMessage += (source, message) => Console.WriteLine($"[{source}] {message}");
            runner.OnTestCompleted += success => Console.WriteLine($"Completed success={success}, validReference={runner.HasComparedToStandard}, acquisitionIssue={runner.LastFeqInvalidReason}, THD={runner.LastMeasuredThdPercent}");
            runner.OnSweepResultReady += result => { File.WriteAllText(Path.Combine(directory, "sweep-summary.txt"),
                $"Validity={result.Validity}, Fs={result.SampleRate}, SNR={result.EstimatedSnrDb}, Clipped={result.IsClipped}, Delay={result.DirectArrivalMs}"); };
            await runner.RunTestAsync(output, input);
            Console.WriteLine($"Sweep received={runner.LastSweepResult != null}; preflightWarning={runner.LastPreflightWarning}");
        }
        finally { engine.StopAllAudio(); foreach (var d in outputs.Concat(inputs)) d.Dispose(); }
    }

    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        try
        {
            var device = devices.Single(FastTrackDeviceSetup.IsFastTrack);
            using var capture = new WasapiCapture(device, false, 100);
            var left = new List<float>(); var right = new List<float>();
            capture.DataAvailable += (_, e) => { lock (left) {
                left.AddRange(CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, capture.WaveFormat, 1, 0));
                right.AddRange(CaptureSampleDecoder.Decode(e.Buffer, e.BytesRecorded, capture.WaveFormat, 1, 1)); } };
            Console.WriteLine($"Silent input read only: {device.FriendlyName}; {capture.WaveFormat}");
            capture.StartRecording(); await Task.Delay(1200); capture.StopRecording();
            lock (left) foreach (var pair in new[] { ("IN1", left.ToArray()), ("IN2", right.ToArray()) })
            {
                double rms = Math.Sqrt(pair.Item2.Select(v => (double)v * v).DefaultIfEmpty().Average());
                double peak = pair.Item2.Select(v => Math.Abs((double)v)).DefaultIfEmpty().Max();
                bool repeated = CaptureDataIntegrity.HasRepeatedPcm16Bytes(pair.Item2, device.AudioEndpointVolume.MasterVolumeLevelScalar);
                Console.WriteLine($"{pair.Item1}: {20 * Math.Log10(Math.Max(1e-12, rms)):F2} dBFS, peak {peak:F6}, repeatedPCM={repeated}");
                using var wav = new WaveFileWriter(Path.Combine(directory, pair.Item1 + ".wav"), WaveFormat.CreateIeeeFloatWaveFormat(capture.WaveFormat.SampleRate, 1));
                wav.WriteSamples(pair.Item2, 0, pair.Item2.Length);
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }
}
