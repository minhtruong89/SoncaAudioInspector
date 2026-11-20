using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector;

internal static class ServerTestListChecks
{
    public static async Task Run(string output, Action<bool, string> check)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = key.ExportSubjectPublicKeyInfoPem();
        var originals = new[] {
            C360ModelDefinition.Create(),
            Acoustic3ModelDefinition.Create("Synthetic playback", "Synthetic capture", micReady: true, lineReady: true, usbReady: true),
            new ModelConfig { model = "Generic web model", testItems = new() { InOut = new() {
                Devices = new() { Input = new() { ["OUT"] = "Synthetic playback" }, Output = new() { ["IN"] = "Synthetic capture" } },
                Tests = Enumerable.Range(1, 3).Select(i => new TestConfig { id = $"IO{i:000}", name = $"Test {i}", PlaybackOut = "OUT", RecordingIn = "IN", Enable = true }).ToList()
            } } }
        };
        var edited = originals.Select(model => JsonSerializer.Deserialize<ModelConfig>(JsonSerializer.Serialize(model))!).ToArray();
        foreach (var model in edited)
        {
            var tests = model.testItems.InOut.Tests;
            var removed = tests[3 < tests.Count ? 3 : 1];
            tests.Remove(removed);
            tests[0].Enable = false;
            var added = JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(removed))!;
            added.id = "IO099"; added.name = "Bài thêm từ web"; added.ReferenceTestId = "WEB-NEW"; added.Enable = true;
            tests.Add(added);
        }
        ActiveAudioConfig Sign(ModelConfig model, long serial)
        {
            string json = JsonSerializer.Serialize(model);
            var revision = new ActiveAudioConfig(model.model, "web-" + model.model + "-" + serial, serial, json,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), "");
            return revision with { Signature = Convert.ToBase64String(key.SignData(AudioConfigSignature.Payload(revision),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
        }
        var store = new ActiveAudioConfigStore(Path.Combine(output, "server-test-list-cache"), publicKey);
        var revisions = edited.Select(model => Sign(model, 1)).ToArray();
        var online = await ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(revisions), store, publicKey);
        var baseline = new CheckingConfig { models = originals.ToList() };
        string before = JsonSerializer.Serialize(baseline);
        var merged = online.Merge(baseline);
        check(online.Warning.Length == 0 && merged.models.Count == originals.Length, "Web ACTIVE loads every model without a model-name allowlist");
        foreach (var model in edited)
        {
            var actual = merged.models.Single(m => m.model == model.model);
            check(JsonSerializer.Serialize(actual) == JsonSerializer.Serialize(model), model.model + ": full signed test list replaces old tests including additions/deletions/disabled entries");
            check(!actual.testItems.InOut.Tests[0].IsEnabled && actual.testItems.InOut.Tests[^1].IsEnabled,
                model.model + ": web enable false/true survives download and parse");
            if (C360ModelDefinition.Matches(actual.testItems.InOut))
            {
                var io = actual.testItems.InOut;
                var endpoints = io.Devices.Input.Values.Distinct().Select((name, i) => new C360ModelEndpoint("out-" + i, name, false))
                    .Concat(io.Devices.Output.Values.Distinct().Select((name, i) => new C360ModelEndpoint("in-" + i, name, true))).ToArray();
                var plan = C360ModelPlan.Create(io, endpoints, new("synthetic-out", "synthetic-usb", "synthetic-in"));
                check(plan.Stages.SequenceEqual(io.Tests.Where(t => t.IsEnabled).Select(C360ModelDefinition.Stage)),
                    model.model + ": actual runner plan follows enabled server tests in server order");
            }
        }
        check(JsonSerializer.Serialize(baseline) == before, "Web ACTIVE merge does not mutate the signed base configuration");
        foreach (var model in edited) { model.testItems.InOut.Tests[^1].Enable = false; model.testItems.InOut.Tests[0].Enable = true; }
        var updated = await ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(edited.Select(model => Sign(model, 2)).ToArray()), store, publicKey);
        check(updated.Merge(baseline).models.All(m => m.testItems.InOut.Tests[0].IsEnabled && !m.testItems.InOut.Tests[^1].IsEnabled),
            "Newer approved web revision updates enable state for every model");
        var offline = await ActiveAudioConfigSync.RefreshAsync(() => throw new IOException("Synthetic offline"), store, publicKey);
        check(offline.Warning.Contains("OFFLINE") && JsonSerializer.Serialize(offline.Merge(baseline)) == JsonSerializer.Serialize(updated.Merge(baseline)),
            "Offline cache retains the complete verified web test lists and flags");
    }
}
