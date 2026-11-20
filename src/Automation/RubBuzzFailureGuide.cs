using System.IO;

namespace SoncaAudioInspector;

public sealed record RubBuzzFailureHelp(string Title, string Message, IReadOnlyList<string> Steps)
{
    public string Body => Message + "\n\nCách xử lý:\n" + string.Join("\n", Steps.Select((s, i) => $"{i + 1}. {s}"));
    public string Compact => Title + " → " + Steps.FirstOrDefault();
}

/// <summary>Operator guidance for every way a Rè acquisition can stop before a result. Classifies the whole exception chain.</summary>
public static class RubBuzzFailureGuide
{
    private const int DeviceInUse = unchecked((int)0x8889000A), ExclusiveNotAllowed = unchecked((int)0x8889000E),
        DeviceInvalidated = unchecked((int)0x88890004), UnsupportedFormat = unchecked((int)0x88890008), AccessDenied = unchecked((int)0x80070005);

    private static readonly string[] PrivacySteps =
    [
        "Settings → Privacy & security → Microphone: bật \"Microphone access\" và \"Let desktop apps access your microphone\".",
        "Sound (mmsys.cpl) → Recording → mic → Properties → Levels: bỏ Mute, mức mic > 0.",
        "Kiểm tra cáp/jack mic; cắm lại USB rồi bấm Làm mới thiết bị và chọn lại ngõ thu.",
        "Tắt Audio enhancements của mic (Properties → Advanced).",
    ];

    /// <summary>Null when nothing should pop up: a cancel, or a noisy floor the operator already handled in the background dialog.</summary>
    public static RubBuzzFailureHelp? Explain(Exception error, Func<string>? activeSessions = null)
    {
        var chain = Chain(error).ToArray();
        string message = error.Message;
        if (chain.Any(e => e is OperationCanceledException)) return null;
        if (chain.OfType<RubBuzzBackgroundException>().FirstOrDefault() is { } background)
        {
            if (background.Check.IsDigitalSilence) return new("Mic không có tín hiệu (im lặng số)", message, PrivacySteps);
            if (background.Check.CanRaiseAllowance) return null;
            return new("Mic không trả dữ liệu", message, [
                "Cắm lại mic (cổng USB trực tiếp trên máy, không qua hub) rồi bấm Làm mới thiết bị và chọn lại ngõ thu.",
                "Đóng app khác đang dùng mic, rồi đo lại.",
                "Vẫn lỗi: cài lại/đổi driver mic hoặc thử mic khác."]);
        }
        if (chain.Any(e => e.HResult == DeviceInUse))
        {
            string sessions = activeSessions?.Invoke() ?? "";
            return new("Mic/loa đang bị ứng dụng khác giữ", message + (sessions.Length > 0 ? "\n\nĐang mở:\n" + sessions : ""), [
                "Đóng app đang dùng mic/loa: Zalo, Teams, Zoom, tab Chrome/Edge có mic, OBS, ghi âm.",
                "Đóng trang Settings → Sound (thanh đo mức mic của trang này giữ mic).",
                "Đóng cửa sổ SoncaAudioInspector thứ hai nếu có.",
                "Đo lại."]);
        }
        if (chain.Any(e => e.HResult == ExclusiveNotAllowed))
            return new("Windows không cho dùng mic ở chế độ Exclusive", message, [
                "Sound (mmsys.cpl) → Recording → mic → Properties → Advanced.",
                "Tick \"Allow applications to take exclusive control of this device\" và \"Give exclusive mode applications priority\", bấm OK.",
                "Đo lại."]);
        if (chain.Any(e => e is NotSupportedException || e.HResult == UnsupportedFormat))
            return new("Mic không nhận định dạng thu Exclusive", message, [
                "Sound (mmsys.cpl) → Recording → mic → Properties → Advanced → Default Format: chọn 16 hoặc 24 bit, 44100 hoặc 48000 Hz; bấm OK.",
                "Tắt Audio enhancements của mic, cắm lại USB, bấm Làm mới thiết bị.",
                "Vẫn lỗi: mic này không hỗ trợ WASAPI Exclusive; dùng mic đo khác (ví dụ UMIK-1)."]);
        if (chain.Any(e => e.HResult == DeviceInvalidated)
            || chain.OfType<IOException>().Any(e => e.Message.Contains("ngắt", StringComparison.Ordinal) || e.Message.Contains("Mất phiên", StringComparison.Ordinal)))
            return new("Mất kết nối thiết bị âm thanh", message, [
                "Kiểm tra cáp USB; cắm trực tiếp vào máy (hub hay rớt khi đổi nguồn).",
                "Bấm Làm mới thiết bị, chọn lại ngõ phát/thu đúng tên.",
                "Đo lại."]);
        if (chain.Any(e => e is UnauthorizedAccessException || e.HResult == AccessDenied))
            return new("Không có quyền dùng mic", message, PrivacySteps);
        if (chain.Any(e => e is TimeoutException))
            return new("Thiết bị không trả dữ liệu đúng hạn", message, [
                "Kiểm tra cáp USB và nguồn của thiết bị.",
                "Bấm Làm mới thiết bị, chọn lại ngõ phát/thu.",
                "Khởi động lại app nếu vẫn lỗi."]);
        if (Has(chain, "Rate thu đổi", "định dạng thu đã đổi"))
            return new("Định dạng thu thay đổi giữa các lượt", message, [
                "Tắt Audio enhancements của mic (Windows có thể đổi sample rate khi bật).",
                "Đặt Default Format của mic về 44100 hoặc 48000 Hz rồi đo lại.",
                "Không cắm/rút thiết bị âm thanh khác trong lúc đo."]);
        if (Has(chain, "đã đổi", "đổi giữa các bước", "đổi trong lượt", "đổi trước lượt"))
            return new("Route/âm lượng thay đổi trong lúc đo", message, [
                "Không chỉnh ngõ phát/thu, kênh, sample rate, âm lượng Windows hay model trong lúc đo.",
                "Tắt app/phím tắt tự đổi âm lượng (driver headset, phần mềm loa).",
                "Đo lại."]);
        if (Has(chain, "Mono audio"))
            return new("Đang bật Mono audio của Windows", message, [
                "Settings → Accessibility → Audio: tắt \"Mono audio\".",
                "Đo lại."]);
        if (chain.Any(e => e is ArgumentException) && Has(chain, "kHz"))
            return new("Sample rate không phù hợp", message, [
                "Chọn sample rate phát 44,1 kHz hoặc 48 kHz trong Audio Routing.",
                "Đặt Default Format của mic ≥ 32 kHz (khuyến nghị 44100/48000 Hz).",
                "Đo lại."]);
        if (Has(chain, "Đang đo", "đang bận", "chuyển model"))
            return new("Thiết bị đang bận", message, [
                "Chờ bài đo/Auto Test hiện tại xong hoặc bấm Dừng.",
                "Đo lại."]);
        if (Has(chain, "Chưa chọn ngõ"))
            return new("Chưa chọn ngõ phát/thu", message, [
                "Chọn ngõ phát và ngõ thu trong Audio Routing.",
                "Đo lại."]);
        return new("Không hoàn tất đo Rè", message, [
            "Kiểm tra kết nối loa/mic và ngõ phát/thu đang chọn.",
            "Đo lại.",
            "Vẫn lỗi: chụp màn hình thông báo này gửi kỹ thuật."]);
    }

    private static bool Has(IEnumerable<Exception> chain, params string[] fragments)
        => chain.Any(e => fragments.Any(f => e.Message.Contains(f, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Chain)) yield return inner;
        }
    }
}
