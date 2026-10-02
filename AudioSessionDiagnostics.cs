using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace SoncaAudioInspector;

public sealed record ExternalAudioSession(string DeviceName, string Direction, string ProcessName, uint ProcessId, bool SignalDetected);
public sealed record AudioUsageReport(DateTime CheckedAt, IReadOnlyList<ExternalAudioSession> Sessions, IReadOnlyList<string> UnreadableDevices)
{
    public string Summary => Sessions.Count > 0
        ? $"Âm thanh: {Sessions.Count} phiên của ứng dụng khác đang dùng ngõ phát/thu ({CheckedAt:HH:mm:ss})."
        : UnreadableDevices.Count > 0 ? "Âm thanh: chưa kiểm tra được hết thiết bị."
        : $"Âm thanh: chưa thấy phiên phát/thu hoạt động của ứng dụng khác ({CheckedAt:HH:mm:ss}).";

    public string Details => string.Join("\n", Sessions.Select(s =>
        $"• {s.ProcessName} (PID {s.ProcessId}) — {s.Direction}: {s.DeviceName}"
        + (s.SignalDetected ? " — có tín hiệu" : " — phiên đang mở")))
        + (UnreadableDevices.Count > 0 ? "\nChưa đọc được: " + string.Join(", ", UnreadableDevices) : "")
        + "\nĐóng hoặc dừng ứng dụng âm thanh khác trước khi đo. Danh sách phiên không xác nhận được mọi khóa độc quyền; app sẽ báo nếu WASAPI từ chối mở thiết bị.";
}

public static class AudioSessionDiagnostics
{
    // Read session metadata/meters only. Never initialize a playback/capture
    // stream just to probe startup: that can disturb the FastTrack ADC state.
    public static AudioUsageReport Scan()
    {
        var sessions = new List<ExternalAudioSession>();
        var unreadable = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                using (device)
                {
                    string name = "Thiết bị âm thanh";
                    try
                    {
                        name = device.FriendlyName;
                        var manager = device.AudioSessionManager;
                        try
                        {
                        var collection = manager.Sessions;
                        for (int i = 0; i < collection.Count; i++)
                        {
                            using var session = collection[i];
                            if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                            uint pid = session.GetProcessID;
                            if (pid == Environment.ProcessId) continue;
                            string processName = pid == 0 ? "Âm thanh hệ thống" : $"Tiến trình {pid}";
                            try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; } catch { }
                            bool signal = false;
                            try { signal = session.AudioMeterInformation.MasterPeakValue > 0.000001f; } catch { }
                            sessions.Add(new ExternalAudioSession(name, device.DataFlow == DataFlow.Render ? "phát" : "thu", processName, pid, signal));
                        }
                        }
                        finally { manager.Dispose(); }
                    }
                    catch { unreadable.Add(name); }
                }
            }
        }
        catch { unreadable.Add("Windows Audio"); }
        return new AudioUsageReport(DateTime.Now, sessions, unreadable);
    }

    public static bool IsDeviceInUse(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current.HResult == unchecked((int)0x8889000A)) return true; // AUDCLNT_E_DEVICE_IN_USE
        return false;
    }
}
