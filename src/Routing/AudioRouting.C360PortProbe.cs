namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private async Task<C360ModelPlan> ProbeC360PortsAsync(IC360ElectronicBench bench, C360ModelPlan plan,
        C360AutoTestResumeSession? session, IReadOnlyDictionary<ushort, ushort> baseline, CancellationToken token)
    {
        _c360PortSync = session?.PortSync ?? CreateC360PortSync(plan, bench);
        if (session != null) session.PortSync = _c360PortSync;
        var line = plan.Configuration.Tests.FirstOrDefault(t => t.IsEnabled && C360ModelDefinition.Stage(t) == C360ElectronicStage.LineIn);
        if (_c360PortSync is not { } sync || line == null) return plan;
        var report = await C360PortProbe.RunAsync(bench, plan.SetupFor(line), sync, baseline,
            message => AppendLog("Dò sound card", message), token);
        if (report != null) C360Popup(report, "Đã kiểm tra ngõ", ModernMessageBox.MessageBoxType.Info);

        // Reference preflight and endpoint-loss handling need the same route as capture.
        var playback = plan.PlaybackIds.ToDictionary(p => p.Key, p => p.Value);
        foreach (var test in plan.Configuration.Tests.Where(t => t.IsEnabled))
        {
            var stage = C360ModelDefinition.Stage(test);
            string endpoint = sync.EndpointFor(test.PlaybackOut);
            if (C360ModelDefinition.UsesAnalogPlayback(stage) && endpoint.Length > 0) playback[stage] = endpoint;
        }
        return _runningC360Plan = plan with { PlaybackIds = playback };
    }
}
