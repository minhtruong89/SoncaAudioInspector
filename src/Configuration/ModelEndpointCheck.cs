using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed record WindowsEndpointName(string Id, string Name, bool Capture);

public sealed record EndpointNameIssue(string TestId, string Port, string ConfiguredName, bool Capture, string Problem, IReadOnlyList<string> Suggestions)
{
    public string Describe() => $"- {TestId} · {(Capture ? "Ngõ THU" : "Ngõ PHÁT")} {Port} = \"{ConfiguredName}\": {Problem}"
        + (Suggestions.Count > 0 ? "\n    Windows đang có: " + string.Join(" | ", Suggestions) : "");
}

/// <summary>
/// Compares the configured Windows device names of the enabled tests with the endpoints Windows has right now, using the
/// matching rules of the measurement routes (C360/ACOUSTIC 3: exact name, else a unique "contains" match; other models: the first
/// "contains" match). Every problem is reported at once, with similar names, so a misspelt name is told apart from an unplugged device.
/// </summary>
public static class ModelEndpointCheck
{
    private static readonly HashSet<string> CommonTokens = new(StringComparer.OrdinalIgnoreCase)
        { "usb", "audio", "device", "speakers", "speaker", "microphone", "headset", "headphones" };

    public static IReadOnlyList<EndpointNameIssue> Check(InOutConfig config, IReadOnlyList<WindowsEndpointName> endpoints)
    {
        bool electronic = C360ModelDefinition.Matches(config);
        var issues = new List<EndpointNameIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var test in config.Tests.Where(t => t.IsEnabled))
        {
            // BT is found from the BLE speaker address and USB A only appears after the BLE source switch.
            if (!(electronic && test.SpeakerStage is "Bluetooth" or "UsbA"))
                Add(test, test.PlaybackOut, config.Devices.Input.GetValueOrDefault(test.PlaybackOut), false);
            Add(test, test.RecordingIn, config.Devices.Output.GetValueOrDefault(test.RecordingIn), true);
        }
        return issues;

        void Add(TestConfig test, string port, string? name, bool capture)
        {
            if (!seen.Add((capture ? "in\n" : "out\n") + port + "\n" + name)) return;
            if (Problem(name, capture, endpoints, electronic, out var suggestions) is string problem)
                issues.Add(new(test.id, port, name ?? "", capture, problem, suggestions));
        }
    }

    /// <summary>Null when the name resolves to exactly the endpoint the measurement would use.</summary>
    public static string? Problem(string? name, bool capture, IReadOnlyList<WindowsEndpointName> endpoints, bool electronic, out IReadOnlyList<string> suggestions)
    {
        var flow = endpoints.Where(e => e.Capture == capture).ToArray();
        string kind = capture ? "thu" : "phát";
        suggestions = Array.Empty<string>();
        if (flow.Length == 0) return $"Windows không có thiết bị {kind} nào đang hoạt động (thiết bị/hub USB đã rớt kết nối hoặc chưa cắm).";
        if (string.IsNullOrWhiteSpace(name)) { suggestions = Suggest("", flow); return "chưa nhập tên thiết bị Windows."; }
        var matches = Matches(name, flow, electronic);
        if (matches.Length == 1 || matches.Length > 1 && !electronic) return null;
        if (matches.Length > 1)
        {
            suggestions = matches.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return $"khớp {matches.Length} thiết bị cùng lúc; nhập tên đầy đủ để chọn đúng một.";
        }
        suggestions = Suggest(name, flow);
        return $"Windows không có thiết bị {kind} tên này — sai tên, hoặc thiết bị chưa cắm / đã rớt kết nối USB.";
    }

    public static WindowsEndpointName[] Matches(string name, IEnumerable<WindowsEndpointName> flow, bool electronic)
    {
        var all = flow.ToArray();
        var exact = all.Where(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (electronic && exact.Length > 0) return exact;
        var contains = all.Where(e => e.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (contains.Length > 0) return contains;
        // Same device with another Windows instance number ("7-" on the PC that wrote the config, "8-" here): same rule as C360ModelPlan.
        string key = C360ModelPlan.EndpointKey(name);
        var same = all.Where(e => C360ModelPlan.EndpointKey(e.Name) == key).ToArray();
        return same.Length > 0 ? same : all.Where(e => C360ModelPlan.EndpointKey(e.Name).Contains(key, StringComparison.Ordinal)).ToArray();
    }

    private static string[] Suggest(string name, IReadOnlyList<WindowsEndpointName> flow)
    {
        static string[] Tokens(string text) => text.Split(new[] { ' ', '(', ')', '-', '_', '.', ',', '[', ']', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2).ToArray();
        var wanted = Tokens(name);
        var ranked = flow.Select(e => (e.Name, Score: Tokens(e.Name).Count(t => wanted.Contains(t, StringComparer.OrdinalIgnoreCase)) * 10
                + Tokens(e.Name).Count(t => !CommonTokens.Contains(t) && wanted.Contains(t, StringComparer.OrdinalIgnoreCase)) * 5))
            .Where(e => e.Score > 0).OrderByDescending(e => e.Score).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        return ranked.Length > 0 ? ranked : flow.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
    }

    public static string Describe(IReadOnlyList<EndpointNameIssue> issues)
        => string.Join("\n", issues.Select(i => i.Describe()))
            + "\nCắm lại USB, hoặc sửa tên thiết bị ở /config.";

    /// <summary>Active Windows render and capture endpoints right now.</summary>
    public static WindowsEndpointName[] ReadActive()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<WindowsEndpointName>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device)
            {
                try { endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture)); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        return endpoints.ToArray();
    }
}
