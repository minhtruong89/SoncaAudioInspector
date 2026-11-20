using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed record RubBuzzTestRoute(TestConfig Test, string PlaybackName, string RecordingName,
    string? PlaybackId, string? RecordingId, bool CanPrepare)
{
    public bool IsMatched => PlaybackId != null && RecordingId != null;
    public string Label => AudioQaRouteDetails.Format(Test.PlaybackOut, PlaybackName, RecordingName);
    public bool SameEndpoints(RubBuzzTestRoute? other) => other is { IsMatched: true }
        && ReferenceEquals(Test, other.Test) && PlaybackId == other.PlaybackId && RecordingId == other.RecordingId;

    public static TestConfig? DefaultTest(IEnumerable<TestConfig> tests)
    {
        var enabled = tests.Where(t => t.IsEnabled).ToArray();
        return enabled.FirstOrDefault(t => t.SpeakerStage == C360ModelDefinition.AcousticRubBuzzStage)
            ?? enabled.FirstOrDefault(t => t.UsesAcousticRubBuzz) ?? enabled.FirstOrDefault();
    }

    public static RubBuzzTestRoute Resolve(InOutConfig model, TestConfig test, IReadOnlyList<C360ModelEndpoint> endpoints)
    {
        var output = ResolveEndpoint(model.Devices.Input.GetValueOrDefault(test.PlaybackOut), false);
        var input = ResolveEndpoint(model.Devices.Output.GetValueOrDefault(test.RecordingIn), true);
        bool canPrepare = C360ModelDefinition.Matches(model) && C360ModelDefinition.UsesAnalogPlayback(C360ModelDefinition.Stage(test));
        return new(test, output.Name, input.Name, output.Id, input.Id, canPrepare);

        (string Name, string? Id) ResolveEndpoint(string? name, bool capture)
        {
            if (string.IsNullOrWhiteSpace(name)) return ("Chưa cấu hình thiết bị", null);
            string key = C360ModelPlan.EndpointKey(name);
            var candidates = endpoints.Where(e => e.Capture == capture).ToArray();
            var matches = candidates.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0) matches = candidates.Where(e => C360ModelPlan.EndpointKey(e.Name) == key).ToArray();
            if (matches.Length == 0) matches = candidates.Where(e => C360ModelPlan.EndpointKey(e.Name).Contains(key, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 ? (matches[0].Name, matches[0].Id) : (name, null);
        }
    }
}

public partial class AudioRouting
{
    private sealed class RubBuzzPreparation(RubBuzzTestRoute route)
    {
        public RubBuzzTestRoute Route { get; } = route;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Func<CancellationToken, Task>> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    private RubBuzzPreparation? _rubBuzzPreparation;

    private RubBuzzTestRoute? ReadRubBuzzRoute()
    {
        if (_activeInOutConfig is not { } model || _measurementDetailsWindow?.RubBuzz.SelectedRouteTest is not { } test
            || !model.Tests.Contains(test)) return null;
        if (C360EndpointProvider is { } fake) return RubBuzzTestRoute.Resolve(model, test, fake());
        using var devices = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device) endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        return RubBuzzTestRoute.Resolve(model, test, endpoints);
    }

    private async Task PrepareManualRubBuzzAsync(CancellationToken token)
    {
        var route = ReadRubBuzzRoute();
        if (route is not { IsMatched: true, CanPrepare: true }) throw new InvalidOperationException("Chưa có cặp ngõ phát/thu hợp lệ cho bài đã chọn.");
        if (_rubBuzzPreparation != null) throw new InvalidOperationException("Đang chuẩn bị hoặc khôi phục loa.");
        var preparation = new RubBuzzPreparation(route);
        _rubBuzzPreparation = preparation;
        preparation.Completion = HoldManualRubBuzzRouteAsync(preparation);
        using var cancel = token.Register(preparation.Lifetime.Cancel);
        if (await Task.WhenAny(preparation.Ready.Task, preparation.Completion) == preparation.Completion)
            await preparation.Completion;
        await preparation.Ready.Task;
    }

    private async Task HoldManualRubBuzzRouteAsync(RubBuzzPreparation preparation)
    {
        var token = preparation.Lifetime.Token;
        try
        {
            CloseRoutingScope();
            using var persistence = ResultPersistencePolicy.BeginMeasurement();
            using var workflow = TryBeginRoutingWorkflow() ?? throw new InvalidOperationException("Đang đo/chuyển model; chờ hoàn tất.");
            StopRoutingHeadroomMonitor();
            var model = _activeInOutConfig;
            var route = preparation.Route;
            var ble = SoncaBleManager.Instance;
            if (!await ble.EnsureConnectedAsync(PreferredModelController(model), token, userInitiated: true,
                expectedModelId: ExpectedFirmwareModel(model), selectStrongestByModel: model.BleSelectStrongestByModel,
                namePrefix: model.EffectiveBleNamePrefix))
                throw new InvalidOperationException("Chưa xác nhận được loa BLE đúng model.");
            Acoustic3ModelDefinition.RequireFirmware(model, ble.FirmwareInfoPayload);
            var bench = new C360AudioBench(_audioEngine, ble, Path.Combine(GetUserDataDirectory(), "manual-rubbuzz"));
            var config = JsonSerializer.Deserialize<TestConfig>(JsonSerializer.Serialize(route.Test))!;
            config.TestVolume = config.ModelVolume = ModelTestVolume.Merge(model.TestVolume, config.TestVolume);
            var setup = C360ModelPlan.Configure(C360MachineDefaults() with
            { AnalogPlaybackId = route.PlaybackId!, LinePlaybackId = route.PlaybackId, LineOutCaptureId = route.RecordingId! }, config);
            var stage = C360ModelDefinition.Stage(config);
            var scoped = new C360StageActionBench(bench, async (_, ct) =>
            {
                if (!route.SameEndpoints(ReadRubBuzzRoute())) throw new InvalidOperationException("Route vừa đổi; chọn lại bài đo.");
                using var devices = new MMDeviceEnumerator();
                using var playback = devices.GetDevice(route.PlaybackId!);
                using var recording = devices.GetDevice(route.RecordingId!);
                using var levels = new WindowsAudioLevels(playback, recording, config);
                preparation.Ready.TrySetResult();
                var measure = await preparation.Start.Task.WaitAsync(ct);
                await measure(ct);
            }) { Configuration = config };
            await new C360ElectronicSuite().RunAsync(scoped, setup, token: token, stages: [stage]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            preparation.Ready.TrySetCanceled(token);
            if (preparation.Start.Task.IsCompletedSuccessfully) throw;
        }
        catch (Exception ex) { AppendLog("Chuẩn bị loa Rè", ex.Message); throw; }
        finally
        {
            if (ReferenceEquals(_rubBuzzPreparation, preparation)) _rubBuzzPreparation = null;
        }
    }

    private async Task ReleaseManualRubBuzzAsync()
    {
        if (_rubBuzzPreparation is not { } preparation) return;
        preparation.Lifetime.Cancel();
        try { await preparation.Completion; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowRubBuzzFailure(ex); }
    }
}
