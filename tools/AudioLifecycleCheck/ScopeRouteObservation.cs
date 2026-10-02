using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SoncaAudioInspector;

internal static class ScopeRouteObservation
{
    public static async Task Run(bool playWav = false)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active).ToList();
        try
        {
            using var player = new ScopeWavPlayer();
            var input = devices.Single(d => d.DataFlow == DataFlow.Capture && d.FriendlyName.Contains("FastTrack Pro"));
            var output = devices.Single(d => d.DataFlow == DataFlow.Render && d.FriendlyName.Contains("MI30 SAM"));
            if (playWav && AudioSessionDiagnostics.Scan().Sessions.Any(s => s.Direction == "phát" && s.DeviceName.Contains("MI30 SAM")))
                throw new InvalidOperationException("Ngõ phát MI30 SAM đang được sử dụng; không phát chồng tone kiểm tra.");
            string BeforeSettings() => JsonSerializer.Serialize(new { input = input.AudioEndpointVolume.MasterVolumeLevelScalar,
                inputMute = input.AudioEndpointVolume.Mute, output = output.AudioEndpointVolume.MasterVolumeLevelScalar, outputMute = output.AudioEndpointVolume.Mute });
            string before = BeforeSettings();
            foreach (var device in new[] { output, input })
            {
                using var client = device.AudioClient;
                var manager = device.AudioSessionManager;
                try
                {
                    var sessions = new List<object>();
                    for (int i = 0; i < manager.Sessions.Count; i++)
                    {
                        using var session = manager.Sessions[i];
                        sessions.Add(new { pid = session.GetProcessID, state = session.State.ToString(),
                            volume = session.SimpleAudioVolume.Volume, mute = session.SimpleAudioVolume.Mute,
                            peak = session.AudioMeterInformation.MasterPeakValue });
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { device.FriendlyName, format = client.MixFormat.ToString(),
                        volume = device.AudioEndpointVolume.MasterVolumeLevelScalar, mute = device.AudioEndpointVolume.Mute, sessions }));
                }
                finally { manager.Dispose(); }
            }
            using var capture = new WasapiCapture(input, false, 100);
            using var loopback = new WasapiLoopbackCapture(output);
            var captured = new List<byte>(); var rendered = new List<byte>(); object gate = new();
            capture.DataAvailable += (_, e) => { lock (gate) captured.AddRange(e.Buffer.Take(e.BytesRecorded)); };
            loopback.DataAvailable += (_, e) => { lock (gate) rendered.AddRange(e.Buffer.Take(e.BytesRecorded)); };
            capture.StartRecording(); loopback.StartRecording();
            if (playWav)
            {
                await Task.Delay(1000);
                byte[] baseline; lock (gate) { baseline = captured.ToArray(); captured.Clear(); rendered.Clear(); }
                float[] baselineSamples = CaptureSampleDecoder.Decode(baseline, baseline.Length, capture.WaveFormat, 1, 1);
                double baselineRms = Math.Sqrt(baselineSamples.Select(s => (double)s * s).DefaultIfEmpty().Average());
                if (baselineRms > Math.Pow(10, -55.0 / 20)) throw new InvalidOperationException("Nền thu cao; chưa phát WAV kiểm tra.");
                player.Play(output.ID, 0);
            }
            await Task.Delay(3000);
            byte[] duringInput, duringOutput;
            lock (gate) { duringInput = captured.ToArray(); duringOutput = rendered.ToArray(); }
            if (playWav)
            {
                player.Stop();
                await Task.Delay(700);
                lock (gate) { captured.Clear(); rendered.Clear(); }
                await Task.Delay(700);
            }
            capture.StopRecording(); loopback.StopRecording();
            capture.Dispose(); loopback.Dispose();
            Report("input", duringInput, capture.WaveFormat);
            Report("output-loopback", duringOutput, loopback.WaveFormat);
            if (playWav)
            {
                Report("input-after-stop", captured.ToArray(), capture.WaveFormat);
                Report("output-after-stop", rendered.ToArray(), loopback.WaveFormat);
                if (before != BeforeSettings() || player.IsPlaying) throw new Exception("WAV player changed endpoint settings or remained active.");
                Console.WriteLine("WAV player stopped; endpoint settings unchanged.");
            }
        }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    private static void Report(string direction, byte[] data, WaveFormat format)
    {
        for (int c = 0; c < format.Channels; c++)
        {
            var samples = CaptureSampleDecoder.Decode(data, data.Length, format, 1, c);
            double rms = Math.Sqrt(samples.Select(s => (double)s * s).DefaultIfEmpty().Average());
            var quality = samples.Length >= 4096 ? AdvancedAudioMeasurement.AnalyzeTone(samples.TakeLast(Math.Min(16384, samples.Length)).ToArray(), format.SampleRate, 1000, 9) : null;
            Console.WriteLine(JsonSerializer.Serialize(new { direction, channel = c + 1, count = samples.Length,
                rmsDbFs = 20 * Math.Log10(Math.Max(1e-12, rms)), peak = samples.Select(s => Math.Abs(s)).DefaultIfEmpty().Max(),
                validTone = quality?.IsValid, frequency = quality is { IsValid: true } ? quality.FundamentalFrequencyHz : (double?)null }));
        }
    }
}
