using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public static class AudioSampleRateSetup
{
    public const int StandardRate = 44100;

    public static bool EnsureStandard(MMDevice device, out string message)
    {
        try { return EnsureStandard(device.FriendlyName, () => FastTrackDeviceSetup.ReadMixSampleRate(device.ID),
            rate => WindowsEndpointPolicy.SetSampleRate(device.ID, rate), out message); }
        catch (Exception ex) { message = "Không truy cập được thiết bị để đồng bộ 44.1 kHz: " + ex.Message; return false; }
    }

    internal static bool EnsureStandard(string name, Func<int> readRate, Action<int> setRate, out string message)
    {
        message = "";
        try
        {
            int before = readRate();
            if (before == StandardRate) return true;
            setRate(StandardRate);
            int actual = readRate();
            if (actual != StandardRate) throw new InvalidOperationException($"Windows vẫn báo {actual} Hz.");
            message = $"Đã đồng bộ {name}: {before} → {actual} Hz; giữ nguyên bit depth, kênh, volume/mute.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Chưa đồng bộ được {name} về 44.1 kHz: {ex.Message}";
            return false;
        }
    }
}
