namespace SoncaAudioInspector;

/// <summary>Recognises a capture that heard nothing from the analog sound card (cable on another output pair than the config says,
/// handoff run 2026-11-18: IO004 −77 dBFS / SNR ≈ 0, IO006 SNR 1.5 dB), as opposed to a real measurement result.</summary>
public static class C360SignalCheck
{
    public const double MinimumUsableSnrDb = 10;
    public static bool IsMissingAnalogSignal(C360ElectronicStage stage, C360ElectronicResult result)
    {
        if (!C360ModelDefinition.UsesAnalogPlayback(stage) || result.Status is "PASS" or "RUNNING") return false;
        if (result.Status == "NO_SIGNAL") return true;
        if (result.Measurement is { } m)
            return MeasurementSignalStatus.MissingStimulus(m) || double.IsFinite(m.EstimatedSnrDb) && m.EstimatedSnrDb < MinimumUsableSnrDb;
        if (result.FrequencyShift is { IsValid: false } shift)
        {
            var carriers = shift.Off.Concat(shift.On).ToArray();
            return carriers.All(c => !double.IsFinite(c.NarrowBandSnrDb) || c.NarrowBandSnrDb < MinimumUsableSnrDb);
        }
        return false;
    }
    /// <summary>IO009 (owner 2026-11-19): FBX off never howled up to the maximum loop gain, i.e. the measurement mic hears the
    /// speaker too weakly. The operator moves the mic closer and the stage is captured again.</summary>
    public static bool IsWeakFeedbackLoop(C360ElectronicStage stage, C360ElectronicResult result)
        => stage == C360ElectronicStage.MicFeedbackGain && result.FeedbackGain is { Off.Howled: false };
    public static bool NeedsOperator(C360ElectronicStage stage, C360ElectronicResult result)
        => IsMissingAnalogSignal(stage, result) || IsWeakFeedbackLoop(stage, result);
    public static string Describe(C360ElectronicResult result) => result.FeedbackGain is { Off.Howled: false } loop
        ? $"FBX tắt chưa hú tới gain vòng {loop.MaximumGainDb:+0;-0} dB"
        : result.Measurement is { } m
        ? $"mức thu {m.SignalLevelDbFs:F1} dBFS · SNR {m.EstimatedSnrDb:F1} dB"
        : result.FrequencyShift != null ? "không thấy tone thử ở ngõ thu" : "không có tín hiệu thử";
}

/// <summary>Owner 2026-11-19: the app syncs the server config with the real wiring instead of asking for the sound card A/B button.
/// Each port (LINE IN, MIC IN) has the endpoint the config names. When a stage on that port hears nothing, the other analog
/// endpoints of the config are tried; the one that carries the signal becomes the port's endpoint for the rest of the session.
/// Unambiguous because the suite isolates the paths: MIC stages mute music/line, LINE stages mute the mic.
/// A port that moves frees its endpoint for the port that was configured on the new one (two cables swapped).</summary>
public sealed class C360PortSync(IReadOnlyDictionary<string, string> configured, IReadOnlyDictionary<string, string>? names = null)
{
    private readonly Dictionary<string, string> _override = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = [];
    public IReadOnlyDictionary<string, string> Configured => configured;
    public string Name(string endpoint) => names?.GetValueOrDefault(endpoint) is { Length: > 0 } name ? name : endpoint;
    public string EndpointFor(string port) => _override.TryGetValue(port, out var id) ? id : configured.GetValueOrDefault(port, "");
    public C360ElectronicSetup Apply(C360ElectronicStage stage, C360ElectronicSetup setup, TestConfig? test)
    {
        if (!C360ModelDefinition.UsesAnalogPlayback(stage) || test == null || !_override.TryGetValue(test.PlaybackOut, out var id)) return setup;
        return setup with { AnalogPlaybackId = id, LinePlaybackId = id };
    }
    public void Confirm(string port, string endpoint) { if (endpoint.Length > 0) _confirmed[port] = endpoint; }
    /// <summary>Other analog endpoints of the config, never one another port has already proven to be its own.</summary>
    public IReadOnlyList<string> Alternatives(string port)
    {
        string current = EndpointFor(port);
        var taken = _confirmed.Where(p => !p.Key.Equals(port, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return configured.Values.Where(id => id.Length > 0 && !id.Equals(current, StringComparison.OrdinalIgnoreCase) && !taken.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public string Adopt(string port, string endpoint)
    {
        string previous = EndpointFor(port);
        _override[port] = endpoint; _confirmed[port] = endpoint;
        string note = $"{port}: tín hiệu thật đi qua {Name(endpoint)} (cấu hình ghi {Name(configured.GetValueOrDefault(port, previous))})";
        foreach (var other in configured.Keys.Where(p => !p.Equals(port, StringComparison.OrdinalIgnoreCase)
            && EndpointFor(p).Equals(endpoint, StringComparison.OrdinalIgnoreCase) && !_confirmed.ContainsKey(p)).ToArray())
        {
            _override[other] = previous;
            note += $"; {other} đổi sang {Name(previous)}";
        }
        Notes.Add(note);
        return note;
    }
}

/// <summary>Wraps one acquisition bench. An analog stage that hears nothing is first synced to the real wiring (C360PortSync);
/// only when no configured sound card output reaches the speaker the operator is asked to check the cable. IO009 asks to move
/// the measurement mic closer. BLE state is already set by the suite, so only the capture repeats.</summary>
public sealed class C360NoSignalRetryBench(IC360ElectronicBench inner,
    Func<C360ElectronicStage, C360ElectronicResult, int, CancellationToken, Task<bool>> askRetry, int maximumRetries = 5,
    C360PortSync? sync = null)
    : IC360ElectronicBench, IC360AcquisitionPreparation
{
    public string DeviceIdentity => inner.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => inner.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => inner.WriteAsync(register, value, token);
    public void StopAudio() => inner.StopAudio();
    public Task<C360ElectronicSetup> PrepareAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
        => inner is IC360AcquisitionPreparation prepare ? prepare.PrepareAsync(stage, Synced(stage, setup), token) : Task.FromResult(Synced(stage, setup));
    public Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var synced = Synced(stage, setup);
        return RetryAsync(stage, synced, () => inner.CaptureAsync(stage, synced, token), token);
    }
    public Task<C360ElectronicResult> CapturePreparedAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var synced = Synced(stage, setup);
        return RetryAsync(stage, synced, () => inner is IC360AcquisitionPreparation prepare ? prepare.CapturePreparedAsync(stage, synced, token) : inner.CaptureAsync(stage, synced, token), token);
    }
    private C360ElectronicSetup Synced(C360ElectronicStage stage, C360ElectronicSetup setup) => sync?.Apply(stage, setup, setup.TestConfiguration) ?? setup;
    private async Task<C360ElectronicResult> RetryAsync(C360ElectronicStage stage, C360ElectronicSetup setup, Func<Task<C360ElectronicResult>> first,
        CancellationToken token)
    {
        var result = await first();
        string? port = setup.TestConfiguration?.PlaybackOut;
        bool analog = sync != null && port != null && C360ModelDefinition.UsesAnalogPlayback(stage);
        if (analog)
        {
            // A port is proven only by a stage that heard its signal (IO009: FBX off howled, so the loop carries sound).
            if (!C360SignalCheck.IsMissingAnalogSignal(stage, result))
            {
                if (stage != C360ElectronicStage.MicFeedbackGain || result.FeedbackGain is { Off.Howled: true }) sync!.Confirm(port!, setup.PlaybackId(stage));
            }
            else
                foreach (string alternative in sync!.Alternatives(port!))
                {
                    token.ThrowIfCancellationRequested();
                    inner.StopAudio();
                    // A prepared setup is consumed by the first capture; CaptureAsync prepares the moved route again.
                    var moved = setup with { AnalogPlaybackId = alternative, LinePlaybackId = alternative };
                    var tried = await inner.CaptureAsync(stage, moved, token);
                    if (C360SignalCheck.IsMissingAnalogSignal(stage, tried)) continue;
                    result = tried with { Summary = "TỰ ĐỒNG BỘ NGÕ SOUND CARD — " + sync.Adopt(port!, alternative) + ". " + tried.Summary };
                    break;
                }
        }
        for (int retry = 1; retry <= maximumRetries && C360SignalCheck.NeedsOperator(stage, result); retry++)
        {
            token.ThrowIfCancellationRequested();
            inner.StopAudio();
            if (!await askRetry(stage, result, retry, token)) break;
            var previous = result;
            var again = Synced(stage, setup);
            result = await inner.CaptureAsync(stage, again, token);
            if (!C360SignalCheck.NeedsOperator(stage, result))
            {
                if (analog) sync!.Confirm(port!, again.PlaybackId(stage));
                result = result with { Summary = (stage == C360ElectronicStage.MicFeedbackGain
                    ? $"Đo được sau khi đặt mic gần loa hơn (lần thử lại {retry}; trước đó {C360SignalCheck.Describe(previous)}). "
                    : $"Có tín hiệu sau khi kiểm tra cáp (lần thử lại {retry}; trước đó {C360SignalCheck.Describe(previous)}). ") + result.Summary };
            }
        }
        return result;
    }
}
