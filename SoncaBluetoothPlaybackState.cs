using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public sealed record BluetoothPlaybackLevel(string Name, double Percent, bool Muted);

public static class SoncaBluetoothPlaybackState
{
    /// <summary>Read-only Windows playback level, independent of the speaker's physical Master.</summary>
    public static IReadOnlyList<BluetoothPlaybackLevel> Read(IReadOnlyList<BluetoothAudioPeer> peers)
    {
        var levels = new List<BluetoothPlaybackLevel>();
        if (peers.Count == 0) return levels;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (peers.Any(p => device.FriendlyName.Contains(p.Name, StringComparison.OrdinalIgnoreCase)))
                        levels.Add(new(device.FriendlyName, device.AudioEndpointVolume.MasterVolumeLevelScalar * 100, device.AudioEndpointVolume.Mute));
                }
            }
        }
        catch { /* No numeric fallback: an unavailable level is not 0%. */ }
        return levels;
    }
}
