namespace SoncaAudioInspector;

/// <param name="PlaybackChannel">One based channel, or null for L+R.</param>
public sealed record MeasurementRouteInfo(string PlaybackId, string PlaybackName, int? PlaybackChannel, int PlaybackRate,
    string RecordingName, int? RecordingChannel, int RecordingRate)
{
    public static string Format(MeasurementRouteInfo? route) => route == null
        ? "Chưa chọn ngõ phát/thu — chọn tại Cấu hình đường âm thanh"
        : $"Ngõ phát: {route.PlaybackName} · kênh {route.PlaybackChannel?.ToString() ?? "L+R"} · {route.PlaybackRate} Hz Shared │ "
            + $"Ngõ thu: {route.RecordingName} · kênh {route.RecordingChannel?.ToString() ?? "Trộn"} · {route.RecordingRate} Hz · Shared (bài Rè thu Exclusive)";
}
