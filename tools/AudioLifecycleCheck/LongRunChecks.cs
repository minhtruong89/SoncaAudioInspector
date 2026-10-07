using System.Net;
using System.IO;
using System.Net.Http;
using System.Reflection;
using SoncaAudioInspector;

internal static class LongRunChecks
{
    public static async Task RunAsync(string outputDirectory)
    {
        string root = Path.Combine(outputDirectory, "long-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LevelChecks();
        AtomicChecks(root);
        StorageChecks(root);
        await HttpChecks();
        var expiry = typeof(ServerEngine).GetMethod("IsAppSessionUsable", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Usable(string token, DateTimeOffset? time) => (bool)expiry.Invoke(null, new object?[] { token, time })!;
        Require(!Usable("", DateTimeOffset.UtcNow.AddDays(1)) && !Usable("expired", DateTimeOffset.UtcNow.AddSeconds(-1))
            && !Usable("near-expiry", DateTimeOffset.UtcNow.AddSeconds(10)) && !Usable("unknown-expiry", null)
            && Usable("valid", DateTimeOffset.UtcNow.AddHours(1)), "App-token expiry guard failed.");
        Console.WriteLine("Long-run checks passed: bounded meter, stale sessions, atomic config/backup, pending QA/retention, auth-only HTTP replay and token expiry. No hardware/network used.");
    }

    private static void LevelChecks()
    {
        var meter = new LiveLevelBuffer();
        long old = meter.Start();
        meter.Push(new float[] { 1 }, old); meter.Push(new float[] { 0, 0, 0 }, old);
        Require(meter.TryRead(out double peak, out double rms, out bool clip, out bool invalid)
            && peak == 0 && Math.Abs(rms - 20 * Math.Log10(.5)) < 1e-10 && clip && !invalid,
            "Coalescing lost clipping/peak or computed an unweighted RMS.");
        long current = meter.Start();
        meter.Push(new float[] { 1 }, old);
        Require(!meter.TryRead(out _, out _, out _, out _), "Old-session callback reached the current meter.");
        meter.Push(new float[] { .1f, float.NaN }, current);
        Require(meter.TryRead(out _, out _, out _, out invalid) && invalid, "Invalid capture appeared valid.");
        meter.Stop(); meter.Push(new float[] { 1 }, current);
        Require(!meter.TryRead(out _, out _, out _, out _) && !meter.IsCurrent(current), "Stopped meter replayed queued samples.");
        current = meter.Start();
        var samples = Enumerable.Repeat(.125f, 32).ToArray();
        meter.Push(samples, current);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100_000; index++) meter.Push(samples, current);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Require(allocated < 4096, "Live meter allocated queued audio/UI objects as callbacks accumulated.");
        Require(meter.TryRead(out _, out rms, out _, out _) && Math.Abs(rms - 20 * Math.Log10(.125)) < 1e-10,
            "Long blocked-UI interval corrupted the aggregate.");
        var weak = PushTemporary(meter, current);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(!weak.IsAlive, "Meter retained a captured sample array.");
        Console.WriteLine($"Meter: 100,000 callbacks, allocated {allocated} bytes, no sample array retained.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PushTemporary(LiveLevelBuffer meter, long session)
    {
        var samples = new float[32768]; meter.Push(samples, session); return new WeakReference(samples);
    }

    private static void AtomicChecks(string root)
    {
        string path = Path.Combine(root, "config.json");
        AtomicFile.WriteAllText(path, "{\"value\":1}");
        AtomicFile.WriteAllText(path, "{\"value\":2}");
        File.WriteAllText(path, "{broken");
        Require(AtomicFile.ReadJson<Dictionary<string, int>>(path)!["value"] == 1, "Corrupted config did not recover its backup.");
        AtomicFile.WriteAllText(path, "{\"value\":3}");
        Require(!AtomicFile.WriteIfUnchanged(path, "old-content", "{\"value\":4}"), "Background refresh overwrote a newer edit.");
        Require(AtomicFile.WriteIfUnchanged(path, "{\"value\":3}", "{\"value\":4}"), "Unchanged cache could not refresh.");
        Parallel.For(0, 24, value => AtomicFile.WriteAllText(path, "{\"value\":" + value + "}"));
        Require(AtomicFile.ReadJson<Dictionary<string, int>>(path)!.ContainsKey("value")
            && !Directory.EnumerateFiles(root, "*.tmp").Any(), "Concurrent writes left corrupt content or orphan temporary files.");
        File.Delete(path);
        Require(AtomicFile.ReadJson<Dictionary<string, int>>(path) != null, "Missing primary config did not recover a backup.");
        var readConfig = typeof(SplashWindow).GetMethod("ReadValidConfiguration", BindingFlags.NonPublic | BindingFlags.Static)!;
        string cache = Path.Combine(root, "checking_config.json");
        File.WriteAllText(cache, "{\"models\":[{\"model\":\"test\"}]}");
        Require(readConfig.Invoke(null, new object[] { cache }) != null, "Valid cache was rejected.");
        File.WriteAllText(cache, "{\"models\":[]}");
        Require(readConfig.Invoke(null, new object[] { cache }) == null, "Empty cache bypassed configuration download.");
        File.WriteAllText(cache + ".bak", "{\"models\":[{\"model\":\"recovered\"}]}");
        File.WriteAllText(cache, "broken-json");
        Require(((string?)readConfig.Invoke(null, new object[] { cache }))?.Contains("recovered") == true,
            "Startup cache did not recover a valid backup.");
    }

    private static void StorageChecks(string root)
    {
        string qaRoot = Path.Combine(root, "fail data");
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        string Create(string name, bool complete, DateTimeOffset time)
        {
            string folder = Path.Combine(qaRoot, name); Directory.CreateDirectory(folder);
            string graph = Path.Combine(folder, "FRA-test.png"); File.WriteAllText(graph, "test-fixture");
            var pending = new PendingAudioQaUpload("https://unit-test.invalid", "staff", "product", true,
                new[] { new ServerEngine.AudioQaStepResult("FEQ", "Pass", "snapshot") }, new[] { Path.GetFileName(graph) },
                true, name, time);
            string marker = LocalQaStorage.SavePending(pending, new[] { graph }, qaRoot)!;
            var restored = LocalQaStorage.ReadPending(qaRoot, marker);
            Require(restored != null && restored.ProductId == pending.ProductId && restored.UploadSessionId == pending.UploadSessionId
                && restored.Passed == pending.Passed && restored.Steps.SequenceEqual(pending.Steps)
                && restored.GraphFiles.SequenceEqual(pending.GraphFiles), "Pending measurement snapshot changed.");
            if (complete) LocalQaStorage.MarkUploaded(qaRoot, marker, time);
            return folder;
        }
        string completed = Create("old-completed", true, old);
        File.WriteAllText(Path.Combine(completed, "calibration.json"), "preserve");
        string fresh = Create("fresh-completed", true, DateTimeOffset.UtcNow.AddDays(-2));
        string waiting = Create("old-pending", false, old);
        string unmarked = Path.Combine(qaRoot, "legacy"); Directory.CreateDirectory(unmarked);
        File.WriteAllText(Path.Combine(unmarked, "FRA-legacy.png"), "preserve");
        Require(LocalQaStorage.PruneUploadedGraphs(qaRoot) == 1
            && File.Exists(Path.Combine(completed, "calibration.json"))
            && File.Exists(Path.Combine(fresh, "FRA-test.png")) && File.Exists(Path.Combine(waiting, "FRA-test.png"))
            && File.Exists(Path.Combine(unmarked, "FRA-legacy.png")), "Retention deleted pending, legacy, calibration or recent data.");
        string markerPath = LocalQaStorage.FindPending(qaRoot).Single();
        Require(!LocalQaStorage.ReadPending(qaRoot, markerPath)!.SafeToRetry, "Unknown delivery status was considered safe to replay.");
        Require(LocalQaStorage.ReadPending(root, Path.Combine(root, "outside", ".pending-audio-qa.json")) == null,
            "Nonexistent pending snapshot was accepted.");
        Console.WriteLine("QA retention: only a confirmed upload older than 30 days was removed; pending/unmarked/calibration files preserved.");
    }

    private static async Task HttpChecks()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        int recoveries = 0;
        using var result = await AuthorizedHttpRetry.SendAsync(client, Request, _ => { recoveries++; return Task.FromResult(true); });
        Require(result.IsSuccessStatusCode && handler.Calls == 3 && recoveries == 2
            && handler.Bodies.All(body => body == "saved-snapshot"), "Authentication recovery lost the body or failed to rebuild requests.");
        using var rejected = new ScriptedHandler(HttpStatusCode.Unauthorized);
        using var rejectedClient = new HttpClient(rejected);
        using var limited = await AuthorizedHttpRetry.SendAsync(rejectedClient, Request, _ => Task.FromResult(true));
        Require(rejected.Calls == 3 && limited.StatusCode == HttpStatusCode.Unauthorized, "Authentication replay was unbounded.");
        using var serverError = new ScriptedHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        using var serverClient = new HttpClient(serverError);
        using var error = await AuthorizedHttpRetry.SendAsync(serverClient, Request, _ => throw new Exception("Unexpected recovery"));
        Require(serverError.Calls == 1, "Uncertain POST delivery retried on a server error.");
        using var offline = new ScriptedHandler(HttpStatusCode.OK) { Offline = true };
        using var offlineClient = new HttpClient(offline);
        try { using var unexpected = await AuthorizedHttpRetry.SendAsync(offlineClient, Request, _ => Task.FromResult(true));
            throw new InvalidOperationException("Simulated transport failure was swallowed."); }
        catch (HttpRequestException) { Require(offline.Calls == 1, "Uncertain POST delivery retried after disconnect."); }
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://unit-test.invalid/qa") { Content = new StringContent("saved-snapshot") };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Offline { get; init; }
        public List<string> Bodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Offline) throw new HttpRequestException("Simulated network disconnect");
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statuses[Math.Min(Calls - 1, statuses.Length - 1)]) { Content = new StringContent("{}") };
        }
    }
}
