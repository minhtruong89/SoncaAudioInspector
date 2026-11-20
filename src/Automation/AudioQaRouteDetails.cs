namespace SoncaAudioInspector;

public static class AudioQaRouteDetails
{
    public static string Format(string speakerInput, string playback, string recording, string? recordingPort = null)
        => $"PHÁT → {speakerInput} loa · {playback}   |   THU ← {WithPort(recordingPort, recording)}";

    public static string Route(AutoTestCaseItem row, InOutConfig? model)
        => Format(row.Config.PlaybackOut,
            ActualOrConfigured(row.Acquisition?.PlaybackDeviceName, model?.Devices.Input.GetValueOrDefault(row.Config.PlaybackOut)),
            ActualOrConfigured(row.Acquisition?.RecordingDeviceName, model?.Devices.Output.GetValueOrDefault(row.Config.RecordingIn)
                ?? row.Config.RecordingIn), row.Config.RecordingIn);

    // The capture port (LINE OUT, Measurement Mic) stays visible unless the endpoint name already carries it.
    private static string WithPort(string? port, string device)
        => string.IsNullOrWhiteSpace(port) || device.Contains(port, StringComparison.OrdinalIgnoreCase) ? device : $"{port} · {device}";

    private static string ActualOrConfigured(string? actual, string? configured)
        => string.IsNullOrWhiteSpace(actual) ? configured ?? "Chưa xác định" : actual;

    public static string Header(AutoTestCaseItem row, InOutConfig? model)
    {
        string rates = row.Acquisition is { } a ? $"\nRate phát app / endpoint / Default: {a.PlaybackSampleRate} / {a.PlaybackEndpointSampleRate?.ToString() ?? "chưa ghi"} / {a.PlaybackDefaultSampleRate?.ToString() ?? "chưa ghi"} Hz\nRate thu thực / Default: {a.RecordingSampleRate} / {a.RecordingDefaultSampleRate?.ToString() ?? "chưa ghi"} Hz"
            + (a.SampleRateContinuationAccepted ? "\nĐã chọn tiếp tục ở sample rate khác chuẩn 44100 Hz." : "") : "";
        return $"{row.Id} — {row.Name}\n{Route(row, model)}" + rates;
    }
}
