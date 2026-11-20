using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using SoncaAudioInspector;

internal static class C360StageChecks
{
    public static async Task Run(string output, string configFile, string setupFile, string id, bool verifyReverb = false, bool reverbOff = false, string? referenceDirectory = null, bool findReference = true,string modelName="", bool trial=false)
    {
        Directory.CreateDirectory(output);
        var configuration = JsonSerializer.Deserialize<CheckingConfig>(File.ReadAllText(configFile))!;
        C360ModelDefinition.EnsureSeed(configuration);
        File.WriteAllText(Path.Combine(output, "migrated-model.json"), JsonSerializer.Serialize(configuration, C360Reference.Json));
        var model = configuration.models.Single(m => C360ModelDefinition.Matches(m.testItems?.InOut) && (modelName.Length==0 || m.model==modelName));
        string measurementId = (findReference ? "REFERENCE_" : "AUTOTEST_") + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        Console.WriteLine("Measurement ID: " + measurementId);
        if (id!="*" && !model.testItems.InOut.Tests.Any(t => t.id == id)) throw new Exception("Unknown test ID");
        if(id!="*")foreach (var test in model.testItems.InOut.Tests) test.Enable = test.id == id;
        if (trial) { SessionModelConfigurations.Current.Apply(model); SessionModelConfigurations.Current.SelectedModel = model.model; }
        using var persistence = ResultPersistencePolicy.BeginMeasurement();
        if (reverbOff) foreach (var test in model.testItems.InOut.Tests.Where(t => t.SpeakerStage == "MicEffects"))
            test.ImpulseEffects = (test.ImpulseEffects ?? new()) with { ExpectReverb = false };
        var saved = JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(setupFile))!;
        using var manager = new SoncaBleManager(); manager.LogMessage += Console.WriteLine;
        if (!await manager.EnsureConnectedAsync(saved.BleControllerAddress, userInitiated: true)) throw new Exception("BLE unavailable; no stimulus emitted.");
        Acoustic3ModelDefinition.RequireFirmware(model.testItems.InOut,manager.FirmwareInfoPayload);
        using var enumeration = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumeration.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        File.WriteAllText(Path.Combine(output, "endpoints-before-source.json"), JsonSerializer.Serialize(endpoints, C360Reference.Json));
        var plan = C360ModelPlan.Create(model.testItems.InOut, endpoints, saved);
        using var engine = new AudioEngine(); AudioEngine.flagSaveFile = false;
        var bench = new C360AudioBench(engine, manager, output) { VerifyReverbAlways = verifyReverb };
        var baseline = await bench.ReadAsync(default);
        try
        {
            IC360ElectronicBench acquisition = bench;
            if (referenceDirectory != null)
            {
                using var input = enumeration.GetDevice(plan.Setup.LineOutCaptureId);
                using var client = input.AudioClient;
                string mixerKey = SoncaMixerControls.Fingerprint(baseline,manager.FirmwareInfoPayload,0) + ";CAPTURE_RATE:" + client.MixFormat.SampleRate;
                acquisition = new C360ReferenceBench(bench, plan, model.model, referenceDirectory, mixerKey, findReference, 3,
                    run => new C360AudioBench(engine, manager, Path.Combine(output, "reference-run-" + run)));
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var results = await new C360ElectronicSuite().RunAsync(plan.Bind(acquisition), plan.Setup, stages: plan.Stages,token:timeout.Token);
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { id, measurementId, results = results.Select(r => new { r.StageName, r.Status, r.Summary }) }, C360Reference.Json));
            File.WriteAllText(Path.Combine(output, "measured-results.json"), JsonSerializer.Serialize(new { measurementId, source = "bench-cli", model = model.model, isTrial = trial, findReference,
                config = model, results = results.Select(r => new { r.StageName, r.Status, r.Summary,
                    response = r.Measurement?.FrequencyResponse.Select(p => new { hz = p.FrequencyHz, db = p.LevelDb + r.Measurement.ExcitationRmsDbFs }),
                    reference = r.Reference?.Curve, referenceKey = r.Reference?.Key }) }, C360Reference.Json));
            foreach(var result in results)Console.WriteLine(result.Stage+" "+result.Status+": "+result.Summary);
        }
        finally
        {
            var restored = await bench.ReadAsync(default);
            var changed = baseline.Where(p => restored.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key.ToString("X4")).ToArray();
            File.WriteAllText(Path.Combine(output, "restoration.json"), JsonSerializer.Serialize(new { registers = baseline.Count, changed }));
            if (changed.Length > 0) throw new Exception("DSP restoration incomplete");
            Console.WriteLine("PASS DSP restoration: " + baseline.Count);
        }
    }
}
