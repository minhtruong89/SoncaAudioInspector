using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScottPlot;
using ScottPlot.TickGenerators;
using SoncaAudioInspector;

using var engine = new AudioEngine();
List<MMDevice> playback = engine.GetPlaybackDevices();
List<MMDevice> recording = engine.GetRecordingDevices();

Console.WriteLine("PLAYBACK");
for (int i = 0; i < playback.Count; i++)
{
    string channelVolumes;
    try
    {
        channelVolumes = string.Join(", ", Enumerable.Range(0, playback[i].AudioEndpointVolume.Channels.Count)
            .Select(channel => $"ch{channel + 1}={playback[i].AudioEndpointVolume.Channels[channel].VolumeLevelScalar:P0}"));
    }
    catch (Exception ex)
    {
        channelVolumes = "unavailable: " + ex.Message;
    }
    Console.WriteLine($"[{i}] {playback[i].FriendlyName} | master={playback[i].AudioEndpointVolume.MasterVolumeLevelScalar:P0} | {channelVolumes} | {playback[i].ID}");
}
Console.WriteLine("RECORDING");
for (int i = 0; i < recording.Count; i++)
{
    string channelVolumes;
    try
    {
        channelVolumes = string.Join(", ", Enumerable.Range(0, recording[i].AudioEndpointVolume.Channels.Count)
            .Select(channel => $"ch{channel + 1}={recording[i].AudioEndpointVolume.Channels[channel].VolumeLevelScalar:P0}"));
    }
    catch (Exception ex)
    {
        channelVolumes = "unavailable: " + ex.Message;
    }
    Console.WriteLine($"[{i}] {recording[i].FriendlyName} | master={recording[i].AudioEndpointVolume.MasterVolumeLevelScalar:P0} | {channelVolumes} | {recording[i].ID}");
}

if (args.Length >= 2 && string.Equals(args[0], "--verify-upload", StringComparison.OrdinalIgnoreCase))
{
    ServerEngine.RememberedLogin? login = ServerEngine.GetRememberedLogin();
    if (login == null || !await ServerEngine.AuthenticateAsync(login.Account, login.Password))
        throw new InvalidOperationException("Không thể đăng nhập inventory.acnos.store bằng phiên đã lưu: " + ServerEngine.LastError);
    ProductInfo? uploadedProduct = await ServerEngine.CheckProductStatusAsync(args[1], args.Length > 2 ? args[2] : "MISAM");
    if (uploadedProduct == null)
    {
        IReadOnlyList<ProductInfo> matches = await ServerEngine.GetProductsAsync(1, 100, args[1]);
        uploadedProduct = matches.FirstOrDefault(product =>
            string.Equals(product.SerialNumber, args[1], StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.ProductCode, args[1], StringComparison.OrdinalIgnoreCase)
            || string.Equals(product.Id, args[1], StringComparison.OrdinalIgnoreCase));
    }
    if (uploadedProduct == null)
        throw new InvalidOperationException("Không đọc lại được sản phẩm đã upload: " + ServerEngine.LastError);
    Console.WriteLine($"UPLOAD READBACK productId='{uploadedProduct.Id}', serial='{uploadedProduct.SerialNumber}', model='{uploadedProduct.Model}', qaStatus='{uploadedProduct.QaStatus}', status='{uploadedProduct.Status}'");
    return;
}

if (args.Length == 0 || !string.Equals(args[0], "--measure", StringComparison.OrdinalIgnoreCase))
    return;

string playbackMatch = args.Length > 1 ? args[1] : "USB Audio Device";
string recordingMatch = args.Length > 2 ? args[2] : "KT USB Audio";
MMDevice? playbackDevice = playback.FirstOrDefault(device =>
    device.FriendlyName.Contains(playbackMatch, StringComparison.OrdinalIgnoreCase));
MMDevice? recordingDevice = recording.FirstOrDefault(device =>
    device.FriendlyName.Contains(recordingMatch, StringComparison.OrdinalIgnoreCase));
if (playbackDevice == null || recordingDevice == null)
    throw new InvalidOperationException($"Không tìm thấy endpoint: playback='{playbackMatch}', recording='{recordingMatch}'.");

using (var formatProbe = new WasapiCapture(recordingDevice, true, 100))
    Console.WriteLine($"CAPTURE FORMAT {formatProbe.WaveFormat.SampleRate} Hz, {formatProbe.WaveFormat.Channels} channel(s), {formatProbe.WaveFormat.Encoding}, {formatProbe.WaveFormat.BitsPerSample}-bit");
WaveFormat playbackMixFormat = playbackDevice.AudioClient.MixFormat;
Console.WriteLine($"PLAYBACK MIX FORMAT {playbackMixFormat.SampleRate} Hz, {playbackMixFormat.Channels} channel(s), {playbackMixFormat.Encoding}, {playbackMixFormat.BitsPerSample}-bit");
Console.WriteLine($"PLAYBACK ENDPOINT volume={playbackDevice.AudioEndpointVolume.MasterVolumeLevelScalar:P0}, mute={playbackDevice.AudioEndpointVolume.Mute}");
Console.WriteLine($"RECORDING ENDPOINT volume={recordingDevice.AudioEndpointVolume.MasterVolumeLevelScalar:P0}, mute={recordingDevice.AudioEndpointVolume.Mute}");
Console.WriteLine($"PLAYBACK ENDPOINT peak={(engine.TryGetWindowsPlaybackPeak(playbackDevice) ?? 0.0):F6}");
var defaultPlayback = engine.TryGetWindowsDefaultPlaybackVolume();
Console.WriteLine(defaultPlayback.HasValue
    ? $"WINDOWS DEFAULT volume={defaultPlayback.Value.Volume:P0}, device='{defaultPlayback.Value.DeviceName}'"
    : "WINDOWS DEFAULT unavailable");
Console.WriteLine(engine.TryValidateAudioDevices(playbackDevice, recordingDevice, out string endpointError)
    ? "ENDPOINT VALIDATION active"
    : $"ENDPOINT VALIDATION failed: {endpointError}");
if (args.Any(arg => string.Equals(arg, "--info-only", StringComparison.OrdinalIgnoreCase)))
    return;

double? requestedPlaybackVolume = args.Select(arg =>
    arg.StartsWith("--playback=", StringComparison.OrdinalIgnoreCase)
        && double.TryParse(arg["--playback=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? (double?)value
            : null)
    .FirstOrDefault(value => value.HasValue && double.IsFinite(value.Value));
engine.PlaybackVolume = requestedPlaybackVolume.HasValue ? Math.Clamp(requestedPlaybackVolume.Value, 0.01, 1.0) : 0.60;
engine.RecordingVolume = 0.50;
engine.ApplyWindowsRecordingVolume(recordingDevice);
double? requestedRecordingGain = args.Select(arg =>
    arg.StartsWith("--gain=", StringComparison.OrdinalIgnoreCase)
        && double.TryParse(arg["--gain=".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? (double?)value
            : null)
    .FirstOrDefault(value => value.HasValue && double.IsFinite(value.Value));
engine.RecordingGain = requestedRecordingGain.HasValue ? Math.Clamp(requestedRecordingGain.Value, 0.01, 2.0) : 1.0;
int? requestedRecordingChannel = args.Select(arg =>
    arg.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(arg["--channel=".Length..], out int value) ? (int?)value : null)
    .FirstOrDefault(value => value.HasValue);
engine.RecordingChannel = requestedRecordingChannel;
int? requestedPlaybackChannel = args.Select(arg =>
    arg.StartsWith("--playback-channel=", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(arg["--playback-channel=".Length..], out int value) ? (int?)value : null)
    .FirstOrDefault(value => value.HasValue);
engine.PlaybackChannel = requestedPlaybackChannel;
engine.UseExclusivePlayback = args.Any(arg =>
    string.Equals(arg, "--exclusive", StringComparison.OrdinalIgnoreCase));
if (args.Contains("--set-volume-only", StringComparer.OrdinalIgnoreCase))
{
    if (!requestedPlaybackVolume.HasValue)
        throw new ArgumentException("--set-volume-only requires --playback=<0..1>.");
    engine.ApplyWindowsPlaybackVolume(playbackDevice);
    double? actualVolume = engine.TryGetWindowsPlaybackVolume(playbackDevice);
    Console.WriteLine($"SET PLAYBACK ENDPOINT '{playbackDevice.FriendlyName}': requested={engine.PlaybackVolume:P0}, readback={actualVolume:P0}");
    if (!actualVolume.HasValue || Math.Abs(actualVolume.Value - engine.PlaybackVolume) > 0.02)
        throw new InvalidOperationException("Playback endpoint volume did not reach the requested level.");
    return;
}
if (args.Contains("--server-auth-check", StringComparer.OrdinalIgnoreCase))
{
    ServerEngine.RememberedLogin? remembered = ServerEngine.GetRememberedLogin();
    if (remembered == null)
        throw new InvalidOperationException("No remembered inventory.acnos.store login is available.");
    if (!await ServerEngine.AuthenticateAsync(remembered.Account, remembered.Password))
        throw new InvalidOperationException("Inventory authentication failed: " + ServerEngine.LastError);
    Console.WriteLine("SERVER AUTH READY: inventory.acnos.store authenticated with the locally remembered login.");
    return;
}
if (args.Contains("--save-wav", StringComparer.OrdinalIgnoreCase)) AudioEngine.flagSaveFile = true;
engine.PlaybackSampleRate = 44100;
double playbackFrequencyScale = args.Select(arg =>
    arg.StartsWith("--frequency-scale=", StringComparison.OrdinalIgnoreCase)
        && double.TryParse(arg["--frequency-scale=".Length..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? (double?)value
            : null)
    .FirstOrDefault(value => value.HasValue && double.IsFinite(value.Value)) ?? 1.0;
double[][] measuredBands = TestRunner.TestFrequencyBands.Select(band => band.Frequencies).ToArray();
double[][] playbackBands = measuredBands.Select(frequencies =>
    frequencies.Append(1100.0).Distinct().OrderBy(frequency => frequency).ToArray()).ToArray();
double scale = SignalSampleProvider.CalculateCommonMultitoneSampleScale(48000, playbackBands);
MicrophoneCalibration? calibration = MicrophoneCalibration.ScanAvailableCalibrations()
    .FirstOrDefault(item => item.Name.Contains("99-00192", StringComparison.OrdinalIgnoreCase));

string recordingChannelText = requestedRecordingChannel is int selectedChannel ? $"{selectedChannel + 1}" : "mixed";
string playbackChannelText = requestedPlaybackChannel == 0 ? "Left" : requestedPlaybackChannel == 1 ? "Right" : "Stereo";
Console.WriteLine($"MEASURE playback='{playbackDevice.FriendlyName}' ({playbackChannelText}), recording='{recordingDevice.FriendlyName}', channel={recordingChannelText}, sampleRate={engine.PlaybackSampleRate}, mode={(engine.UseExclusivePlayback ? "EXCL" : "Shared")}, playbackVolume={engine.PlaybackVolume:P0}, recordingGain={engine.RecordingGain:P0}, frequencyScale={playbackFrequencyScale:F5}, calibration='{calibration?.Name ?? "none"}'");
if (args.Contains("--noise-only", StringComparer.OrdinalIgnoreCase))
{
    int noiseChecks = args.Contains("--noise-once", StringComparer.OrdinalIgnoreCase) ? 1 : 3;
    for (int check = 1; check <= noiseChecks; check++)
    {
        AudioEngine.DualCaptureResult capture = await engine.CaptureSilenceAsync(recordingDevice, null, 1.0);
        double rms = capture.DutSamples.Length == 0 ? 0 : Math.Sqrt(capture.DutSamples.Select(value => (double)value * value).Average());
        double peak = capture.DutSamples.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
        Console.WriteLine($"NOISE ONLY {check}/{noiseChecks}: rms={20.0 * Math.Log10(Math.Max(rms, 1e-9)):F1} dBFS, peak={peak:F4}");
        if (args.Contains("--noise-slices", StringComparer.OrdinalIgnoreCase))
        {
            int samplesPerSlice = Math.Max(1, capture.DutSampleRate / 4);
            for (int offset = 0; offset < capture.DutSamples.Length; offset += samplesPerSlice)
            {
                float[] slice = capture.DutSamples.Skip(offset).Take(samplesPerSlice).ToArray();
                double sliceRms = slice.Length == 0 ? 0 : Math.Sqrt(slice.Select(value => (double)value * value).Average());
                double slicePeak = slice.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
                Console.WriteLine($"NOISE SLICE {check} {offset / (double)capture.DutSampleRate:F2}-{(offset + slice.Length) / (double)capture.DutSampleRate:F2}s: rms={20.0 * Math.Log10(Math.Max(sliceRms, 1e-9)):F1} dBFS, peak={slicePeak:F4}");
            }
        }
        if (check == 1 && engine.UseExclusivePlayback)
            Console.WriteLine($"NOISE ONLY capture format: {engine.LastExclusiveCaptureFormat}");
        NoiseSpectrumMetrics noise = ProductionMeasurement.AnalyzeNoise(capture.DutSamples, capture.DutSampleRate, null);
        Console.WriteLine($"NOISE ONLY spectrum: peak={noise.DominantSpectralPeakHz:F1}Hz {noise.DominantSpectralPeakDb:F1}dBFS, hum={noise.DominantHumHz:F1}Hz {noise.DominantHumDb:F1}dBFS");
        if (check < noiseChecks) await Task.Delay(1000);
    }
    engine.Stop();
    return;
}
string? spectrumProbeArgument = args.FirstOrDefault(arg => arg.StartsWith("--spectrum-probe=", StringComparison.OrdinalIgnoreCase));
if (args.Contains("--handoff-left-right", StringComparer.OrdinalIgnoreCase))
{
    if (!engine.UseExclusivePlayback || !requestedPlaybackVolume.HasValue)
        throw new ArgumentException("--handoff-left-right requires --exclusive and --playback=<level>.");
    engine.PlaybackVolume = requestedPlaybackVolume.Value;
    engine.ApplyWindowsPlaybackVolume(playbackDevice);
    double? handoffVolume = engine.TryGetWindowsPlaybackVolume(playbackDevice);
    if (!handoffVolume.HasValue || Math.Abs(handoffVolume.Value - engine.PlaybackVolume) > 0.02)
        throw new InvalidOperationException("Cannot set the Analog 1/2 endpoint volume for handoff probe.");
    engine.RecordingChannel = 1;
    double? handoffFloorReferenceDbFs = null;
    async Task<(double DbFs, double Peak)> ReadSilenceAsync(string label)
    {
        AudioEngine.DualCaptureResult capture = await engine.CaptureSilenceAsync(recordingDevice, null, 1.0);
        double rms = Math.Sqrt(capture.DutSamples.Select(sample => (double)sample * sample).DefaultIfEmpty().Average());
        double peak = capture.DutSamples.Select(sample => Math.Abs((double)sample)).DefaultIfEmpty().Max();
        double dbFs = 20.0 * Math.Log10(Math.Max(rms, 1e-9));
        Console.WriteLine($"HANDOFF {label}: silence={dbFs:F1}dBFS peak={peak:F4}");
        return (dbFs, peak);
    }
    try
    {
        for (int cycle = 1; cycle <= 2; cycle++)
        {
            foreach (int channel in new[] { 0, 1 })
            {
                string side = channel == 0 ? "LEFT" : "RIGHT";
                (double floor, double silentPeak) = await ReadSilenceAsync($"cycle={cycle} before {side}");
                handoffFloorReferenceDbFs ??= floor;
                if (handoffFloorReferenceDbFs.HasValue && floor < handoffFloorReferenceDbFs.Value - 8.0)
                    Console.WriteLine($"HANDOFF {side}: quiet floor {floor:F1}dBFS; 1000Hz probe will verify the microphone");
                if (floor > -55.0 || silentPeak >= 0.05)
                {
                    Console.WriteLine($"HANDOFF STOP: {side} preflight noise is invalid; no stimulus played");
                    return;
                }
                engine.PlaybackChannel = channel;
                Console.WriteLine($"HANDOFF cycle={cycle} play={side} only, PlaybackChannel={engine.PlaybackChannel}, EXCL, 1000Hz");
                float[] captured = await engine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, 1000.0, 3.0);
                int sampleRate = engine.RecordingSampleRate;
                float[] stable = captured.Skip(Math.Max(0, captured.Length - 2 * sampleRate)).ToArray();
                ToneQualityMetrics tone = AdvancedAudioMeasurement.AnalyzeTone(stable, sampleRate, 1000.0);
                Console.WriteLine($"HANDOFF cycle={cycle} {side}: THD={tone.ThdPercent:F4}% THD+N={tone.ThdNPercent:F4}% signal={tone.SignalLevelDbFs:F1}dBFS peak={tone.PeakSample:F4} valid={tone.IsValid}");
                engine.Stop();
                await Task.Delay(1000);
                if (tone.PeakSample >= 0.995 || !tone.IsValid)
                {
                    Console.WriteLine("HANDOFF STOP: capture invalid or clipping");
                    return;
                }
            }
        }
    }
    finally
    {
        engine.Stop();
    }
    return;
}
if (spectrumProbeArgument != null
    && double.TryParse(spectrumProbeArgument["--spectrum-probe=".Length..], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out double spectrumProbeFrequency))
{
    if (engine.UseExclusivePlayback)
    {
        double exclusiveProbeFrequency = spectrumProbeFrequency * playbackFrequencyScale;
        double signalScale = args.Select(arg =>
            arg.StartsWith("--signal-scale=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(arg["--signal-scale=".Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value)
                    ? (double?)value : null)
            .FirstOrDefault(value => value.HasValue && double.IsFinite(value.Value)) ?? 1.0;
        signalScale = Math.Clamp(signalScale, 0.01, 1.0);
        double? originalEndpointVolume = engine.TryGetWindowsPlaybackVolume(playbackDevice);
        try
        {
            if (requestedPlaybackVolume.HasValue)
            {
                if (!originalEndpointVolume.HasValue)
                    throw new InvalidOperationException("Cannot read original playback endpoint volume; refusing to alter it.");
                engine.ApplyWindowsPlaybackVolume(playbackDevice);
                double? appliedVolume = engine.TryGetWindowsPlaybackVolume(playbackDevice);
                Console.WriteLine($"EXCL PROBE endpoint volume requested={engine.PlaybackVolume:P0}, readback={appliedVolume:P0}");
                if (!appliedVolume.HasValue || Math.Abs(appliedVolume.Value - engine.PlaybackVolume) > 0.02)
                    throw new InvalidOperationException("Playback endpoint volume readback does not match requested level.");
            }
            float[] exclusiveCaptured = await engine.PlayAndRecordAsync(
                playbackDevice, recordingDevice, SignalType.Sine, exclusiveProbeFrequency, 3.0,
                recordingChannel: requestedRecordingChannel, signalSampleScale: signalScale);
            int exclusiveSampleRate = engine.RecordingSampleRate;
            int exclusiveAnalysisCount = Math.Min(exclusiveCaptured.Length, exclusiveSampleRate);
            float[] exclusiveAnalysis = exclusiveCaptured.Skip(Math.Max(0, (exclusiveCaptured.Length - exclusiveAnalysisCount) / 2)).Take(exclusiveAnalysisCount).ToArray();
            ToneQualityMetrics exclusiveTone = AdvancedAudioMeasurement.AnalyzeTone(exclusiveAnalysis, exclusiveSampleRate, exclusiveProbeFrequency);
            Console.WriteLine($"EXCL PROBE requested={spectrumProbeFrequency:F1}Hz, generated={exclusiveProbeFrequency:F2}Hz, digitalScale={signalScale:F3}, detected={exclusiveTone.FundamentalFrequencyHz:F2}Hz, valid={exclusiveTone.IsValid}, THD={exclusiveTone.ThdPercent:F4}%, THD+N={exclusiveTone.ThdNPercent:F4}%, SNR={exclusiveTone.SnrDb:F1}dB, level={exclusiveTone.SignalLevelDbFs:F1}dBFS, peak={exclusiveTone.PeakSample:F4}");
        }
        finally
        {
            engine.Stop();
            if (originalEndpointVolume.HasValue && requestedPlaybackVolume.HasValue)
            {
                engine.PlaybackVolume = originalEndpointVolume.Value;
                engine.ApplyWindowsPlaybackVolume(playbackDevice);
                Console.WriteLine($"EXCL PROBE endpoint volume restored={engine.TryGetWindowsPlaybackVolume(playbackDevice):P0}");
            }
        }
        return;
    }

    string? captureRateArgument = args.FirstOrDefault(arg => arg.StartsWith("--capture-rate=", StringComparison.OrdinalIgnoreCase));
    if (captureRateArgument != null
        && int.TryParse(captureRateArgument["--capture-rate=".Length..], out int requestedCaptureRate))
    {
        var rateProbeSamples = new List<float>();
        using var rateProbeCapture = new WasapiCapture(recordingDevice, true, 100)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(requestedCaptureRate, 2)
        };
        WaveFormat rateProbeFormat = rateProbeCapture.WaveFormat;
        rateProbeCapture.DataAvailable += (_, eventArgs) => rateProbeSamples.AddRange(
            CaptureSampleDecoder.Decode(eventArgs.Buffer, eventArgs.BytesRecorded, rateProbeFormat, 1.0, requestedRecordingChannel));
        using var rateProbeOutput = new WasapiOut(playbackDevice, AudioClientShareMode.Shared, false, 100);
        rateProbeOutput.Init(new SignalSampleProvider(48000, SignalType.Sine, spectrumProbeFrequency, engine.PlaybackVolume));
        rateProbeCapture.StartRecording();
        await Task.Delay(150);
        rateProbeOutput.Play();
        await Task.Delay(3000);
        rateProbeOutput.Stop();
        rateProbeCapture.StopRecording();
        await Task.Delay(150);
        int rateProbeCount = Math.Min(rateProbeSamples.Count, rateProbeFormat.SampleRate);
        float[] rateProbeAnalysis = rateProbeSamples.Skip(Math.Max(0, (rateProbeSamples.Count - rateProbeCount) / 2)).Take(rateProbeCount).ToArray();
        ToneQualityMetrics rateProbeTone = AdvancedAudioMeasurement.AnalyzeTone(rateProbeAnalysis, rateProbeFormat.SampleRate, spectrumProbeFrequency);
        FrequencySpectrum rateProbeSpectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(rateProbeAnalysis, rateProbeFormat.SampleRate);
        int strongestIndex = Enumerable.Range(1, Math.Max(0, rateProbeSpectrum.Magnitudes.Length - 2))
            .Where(index => rateProbeSpectrum.FrequenciesHz[index] >= 20 && rateProbeSpectrum.FrequenciesHz[index] <= 20000)
            .OrderByDescending(index => rateProbeSpectrum.Magnitudes[index])
            .FirstOrDefault();
        Console.WriteLine($"CAPTURE RATE PROBE requestedFormat={requestedCaptureRate}Hz, actualFormat={rateProbeFormat.SampleRate}Hz, detected={rateProbeTone.FundamentalFrequencyHz:F2}Hz, valid={rateProbeTone.IsValid}, strongest={rateProbeSpectrum.FrequenciesHz.ElementAtOrDefault(strongestIndex):F1}Hz, level={rateProbeTone.SignalLevelDbFs:F1}dBFS");
        return;
    }
    double sharedProbeScale = args.Select(arg =>
        arg.StartsWith("--signal-scale=", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(arg["--signal-scale=".Length..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? (double?)value : null)
        .FirstOrDefault(value => value.HasValue && double.IsFinite(value.Value)) ?? 1.0;
    sharedProbeScale = Math.Clamp(sharedProbeScale, 0.01, 1.0);
    var loopbackSamples = new List<float>();
    using var loopback = new WasapiLoopbackCapture(playbackDevice);
    WaveFormat loopbackFormat = loopback.WaveFormat;
    loopback.DataAvailable += (_, eventArgs) => loopbackSamples.AddRange(
        CaptureSampleDecoder.Decode(eventArgs.Buffer, eventArgs.BytesRecorded, loopbackFormat, 1.0));
    loopback.StartRecording();
    await Task.Delay(150);
    double generatedProbeFrequency = spectrumProbeFrequency * playbackFrequencyScale;
    float[] captured = await engine.PlayAndRecordAsync(
        playbackDevice, recordingDevice, SignalType.Sine, generatedProbeFrequency, 3.0,
        recordingChannel: requestedRecordingChannel, signalSampleScale: sharedProbeScale);
    loopback.StopRecording();
    await Task.Delay(150);
    int sampleRate = engine.RecordingSampleRate;
    int analysisCount = Math.Min(captured.Length, sampleRate);
    float[] analysis = captured.Skip(Math.Max(0, (captured.Length - analysisCount) / 2)).Take(analysisCount).ToArray();
    ToneQualityMetrics tone = AdvancedAudioMeasurement.AnalyzeTone(analysis, sampleRate, generatedProbeFrequency);
    FrequencySpectrum spectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(analysis, sampleRate);
    var peaks = Enumerable.Range(1, Math.Max(0, spectrum.Magnitudes.Length - 2))
        .Where(index => spectrum.FrequenciesHz[index] >= 20 && spectrum.FrequenciesHz[index] <= 20000
            && spectrum.Magnitudes[index] >= spectrum.Magnitudes[index - 1]
            && spectrum.Magnitudes[index] > spectrum.Magnitudes[index + 1])
        .OrderByDescending(index => spectrum.Magnitudes[index])
        .Take(12)
        .Select(index => $"{spectrum.FrequenciesHz[index]:F1}Hz/{20 * Math.Log10(spectrum.Magnitudes[index] + 1e-12):F1}dB")
        .ToArray();
    Console.WriteLine($"SPECTRUM PROBE requested={spectrumProbeFrequency:F1}Hz, generated={generatedProbeFrequency:F2}Hz, digitalScale={sharedProbeScale:F3}, detected={tone.FundamentalFrequencyHz:F2}Hz, valid={tone.IsValid}, THD={tone.ThdPercent:F4}%, THD+N={tone.ThdNPercent:F4}%, SNR={tone.SnrDb:F1}dB, level={tone.SignalLevelDbFs:F1}dBFS, peak={tone.PeakSample:F4}");
    Console.WriteLine("STRONGEST PEAKS " + string.Join(" | ", peaks));
    int loopbackCount = Math.Min(loopbackSamples.Count, loopbackFormat.SampleRate);
    float[] loopbackAnalysis = loopbackSamples.Skip(Math.Max(0, (loopbackSamples.Count - loopbackCount) / 2)).Take(loopbackCount).ToArray();
    ToneQualityMetrics loopbackTone = AdvancedAudioMeasurement.AnalyzeTone(loopbackAnalysis, loopbackFormat.SampleRate, generatedProbeFrequency);
    FrequencySpectrum loopbackSpectrum = AdvancedAudioMeasurement.AnalyzeSpectrum(loopbackAnalysis, loopbackFormat.SampleRate);
    int loopbackPeakIndex = Enumerable.Range(1, Math.Max(0, loopbackSpectrum.Magnitudes.Length - 2))
        .Where(index => loopbackSpectrum.FrequenciesHz[index] >= 20 && loopbackSpectrum.FrequenciesHz[index] <= 20000)
        .OrderByDescending(index => loopbackSpectrum.Magnitudes[index])
        .FirstOrDefault();
    Console.WriteLine($"LOOPBACK PROBE format={loopbackFormat.SampleRate}Hz, detected={loopbackTone.FundamentalFrequencyHz:F2}Hz, valid={loopbackTone.IsValid}, strongest={loopbackSpectrum.FrequenciesHz.ElementAtOrDefault(loopbackPeakIndex):F1}Hz");
    return;
}
if (args.Any(arg => string.Equals(arg, "--advanced-sweep", StringComparison.OrdinalIgnoreCase)))
{
    int sampleRate = playbackMixFormat.SampleRate;
    engine.PlaybackSampleRate = sampleRate;
    var settings = new LogSweepSettings(sampleRate, 18, Math.Min(22000, sampleRate * 0.47), 8, 0.2, 48, 1, 1.0);
    float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
    float[] playbackSweep = excitation;
    float[] captured = await engine.PlayAndRecordAsync(
        playbackDevice, recordingDevice, SignalType.Sweep, 0.0,
        playbackSweep.Length / (double)sampleRate + settings.DecayAnalysisSeconds,
        customSweepSamples: playbackSweep,
        recordingChannel: requestedRecordingChannel);
    int recordingRate = engine.RecordingSampleRate;
    captured = StandardAcousticMeasurement.ApplySubsonicConditioning(captured, recordingRate, 10.0);
    float[] analysisExcitation = recordingRate == sampleRate
        ? excitation
        : StandardAcousticMeasurement.GenerateLogSweep(settings with { SampleRate = recordingRate });
    StandardAcousticResult result = StandardAcousticMeasurement.AnalyzeLogSweep(
        analysisExcitation,
        captured,
        settings with { SampleRate = recordingRate },
        calibration);
    SweepHarmonicPoint? point = result.SweepHarmonics
        .OrderBy(item => Math.Abs(item.FundamentalFrequencyHz - 1000.0))
        .FirstOrDefault();
    Console.WriteLine($"ADVANCED SWEEP validity={result.Validity}, level={result.SignalLevelDbFs:F1}dBFS, " +
        $"SNR={result.EstimatedSnrDb:F1}dB, noise={result.NoiseFloorDbFs:F1}dBFS, " +
        $"arrival={result.DirectArrivalMs:F1}ms, correlation={result.SweepCorrelation:F3}, impulse={result.ImpulsePeakToBackgroundDb:F1}dB, " +
        $"responsePoints={result.FrequencyResponse.Count}, harmonicPoints={result.SweepHarmonics.Count}, " +
        $"THD@{point?.FundamentalFrequencyHz ?? double.NaN:F0}Hz={point?.ThdPercent ?? double.NaN:F4}%");
    if (point != null)
    {
        foreach (int order in Enumerable.Range(2, 8))
        {
            double dbc = point.GetHarmonicDbc(order);
            Console.WriteLine(double.IsFinite(dbc)
                ? $"  H{order}@{point.FundamentalFrequencyHz:F0}Hz={100.0 * Math.Pow(10.0, dbc / 20.0):F6}% ({dbc:F2} dBc)"
                : $"  H{order}@{point.FundamentalFrequencyHz:F0}Hz=N/A (không đủ tương quan thời gian hoặc băng thông)");
        }
    }
    foreach (double target in new[] { 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 15000.0, 20000.0 })
    {
        FrequencyResponsePoint? responsePoint = result.FrequencyResponse
            .OrderBy(item => Math.Abs(Math.Log(item.FrequencyHz / target)))
            .FirstOrDefault();
        FrequencyResponsePoint? ungatedPoint = result.UngatedFrequencyResponse
            .OrderBy(item => Math.Abs(Math.Log(item.FrequencyHz / target)))
            .FirstOrDefault();
        Console.WriteLine($"  RESPONSE {responsePoint?.FrequencyHz ?? double.NaN,8:F1}Hz gated={responsePoint?.NormalizedLevelDb ?? double.NaN,8:F2}dBr ungated={ungatedPoint?.NormalizedLevelDb ?? double.NaN,8:F2}dBr");
    }
    foreach (MeasurementDiagnostic diagnostic in result.Diagnostics)
        Console.WriteLine($"  {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
    return;
}
if (args.Any(arg => string.Equals(arg, "--clip-probe", StringComparison.OrdinalIgnoreCase)))
{
    var settings = new LogSweepSettings(48000, 20, 20000, 8, 0.2, 48, 1, engine.RecordingGain);
    float[] excitation = StandardAcousticMeasurement.GenerateLogSweep(settings);
    float[] captured = await engine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sweep, 0,
        9, customSweepSamples: excitation, recordingChannel: 0);
    int railCount = captured.Count(sample => Math.Abs(sample) >= 0.995f);
    int exactPositive = captured.Count(sample => sample == 1.0f);
    int exactNegative = captured.Count(sample => sample == -1.0f);
    int longestRailRun = 0, currentRailRun = 0;
    foreach (float sample in captured)
    {
        if (Math.Abs(sample) >= 0.995f) longestRailRun = Math.Max(longestRailRun, ++currentRailRun);
        else currentRailRun = 0;
    }
    double[] absolute = captured.Select(sample => Math.Abs((double)sample)).OrderBy(value => value).ToArray();
    double Percentile(double ratio) => absolute[Math.Clamp((int)Math.Round((absolute.Length - 1) * ratio), 0, absolute.Length - 1)];
    StandardAcousticResult result = StandardAcousticMeasurement.AnalyzeLogSweep(excitation, captured, settings, calibration);
    Console.WriteLine($"CLIP PROBE samples={captured.Length}, min={captured.Min():F6}, max={captured.Max():F6}, p99={Percentile(.99):F6}, p99.9={Percentile(.999):F6}");
    Console.WriteLine($"CLIP PROBE rail>=0.995: {railCount}/{captured.Length} ({railCount * 100.0 / captured.Length:F5}%), exact +1={exactPositive}, exact -1={exactNegative}, longest rail run={longestRailRun} samples");
    Console.WriteLine($"CLIP PROBE analyzer validity={result.Validity}, level={result.SignalLevelDbFs:F1}dBFS, SNR={result.EstimatedSnrDb:F1}dB, correlation={result.SweepCorrelation:F3}, diagnostics={string.Join(" | ", result.Diagnostics.Select(item => item.Code))}");
    return;
}
if (args.Any(arg => string.Equals(arg, "--reference-five", StringComparison.OrdinalIgnoreCase)))
{
    var referenceCurves = new List<Dictionary<double, double>>();
    for (int run = 1; run <= 5; run++)
    {
        var curve = new Dictionary<double, double>();
        var runner = new TestRunner(engine)
        {
            UseLogSweepFrequencyResponse = true,
            FeqMicrophoneCalibration = calibration,
            FreqResponseToleranceDb = 3.0
        };
        runner.OnLogMessage += (source, message) => Console.WriteLine($"[{run}/5 {source}] {message}");
        runner.OnFrequencyResponsePoint += (frequency, level) => curve[frequency] = level;
        await runner.RunTestAsync(playbackDevice, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: true);
        Console.WriteLine($"REFERENCE RUN {run}/5 valid={runner.ReferenceAcquisitionValid}, points={curve.Count}, peak={runner.LastFeqPeakSample:F4}, level={runner.LastSignalLevelDbFs:F1}dBFS, reason='{runner.LastFeqInvalidReason}'");
        if (!runner.ReferenceAcquisitionValid) return;
        referenceCurves.Add(curve);
    }
    TestRunner.FeqRepeatabilityAssessment assessment = TestRunner.AssessFeqRepeatability(referenceCurves, true, 3.0);
    Console.WriteLine($"REFERENCE FIVE RESULT invalid={assessment.IsInvalid}: {TestRunner.DescribeFeqRepeatability(assessment, 30)}");
    return;
}
if (args.Any(arg => string.Equals(arg, "--log-sweep", StringComparison.OrdinalIgnoreCase)))
{
    var runner = new TestRunner(engine)
    {
        UseLogSweepFrequencyResponse = true,
        LogSweepVerificationRuns = 2,
        RequireLogSweepPhaseAlignment = true,
        PlaybackFrequencyScale = playbackFrequencyScale,
        MinimumInputSignalDbFs = -40.0,
        FeqMicrophoneCalibration = calibration,
        FreqResponseToleranceDb = 3.0
    };
    int responsePoints = 0;
    runner.OnLogMessage += (source, message) => Console.WriteLine($"[{source}] {message}");
    runner.OnFrequencyResponsePoint += (_, _) => responsePoints++;
    Console.WriteLine("LOG SWEEP EXACT PIPELINE: 2 valid runs, up to 3 attempts, THD skipped");
    await runner.RunTestAsync(playbackDevice, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: true);
    Console.WriteLine($"LOG SWEEP RESULT valid={runner.ReferenceAcquisitionValid}, pointsEmitted={responsePoints}, peak={runner.LastFeqPeakSample:F4}, level={runner.LastSignalLevelDbFs:F1}dBFS, repeatabilityInvalid={runner.FeqRepeatabilityInvalid}, reason='{runner.LastFeqInvalidReason}'");
    SweepDistortionSummary averagedSummary = runner.BuildSweepDistortionSummary(runner.LastSweepResult, 1000.0);
    Console.WriteLine($"LOG SWEEP THD PREAVERAGE @1000Hz: valid={averagedSummary.IsValid}, THD={averagedSummary.ThdPercent:F6}%, H2={averagedSummary.HarmonicPercent.GetValueOrDefault(2, double.NaN):F6}%");
    return;
}
if (args.Any(arg => string.Equals(arg, "--compare-multitone-layouts", StringComparison.OrdinalIgnoreCase)))
{
    async Task<(Dictionary<double, double> Curve, double BroadbandDbFs, double PilotDbFs)> CaptureLayoutAsync(double[][] groups)
    {
        var expectedGroups = groups.Select(group => group.Append(1100.0).Distinct().OrderBy(value => value).ToArray()).ToArray();
        var generatedGroups = expectedGroups.Select(group => group.Select(value => value * playbackFrequencyScale).ToArray()).ToArray();
        double layoutScale = SignalSampleProvider.CalculateCommonMultitoneSampleScale(48000, generatedGroups);
        var curve = new Dictionary<double, double>();
        var broadbandLevels = new List<double>();
        var pilotLevels = new List<double>();
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            double duration = groups.Length == 1 ? 10.0 : (groupIndex == groups.Length - 1 ? 10.0 : 6.0);
            double analysisDuration = groups.Length == 1 ? 8.0 : (groupIndex == groups.Length - 1 ? 8.0 : 4.0);
            float[] captured = await engine.PlayAndRecordAsync(
                playbackDevice, recordingDevice, SignalType.Multitone, 1100.0 * playbackFrequencyScale, duration,
                multitoneFrequencies: generatedGroups[groupIndex], multitoneSampleScale: layoutScale);
            int rate = engine.RecordingSampleRate;
            int count = Math.Min(captured.Length, (int)(rate * analysisDuration));
            float[] centered = captured.Skip(Math.Max(0, (captured.Length - count) / 2)).Take(count).ToArray();
            var response = DspProcessor.CalculateMultitoneResponse(centered, rate, expectedGroups[groupIndex]);
            double pilot = response[1100.0];
            pilotLevels.Add(pilot);
            broadbandLevels.Add(20.0 * Math.Log10(DspProcessor.CalculateRms(centered, 0, centered.Length) + 1e-9));
            foreach (double frequency in groups[groupIndex])
                curve[frequency] = response[frequency] - pilot;
        }
        double reference = curve[1000.0];
        foreach (double frequency in curve.Keys.ToArray()) curve[frequency] -= reference;
        return (curve, broadbandLevels.Average(), pilotLevels.Average());
    }

    double[][] threeGroups = TestRunner.TestFrequencyBands.Select(band => band.Frequencies).ToArray();
    double[][] oneGroup = { TestRunner.TestFrequencies };
    var three = await CaptureLayoutAsync(threeGroups);
    var one = await CaptureLayoutAsync(oneGroup);
    Console.WriteLine($"LAYOUT LEVELS three-band broadband={three.BroadbandDbFs:F1}dBFS pilot={three.PilotDbFs:F1}dBFS; one-band broadband={one.BroadbandDbFs:F1}dBFS pilot={one.PilotDbFs:F1}dBFS");
    foreach (double frequency in new[] { 50.0, 100.0, 200.0, 500.0, 1000.0, 2000.0, 5000.0, 10000.0, 15000.0 })
    {
        double nearest = TestRunner.TestFrequencies.OrderBy(value => Math.Abs(value - frequency)).First();
        Console.WriteLine($"LAYOUT {nearest,7:F0}Hz three={three.Curve[nearest],8:F2}dBr one={one.Curve[nearest],8:F2}dBr delta={three.Curve[nearest] - one.Curve[nearest],8:F2}dB");
    }
    return;
}
if (args.Contains("--reference-check", StringComparer.OrdinalIgnoreCase))
{
    var runner = new TestRunner(engine)
    {
        UseLogSweepFrequencyResponse = true,
        UseCombinedMultitoneFrequencyResponse = false,
        LogSweepDurationSeconds = 131072.0 / 44100.0,
        LogSweepVerificationRuns = 1,
        EvaluationMaxFrequencyHz = 20000.0,
        RestrictFeqToEvaluationRange = false,
        PlaybackFrequencyScale = 1.0,
        MinimumInputSignalDbFs = -40.0,
        EnableNoiseDiagnostics = true,
        AmbientNoiseMaxRetries = 0,
        FeqMicrophoneCalibration = calibration,
        FreqResponseToleranceDb = 3.0
    };
    runner.OnLogMessage += (source, message) => Console.WriteLine($"REFERENCE [{source}] {message}");
    Console.WriteLine("REFERENCE CHECK: Shared 44.1 kHz, 128k log sweep, same pilot level and pre/post noise guards as Auto Test");
    await runner.RunTestAsync(playbackDevice, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: true);
    Console.WriteLine($"REFERENCE RESULT valid={runner.ReferenceAcquisitionValid}, reason='{runner.LastFeqInvalidReason}', signal={runner.LastSignalLevelDbFs:F1}dBFS, peak={runner.LastFeqPeakSample:F4}");
    engine.Stop();
    return;
}
if (args.Any(arg => string.Equals(arg, "--auto-three", StringComparison.OrdinalIgnoreCase)))
{
    bool noExtraHeadroom = args.Contains("--no-headroom", StringComparer.OrdinalIgnoreCase);
    bool noWarmup = !args.Contains("--warmup", StringComparer.OrdinalIgnoreCase);
    bool useLogSweep = args.Contains("--auto-log-sweep", StringComparer.OrdinalIgnoreCase);
    if (noExtraHeadroom && args.Contains("--upload", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("Diagnostic --no-headroom must not upload QA results.");
    MMDevice analogPlayback = playback.First(device => device.FriendlyName.Contains("Analog Connector 1/2", StringComparison.OrdinalIgnoreCase));
    string defaultPlaybackId = engine.TryGetWindowsDefaultPlaybackVolume()?.DeviceId ?? "";
    MMDevice? mi30Playback = playback.Where(device => device.FriendlyName.Contains("MI30 SAM", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(device => string.Equals(device.ID, defaultPlaybackId, StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault();
    if (mi30Playback == null && !args.Contains("--partial", StringComparer.OrdinalIgnoreCase))
        throw new InvalidOperationException("MI30 SAM chưa kết nối; Auto Test phải chờ đủ 3 route.");
    var routes = new List<(string Id, MMDevice Playback, int? PlaybackChannel)>
    {
        ("IO001", analogPlayback, 0),
        ("IO002", analogPlayback, 1)
    };
    if (mi30Playback != null) routes.Add(("IO003", mi30Playback, null));
    else Console.WriteLine("PARTIAL HARDWARE CHECK ONLY: MI30 SAM absent, no full-suite or upload verdict.");

    static (double RmsDbFs, double Peak) GetHeadroom(float[] samples)
    {
        double rms = samples.Length == 0 ? 0.0 : Math.Sqrt(samples.Select(value => (double)value * value).Average());
        double peak = samples.Select(value => Math.Abs((double)value)).DefaultIfEmpty().Max();
        return (20.0 * Math.Log10(Math.Max(rms, 1e-9)), peak);
    }

    string sessionName = $"MISAM_{DateTime.Now:yyyyMMdd_HHmmss}";
    string sessionFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "auto_test_captures", DateTime.Now.ToString("yyyyMMdd"), sessionName));
    Directory.CreateDirectory(sessionFolder);
    var graphPaths = new List<string>();
    var uploadSteps = new List<ServerEngine.AudioQaStepResult>();
    bool allPassed = true;
    double? micFloorReferenceDbFs = null;

    static void SaveFrequencyResponsePng(string path, string title, IReadOnlyDictionary<double, double> curve)
    {
        var plot = new Plot();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#0E0E10");
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#27272A");
        plot.Axes.Color(ScottPlot.Color.FromHex("#A1A1AA"));
        plot.Title(title);
        plot.XLabel("Frequency (Hz)");
        plot.YLabel("Relative level (dB)");
        var ordered = curve.Where(item => item.Key > 0 && double.IsFinite(item.Value)).OrderBy(item => item.Key).ToArray();
        var trace = plot.Add.Scatter(ordered.Select(item => Math.Log10(item.Key)).ToArray(), ordered.Select(item => item.Value).ToArray());
        trace.Color = ScottPlot.Color.FromHex("#10B981");
        trace.LineWidth = 3;
        trace.MarkerSize = 5;
        double[] tickHz = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 15000, 20000 };
        string[] tickLabels = { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "15k", "20k" };
        plot.Axes.Bottom.TickGenerator = new NumericManual(tickHz.Select((hz, index) => new Tick(Math.Log10(hz), tickLabels[index])).ToArray());
        plot.Axes.SetLimits(Math.Log10(19.5), Math.Log10(20500), -15, 15);
        plot.SavePng(path, 800, 450);
    }

    Console.WriteLine($"AUTO THREE: mode={(engine.UseExclusivePlayback ? "EXCL" : "Shared")}, 44.1 kHz, capture Mic 2/Right, FEQ {(useLogSweep ? "128k log sweep 20 Hz-20 kHz" : "combined multitone")}, THD 1 kHz only, warmup {(noWarmup ? "OFF" : "ON")}, extra headroom {(noExtraHeadroom ? "OFF (diagnostic only)" : "ON")}; folder='{sessionFolder}'");
    foreach ((string id, MMDevice routePlayback, int? routeChannel) in routes)
    {
        var routeTransition = System.Diagnostics.Stopwatch.StartNew();
        engine.Stop();
        engine.PlaybackSampleRate = 44100;
        engine.PlaybackChannel = routeChannel;
        engine.RecordingChannel = 1;
        engine.PlaybackVolume = 0.60;
        engine.RecordingVolume = 0.50;
        engine.ApplyWindowsPlaybackVolume(routePlayback);
        engine.ApplyWindowsRecordingVolume(recordingDevice);
        string side = routeChannel == 0 ? "Left" : routeChannel == 1 ? "Right" : "Stereo";
        Console.WriteLine($"AUTO {id}: selecting '{routePlayback.FriendlyName}' {side} -> '{recordingDevice.FriendlyName}' Mic 2/Right (Laptop Vol: 60%, In: 50%)");
        await Task.Delay(Math.Max(0, 1000 - (int)routeTransition.ElapsedMilliseconds));

        double beforeRms = double.NaN;
        double beforePeak = double.NaN;
        if (!noExtraHeadroom)
        {
            AudioEngine.DualCaptureResult before = await engine.CaptureSilenceAsync(recordingDevice, null, 2.0);
            float[] beforeStableWindow = before.DutSamples.TakeLast(Math.Min(before.DutSamples.Length, before.DutSampleRate)).ToArray();
            (beforeRms, beforePeak) = GetHeadroom(beforeStableWindow);
            Console.WriteLine($"AUTO {id} HEADROOM BEFORE: rms={beforeRms:F1}dBFS peak={beforePeak:F4}");
            micFloorReferenceDbFs ??= beforeRms;
            if (beforeRms > -55.0 || beforePeak >= 0.05)
            {
                allPassed = false;
                uploadSteps.Add(new ServerEngine.AudioQaStepResult(id, "INVALID", $"Noise floor {beforeRms:F1} dBFS / peak {beforePeak:F4} exceeds preflight threshold; no stimulus played."));
                Console.WriteLine($"AUTO {id} RESULT: INVALID preflight noise; no stimulus played");
                break;
            }
        }
        else if (micFloorReferenceDbFs.HasValue)
        {
            beforeRms = micFloorReferenceDbFs.Value;
            beforePeak = 0.0002;
            Console.WriteLine($"AUTO {id} HEADROOM: gộp noise floor bài test Left ({beforeRms:F1} dBFS) vào sweep cho toàn bộ bài test");
        }

        var runner = new TestRunner(engine)
        {
            UseLogSweepFrequencyResponse = useLogSweep,
            UseCombinedMultitoneFrequencyResponse = !useLogSweep,
            LogSweepDurationSeconds = 131072.0 / 44100.0,
            LogSweepVerificationRuns = 1,
            EvaluationMaxFrequencyHz = 20000.0,
            RestrictFeqToEvaluationRange = false,
            MultitoneDurationSeconds = 10.0,
            PlaybackFrequencyScale = 1.0,
            MinimumInputSignalDbFs = -40.0,
            BassDistortionSampleScale = 1.0,
            BassThdFrequencyHz = 80.0,
            MidThdFrequencyHz = 1000.0,
            TrebleThdFrequencyHz = 4000.0,
            ThdLimitPercent = 0.5,
            FreqResponseToleranceDb = 100.0,
            EnableNoiseDiagnostics = true,
            AutoTestOneKilohertzOnly = true,
            ReuseSuiteNoiseFloor = false,
            SuiteSharedNoiseFloorDbFs = micFloorReferenceDbFs,
            AmbientNoiseMaxRetries = 0,
            FrequencyLimits = new Dictionary<double, FrequencyLimitPoint>
            {
                [20] = new FrequencyLimitPoint(20, 0, -100, 100, false),
                [20000] = new FrequencyLimitPoint(20000, 0, -100, 100, false)
            }
        };
        var responseCurve = new Dictionary<double, double>();
        var diagnosticThd = new Dictionary<double, (double Percent, bool Validated)>();
        runner.OnLogMessage += (source, message) => Console.WriteLine($"AUTO {id} [{source}] {message}");
        runner.OnFrequencyResponsePoint += (frequency, level) => responseCurve[frequency] = level;
        runner.OnThdSpectrumReady += (frequency, _, _, thd, validated) =>
        {
            if (frequency > 0) diagnosticThd[frequency] = (thd, validated);
        };
        runner.OnNoiseAssessmentReady += assessment => Console.WriteLine(
            $"AUTO {id} NOISE: {assessment.DutMicrophone.BroadbandDb:F1}{assessment.DutMicrophone.Unit}, {assessment.Classification}");

        if (!noWarmup)
        {
            Console.WriteLine($"AUTO {id}: warmup/reference acquisition");
            await runner.RunTestAsync(routePlayback, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: true);
        }
        else Console.WriteLine($"AUTO {id}: skip full warmup acquisition");
        await Task.Delay(1000);
        bool headroomStable = noExtraHeadroom;
        for (int check = 1; !headroomStable && check <= 5; check++)
        {
            AudioEngine.DualCaptureResult settle = await engine.CaptureSilenceAsync(recordingDevice, null, 0.75);
            (double settleRms, double settlePeak) = GetHeadroom(settle.DutSamples);
            Console.WriteLine($"AUTO {id} HEADROOM SETTLE {check}/5: rms={settleRms:F1}dBFS peak={settlePeak:F4}");
            if (micFloorReferenceDbFs.HasValue && settleRms < micFloorReferenceDbFs.Value - 8.0)
                Console.WriteLine($"AUTO {id}: quiet floor during route settle ({settleRms:F1}dBFS); 1 kHz signal probe will verify the microphone");
            if (settleRms <= -55.0 && settlePeak < 0.05)
            {
                headroomStable = true;
                break;
            }
            if (check < 5) await Task.Delay(750);
        }
        if (!headroomStable)
        {
            allPassed = false;
            uploadSteps.Add(new ServerEngine.AudioQaStepResult(id, "INVALID", "Headroom không ổn định sau sweep làm nóng; không dùng dữ liệu này để kết luận THD."));
            Console.WriteLine($"AUTO {id} RESULT: INVALID unstable headroom");
            break;
        }
        responseCurve.Clear();
        diagnosticThd.Clear();
        Console.WriteLine($"AUTO {id}: main acquisition after adaptive headroom settle");
        await runner.RunTestAsync(routePlayback, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: false);

        double afterRms = double.NaN;
        if (!noExtraHeadroom)
        {
            await Task.Delay(1000);
            AudioEngine.DualCaptureResult after = await engine.CaptureSilenceAsync(recordingDevice, null, 1.0);
            (afterRms, double afterPeak) = GetHeadroom(after.DutSamples);
            Console.WriteLine($"AUTO {id} HEADROOM AFTER: rms={afterRms:F1}dBFS peak={afterPeak:F4} delta={afterRms - beforeRms:+0.0;-0.0;0.0}dB");
            if (micFloorReferenceDbFs.HasValue && afterRms < micFloorReferenceDbFs.Value - 8.0)
                Console.WriteLine($"AUTO {id}: quiet floor after measurement ({afterRms:F1}dBFS); rely on the measured 1 kHz signal quality");
            if (afterRms > -55.0 || afterPeak >= 0.05)
            {
                allPassed = false;
                uploadSteps.Add(new ServerEngine.AudioQaStepResult(id, "INVALID", $"Post-measurement noise floor {afterRms:F1} dBFS / peak {afterPeak:F4} is too high; THD result is not trustworthy."));
                Console.WriteLine($"AUTO {id} RESULT: INVALID post-measurement noise; stop before next route");
                break;
            }
        }
        foreach (KeyValuePair<double, ToneQualityMetrics> point in runner.LastToneQualities.OrderBy(item => item.Key))
            Console.WriteLine($"AUTO {id} THD {point.Key:0}Hz: {point.Value.ThdPercent:F4}% SNR={point.Value.SnrDb:F1}dB peak={point.Value.PeakSample:F4}");
        Console.WriteLine($"AUTO {id} RESULT: FEQ={runner.Steps.ElementAtOrDefault(1)?.Status}, THD={runner.ThdPassed}, invalid={runner.ThdAcquisitionInvalid}, clipping={runner.InputClippingDetected}");
        if (runner.SilentInputDetected || runner.InputClippingDetected || runner.ThdAcquisitionInvalid
            || runner.LastToneQuality is not { IsValid: true }
            || runner.Steps.ElementAtOrDefault(1)?.Status == "Invalid")
        {
            allPassed = false;
            string invalidReason = runner.SilentInputDetected
                ? $"Không nhận đủ tín hiệu từ micro (mức pilot {runner.LastSignalLevelDbFs:F1} dBFS; cần ≥ {runner.MinimumInputSignalDbFs:F1} dBFS)."
                : new[] { runner.Steps.ElementAtOrDefault(2)?.Details, runner.Steps.ElementAtOrDefault(0)?.Details,
                    runner.LastFeqInvalidReason }.FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail))
                    ?? "Acquisition invalid; no QA verdict.";
            uploadSteps.Add(new ServerEngine.AudioQaStepResult(id, "INVALID", invalidReason));
            Console.WriteLine($"AUTO {id} RESULT: INVALID acquisition ({invalidReason}); stop before next route and do not export an FEQ image as a completed test");
            break;
        }

        string graphPath = Path.Combine(sessionFolder, $"FRA-{id}.png");
        SaveFrequencyResponsePng(graphPath, $"{id} - {routePlayback.FriendlyName} ({side})", responseCurve);
        graphPaths.Add(graphPath);

        string thdText = string.Join("; ", new[] { 1000.0 }.Select(frequency =>
        {
            if (runner.LastToneQualities.TryGetValue(frequency, out ToneQualityMetrics? quality) && double.IsFinite(quality.ThdPercent))
                return $"{frequency:0} Hz = {quality.ThdPercent:F4}%";
            if (diagnosticThd.TryGetValue(frequency, out var value) && double.IsFinite(value.Percent))
                return $"{frequency:0} Hz = {value.Percent:F4}% ({(value.Validated ? "VALID" : "INVALID")})";
            if (double.IsFinite(runner.LastMeasuredThdPercent))
                return $"{frequency:0} Hz = {runner.LastMeasuredThdPercent:F4}%";
            return $"{frequency:0} Hz = N/A";
        }));
        string feqStatus = runner.Steps.ElementAtOrDefault(1)?.Status ?? "Unknown";
        bool thdPass = runner.ThdPassed;
        string stepStatus = feqStatus.Equals("Pass", StringComparison.OrdinalIgnoreCase) && thdPass ? "PASS" : "FAIL";
        allPassed &= stepStatus == "PASS";
        string details = $"Out: {routePlayback.FriendlyName} ({side}){Environment.NewLine}In: {recordingDevice.FriendlyName} (Mic 2 / Right){Environment.NewLine}Sample rate: 44100 Hz{Environment.NewLine}FEQ: {feqStatus}{Environment.NewLine}THD: {thdText}{Environment.NewLine}Headroom: {(noExtraHeadroom ? "disabled for diagnostic" : $"before {beforeRms:F1} dBFS; after {afterRms:F1} dBFS")}";
        uploadSteps.Add(new ServerEngine.AudioQaStepResult(id, stepStatus, details));
    }

    string thdTextPath = Path.Combine(sessionFolder, "THD-results.txt");
    await File.WriteAllTextAsync(thdTextPath, string.Join(Environment.NewLine + Environment.NewLine,
        uploadSteps.Select(step => $"{step.Name} [{step.Status}]{Environment.NewLine}{step.Details}")));
    Console.WriteLine($"AUTO THREE ARTIFACTS: {graphPaths.Count} FEQ PNG(s), THD text='{thdTextPath}'");

    if (routes.Count == 3 && args.Any(arg => string.Equals(arg, "--upload", StringComparison.OrdinalIgnoreCase)))
    {
        if (graphPaths.Count != routes.Count || uploadSteps.Any(step => step.Status == "INVALID"))
            throw new InvalidOperationException("Auto Test acquisition incomplete or invalid; do not upload QA artifacts.");
        ServerEngine.RememberedLogin? login = ServerEngine.GetRememberedLogin();
        if (login == null || !await ServerEngine.AuthenticateAsync(login.Account, login.Password))
            throw new InvalidOperationException("Không thể đăng nhập inventory.acnos.store bằng phiên đã lưu: " + ServerEngine.LastError);

        string autoId = AudioRouting.GenerateAutoTestId("MISAM");
        ProductResolveResult? resolved = await ServerEngine.ResolveProductAsync(autoId, autoId, "MISAM");
        ProductInfo? product = resolved?.Product ?? await ServerEngine.CheckProductStatusAsync(autoId, "MISAM");
        if (product == null)
            throw new InvalidOperationException("Không thể tạo hoặc tìm sản phẩm cho log đo: " + ServerEngine.LastError);

        bool uploaded = await ServerEngine.UploadAudioQaResultAsync(product, allPassed, uploadSteps, graphPaths, deviceReady: true, uploadSessionId: sessionName);
        if (!uploaded)
            throw new InvalidOperationException("Upload LOGS thất bại: " + ServerEngine.LastError);
        Console.WriteLine($"AUTO THREE UPLOAD SUCCESS: product='{autoId}', images={graphPaths.Count}, steps={uploadSteps.Count}, session='{sessionName}'");
    }
    engine.Stop();
    engine.StopContinuousCapture();
    return;
}
if (args.Any(arg => string.Equals(arg, "--thd-direct", StringComparison.OrdinalIgnoreCase)))
{
    AudioEngine.flagSaveFile = true;
    var runner = new TestRunner(engine)
    {
        PlaybackFrequencyScale = playbackFrequencyScale,
        MinimumInputSignalDbFs = -40.0
    };
    runner.OnLogMessage += (source, message) => Console.WriteLine($"[{source}] {message}");
    var measureMethod = typeof(TestRunner).GetMethod("MeasureDistortionPointAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    Console.WriteLine("THD DIRECT: 80 Hz, 1000 Hz, 4000 Hz; one capture per point, FEQ bypassed");
    int directPointIndex = 0;
    foreach (double frequency in new[] { 80.0, 1000.0, 4000.0 })
    {
        if (directPointIndex++ > 0)
        {
            Console.WriteLine("THD DIRECT: waiting 3000 ms for WASAPI/FastTrack to settle...");
            await Task.Delay(3000);
        }
        var measurementTask = (Task)measureMethod.Invoke(runner, new object[] { playbackDevice, recordingDevice, frequency, 0.5 })!;
        await measurementTask;
        object result = measurementTask.GetType().GetProperty("Result")!.GetValue(measurementTask)!;
        object quality = result.GetType().GetProperty("Quality")!.GetValue(result)!;
        int validRuns = (int)result.GetType().GetProperty("ValidRuns")!.GetValue(result)!;
        double thd = (double)quality.GetType().GetProperty("ThdPercent")!.GetValue(quality)!;
        double snr = (double)quality.GetType().GetProperty("SnrDb")!.GetValue(quality)!;
        double level = (double)quality.GetType().GetProperty("SignalLevelDbFs")!.GetValue(quality)!;
        double peak = (double)quality.GetType().GetProperty("PeakSample")!.GetValue(quality)!;
        Console.WriteLine($"THD DIRECT RESULT {frequency:0}Hz: validRuns={validRuns}, THD={thd:F4}%, SNR={snr:F1}dB, level={level:F1}dBFS, peak={peak:F4}");
    }
    return;
}
if (args.Any(arg => string.Equals(arg, "--thd-three", StringComparison.OrdinalIgnoreCase)))
{
    var runner = new TestRunner(engine)
    {
        UseLogSweepFrequencyResponse = false,
        PlaybackFrequencyScale = playbackFrequencyScale,
        MinimumInputSignalDbFs = -40.0,
        BassThdFrequencyHz = 80.0,
        MidThdFrequencyHz = 1000.0,
        TrebleThdFrequencyHz = 4000.0,
        FeqMicrophoneCalibration = calibration,
        FreqResponseToleranceDb = 100.0,
        FrequencyLimits = new Dictionary<double, FrequencyLimitPoint>
        {
            [20] = new FrequencyLimitPoint(20, 0, -100, 100, false),
            [20000] = new FrequencyLimitPoint(20000, 0, -100, 100, false)
        }
    };
    runner.OnLogMessage += (source, message) => Console.WriteLine($"[{source}] {message}");
    runner.OnThdSpectrumReady += (frequency, frequencies, _, thd, validated) =>
        Console.WriteLine($"THD EVENT {frequency:0}Hz: {thd:F4}%, validated={validated}, spectrumPoints={frequencies.Length}");
    Console.WriteLine("THD THREE-POINT EXACT PIPELINE: 80 Hz, 1000 Hz, 4000 Hz; one capture per point");
    await runner.RunTestAsync(playbackDevice, recordingDevice, runFeqThreeTimes: false, referenceAcquisition: false);
    foreach (KeyValuePair<double, ToneQualityMetrics> point in runner.LastToneQualities.OrderBy(item => item.Key))
        Console.WriteLine($"THD RESULT {point.Key:0}Hz: valid={point.Value.IsValid}, THD={point.Value.ThdPercent:F4}%, THD+N={point.Value.ThdNPercent:F4}%, level={point.Value.SignalLevelDbFs:F1}dBFS, peak={point.Value.PeakSample:F4}");
    Console.WriteLine($"THD THREE-POINT PASS={runner.ThdPassed}, inputClipping={runner.InputClippingDetected}");
    return;
}
string[] names = { "Bass", "Mid", "Treble" };
AudioEngine.DualCaptureResult silence = await engine.CaptureSilenceAsync(recordingDevice, null, 1.5);
var noiseByFrequency = new Dictionary<double, double>();
for (int bandIndex = 0; bandIndex < playbackBands.Length; bandIndex++)
    foreach (var point in DspProcessor.CalculateMultitoneResponse(silence.DutSamples, silence.DutSampleRate, playbackBands[bandIndex]))
        noiseByFrequency[point.Key] = point.Value;
Console.WriteLine($"NOISE capture samples={silence.DutSamples.Length}, rate={silence.DutSampleRate}");
var curves = new List<Dictionary<double, double>>();
for (int run = 1; run <= 2; run++)
{
    var curve = new Dictionary<double, double>();
    Console.WriteLine($"RUN {run}/2");
    for (int bandIndex = 0; bandIndex < measuredBands.Length; bandIndex++)
    {
        double captureSeconds = names[bandIndex] == "Treble" ? 10.0 : 6.0;
        double analysisSeconds = names[bandIndex] == "Treble" ? 8.0 : 4.0;
        float[] captured = await engine.PlayAndRecordAsync(
            playbackDevice, recordingDevice, SignalType.Multitone, 1100.0, captureSeconds,
            multitoneFrequencies: playbackBands[bandIndex], multitoneSampleScale: scale,
            recordingChannel: 0);
        int rate = engine.RecordingSampleRate;
        int count = Math.Min(captured.Length, (int)(rate * analysisSeconds));
        int start = Math.Max(0, (captured.Length - count) / 2);
        float[] centered = captured.Skip(start).Take(count).ToArray();
        Dictionary<double, double> response = DspProcessor.CalculateMultitoneResponse(centered, rate, playbackBands[bandIndex]);
        double pilot = response[1100.0] - (calibration?.GetGainCorrectionDb(1100.0) ?? 0.0);
        foreach (double frequency in measuredBands[bandIndex])
            curve[frequency] = response[frequency] - (calibration?.GetGainCorrectionDb(frequency) ?? 0.0) - pilot;
        double peak = centered.Max(sample => Math.Abs((double)sample));
        var snrs = measuredBands[bandIndex].Where(frequency => frequency >= 50 && frequency <= 15000)
            .Select(frequency => (Frequency: frequency, Snr: response[frequency] - noiseByFrequency[frequency]))
            .OrderBy(item => item.Snr).ToArray();
        Console.WriteLine($"  {names[bandIndex]} capture={captureSeconds:F0}s/analyze={analysisSeconds:F0}s, pilot={response[1100.0]:F2} dBFS, capturePeak={peak:F4}, samples={captured.Length}, rate={rate}, SNR<12dB={snrs.Count(item => item.Snr < 12.0)}/{snrs.Length}, worst={snrs[0].Frequency:F0}Hz/{snrs[0].Snr:F1}dB");
    }
    double oneKhz = curve[1000.0];
    foreach (double frequency in curve.Keys.ToArray()) curve[frequency] -= oneKhz;
    curves.Add(curve);
}

Console.WriteLine("REPEATABILITY RUN1 vs RUN2");
foreach (var band in TestRunner.TestFrequencyBands)
{
    var differences = band.Frequencies.Where(frequency => frequency >= 50 && frequency <= 15000)
        .Select(frequency => (Frequency: frequency, Difference: Math.Abs(curves[0][frequency] - curves[1][frequency])))
        .OrderByDescending(item => item.Difference).ToArray();
    var worst = differences.First();
    Console.WriteLine($"  {band.Name}: max={worst.Difference:F2} dB at {worst.Frequency:F0} Hz, avg={differences.Average(item => item.Difference):F2} dB");
    if (band.Name == "Treble")
        foreach (var item in differences.Take(8))
            Console.WriteLine($"    {item.Frequency:F0} Hz: run1={curves[0][item.Frequency]:F2}, run2={curves[1][item.Frequency]:F2}, delta={item.Difference:F2} dB");
}

if (!args.Any(arg => string.Equals(arg, "--sequential-treble", StringComparison.OrdinalIgnoreCase)))
    return;

Console.WriteLine("SEQUENTIAL TREBLE REPEATABILITY");
var sequentialCurves = new List<Dictionary<double, double>>();
double[] sequentialFrequencies = TestRunner.TestFrequencyBands.Single(band => band.Name == "Treble").Frequencies;
engine.PlaybackVolume = scale * 0.85 / playbackBands[2].Length;
Console.WriteLine($"  sine playback volume={engine.PlaybackVolume:P2} to match one treble multitone component");
for (int run = 1; run <= 2; run++)
{
    var curve = new Dictionary<double, double>();
    float[] pilotCapture = await engine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, 1100.0, 0.8, recordingChannel: 0);
    double pilotRms = DspProcessor.CalculateRms(pilotCapture, Math.Max(0, pilotCapture.Length - 24000), Math.Min(24000, pilotCapture.Length));
    double pilotDb = 20.0 * Math.Log10(pilotRms + 1e-9) - (calibration?.GetGainCorrectionDb(1100.0) ?? 0.0);
    foreach (double frequency in sequentialFrequencies.Where(frequency => frequency <= 15000))
    {
        float[] captured = await engine.PlayAndRecordAsync(playbackDevice, recordingDevice, SignalType.Sine, frequency, 0.55, recordingChannel: 0);
        int count = Math.Min(captured.Length, 24000);
        double rms = DspProcessor.CalculateRms(captured, captured.Length - count, count);
        curve[frequency] = 20.0 * Math.Log10(rms + 1e-9) - (calibration?.GetGainCorrectionDb(frequency) ?? 0.0) - pilotDb;
    }
    sequentialCurves.Add(curve);
    Console.WriteLine($"  sequential run {run}/2 complete; pilot={pilotDb:F2} dBFS(calibrated)");
}
var sequentialDifferences = sequentialCurves[0].Keys.Select(frequency => (
    Frequency: frequency,
    Difference: Math.Abs(sequentialCurves[0][frequency] - sequentialCurves[1][frequency])))
    .OrderByDescending(item => item.Difference).ToArray();
Console.WriteLine($"  Treble sequential: max={sequentialDifferences[0].Difference:F2} dB at {sequentialDifferences[0].Frequency:F0} Hz, avg={sequentialDifferences.Average(item => item.Difference):F2} dB");
foreach (var item in sequentialDifferences.Take(8))
    Console.WriteLine($"    {item.Frequency:F0} Hz: run1={sequentialCurves[0][item.Frequency]:F2}, run2={sequentialCurves[1][item.Frequency]:F2}, delta={item.Difference:F2} dB");
