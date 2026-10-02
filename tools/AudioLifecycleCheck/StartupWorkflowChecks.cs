using System.Net;
using System.Net.Http;
using System.Reflection;
using SoncaAudioInspector;

internal static class StartupWorkflowChecks
{
    public static async Task RunAsync()
    {
        Require(AutoTestRetryPolicy.Decide(1, false, true, false, true) == AutoTestAttemptDecision.AwaitReconnect, "Invalid first capture did not request reconnect.");
        Require(AutoTestRetryPolicy.Decide(1, false, true, false, false) == AutoTestAttemptDecision.Fail, "Valid limit failure was mislabeled unstable.");
        Require(AutoTestRetryPolicy.Decide(2, false, true, false, true) == AutoTestAttemptDecision.Fail, "Second invalid capture not finalized.");
        Require(AutoTestRetryPolicy.Decide(2, true, true, false, false) == AutoTestAttemptDecision.Pass, "Recovered measurement cannot pass.");
        Require(AutoTestRetryPolicy.Decide(1, true, true, false, false) == AutoTestAttemptDecision.Pass, "First pass retried unnecessarily.");
        Require(AutoTestRetryPolicy.Decide(1, false, false, false, false) == AutoTestAttemptDecision.InvalidConfiguration, "Missing reference counted as hardware failure.");
        Require(AutoTestRetryPolicy.Decide(1, false, true, true, true) == AutoTestAttemptDecision.Cancelled, "Cancellation counted as fail.");
        foreach (var status in new[] { "RETRY", "FAIL", "INVALID", "WAITING" })
            Require(!AutoTestRetryPolicy.CanRemovePassedDevice(true, false, new[] { "PASS", status }), "Bluetooth removed before entire suite passed.");
        Require(!AutoTestRetryPolicy.CanRemovePassedDevice(true, true, new[] { "PASS" }), "Bluetooth removed after cancel.");
        Require(!AutoTestRetryPolicy.CanRemovePassedDevice(true, false, Array.Empty<string>()), "Empty suite removed devices.");
        Require(AutoTestRetryPolicy.CanRemovePassedDevice(true, false, new[] { "PASS", "PASS", "PASS" }), "Completed passing suite did not allow cleanup.");
        var nameCheck = typeof(AudioEngine).Assembly.GetType("SoncaAudioInspector.BluetoothDeviceRemoval")!
            .GetMethod("IsMi30SamName", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (string name in new[] { "MI30 SAM", "Headphones (MI30 SAM)", "Headphones (2- MI30 SAM)", "MI30_SAM" })
            Require((bool)nameCheck.Invoke(null, new object[] { name })!, $"Missed MI30 SAM: {name}");
        foreach (string name in new[] { "MI30 SAMPLE", "FastTrack Pro", "MI30 SAM OTHER", "D'AURIS 500", "Headphones (Realtek)" })
            Require(!(bool)nameCheck.Invoke(null, new object[] { name })!, $"Unrelated Bluetooth candidate accepted: {name}");

        using var recovering = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var recoveringClient = new HttpClient(recovering);
        var requests = new List<HttpRequestMessage>();
        var progress = new List<string>();
        using var response = await TransientHttpRetry.SendAsync(recoveringClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://unit-test.invalid/login") { Content = new StringContent("test-only") };
            requests.Add(request); return request;
        }, new InlineProgress(progress.Add), delay: (_, _) => Task.CompletedTask);
        Require(response.IsSuccessStatusCode && recovering.Calls == 3 && requests.Distinct().Count() == 3 && progress.Count == 2,
            "Transient login retry did not rebuild/await requests or report waiting.");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest })
        {
            using var handler = new ScriptedHandler(status, HttpStatusCode.OK);
            using var client = new HttpClient(handler);
            using var failed = await TransientHttpRetry.SendAsync(client, Request, delay: (_, _) => Task.CompletedTask);
            Require(failed.StatusCode == status && handler.Calls == 1, "Permanent credential error retried.");
        }
        using var busy = new ScriptedHandler(HttpStatusCode.ServiceUnavailable);
        using var busyClient = new HttpClient(busy);
        using var exhausted = await TransientHttpRetry.SendAsync(busyClient, Request, delay: (_, _) => Task.CompletedTask);
        Require(busy.Calls == 3 && exhausted.StatusCode == HttpStatusCode.ServiceUnavailable, "Retries did not stop at three.");
        using var transport = new ScriptedHandler(HttpStatusCode.OK) { TransportFailures = 1 };
        using var transportClient = new HttpClient(transport);
        using var recovered = await TransientHttpRetry.SendAsync(transportClient, Request, delay: (_, _) => Task.CompletedTask);
        Require(transport.Calls == 2 && recovered.IsSuccessStatusCode, "Temporary network failure not recovered.");
        using var throttled = new ScriptedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK) { RetryAfterSeconds = 3 };
        using var throttledClient = new HttpClient(throttled);
        TimeSpan actualDelay = TimeSpan.Zero;
        using var afterThrottle = await TransientHttpRetry.SendAsync(throttledClient, Request,
            delay: (wait, _) => { actualDelay = wait; return Task.CompletedTask; });
        Require(afterThrottle.IsSuccessStatusCode && actualDelay.TotalSeconds == 3, "Server Retry-After was ignored.");
        throttled.RetryAfterSeconds = 60;
        using var longThrottle = new ScriptedHandler(HttpStatusCode.TooManyRequests) { RetryAfterSeconds = 60 };
        using var longThrottleClient = new HttpClient(longThrottle);
        using var limited = await TransientHttpRetry.SendAsync(longThrottleClient, Request,
            delay: (_, _) => throw new InvalidOperationException("Long rate limit was retried prematurely."));
        Require(longThrottle.Calls == 1, "Long server rate limit caused premature retries.");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        int priorCalls = transport.Calls;
        try
        {
            using var cancelled = await TransientHttpRetry.SendAsync(transportClient, Request, cancellationToken: cts.Token);
            throw new InvalidOperationException("Cancellation ignored.");
        }
        catch (OperationCanceledException) { Require(transport.Calls == priorCalls, "Cancelled request was sent."); }
        Require(AudioSessionDiagnostics.IsDeviceInUse(new Exception("Wrapper", new System.Runtime.InteropServices.COMException("Busy", unchecked((int)0x8889000A)))), "Nested WASAPI busy error was hidden.");
        Require(!AudioSessionDiagnostics.IsDeviceInUse(new Exception("Different error")), "Unrelated device error marked as exclusive busy.");
        Console.WriteLine("Retry/cleanup and startup network checks passed (fake HTTP only; no unpair operation).");
    }

    private static HttpRequestMessage Request() => new(HttpMethod.Post, "https://unit-test.invalid/login");
    private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int TransportFailures { get; set; }
        public int? RetryAfterSeconds { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (TransportFailures-- > 0) throw new HttpRequestException("Simulated offline");
            var response = new HttpResponseMessage(statuses[Math.Min(Calls - 1, statuses.Length - 1)]) { Content = new StringContent("{}") };
            if (RetryAfterSeconds.HasValue) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(RetryAfterSeconds.Value));
            return Task.FromResult(response);
        }
    }
}
