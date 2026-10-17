namespace SoncaAudioInspector;

/// <summary>NRPN controls verified against the C360 INFO/config response.</summary>
public static class SoncaBleMixerProfile
{
    public const ushort MicVolume = 0x6400, MusicVolume = 0x6103, MasterVolume = 0x6102,
        EchoVolume = 0x6402, ReverbVolume = 0x6403, LiveVolume = 0x6467;
    public const ushort MicMute = 0x6447, MusicMute = 0x6448, MasterMute = 0x6449,
        EchoEnable = 0x6404, ReverbEnable = 0x640A, LiveMute = 0x6468;

    public static bool SupportsInfo(SoncaBleReply reply) => reply.Command == SoncaBleProtocol.CmdInfo
        && System.Text.Encoding.ASCII.GetString(reply.Payload).StartsWith("SCA_MAIN48_C360.C360", StringComparison.Ordinal);

    public static bool CanWrite(ushort register, ushort value) => register == MicVolume ? value <= 8192
        : register is MicMute or MusicMute or MasterMute or EchoEnable or ReverbEnable or LiveMute && value <= 1;

    // C360's mobile fader has 201 internal steps but labels step / 2:
    // raw 4096 is displayed as 50, raw 8192 as 100.
    public static ushort VolumeToRaw(double level) => (ushort)(Math.Clamp(level, 0, 100) * 8192 / 100);
    public static double VolumeFromRaw(ushort raw) => raw * 100.0 / 8192;

    public static bool TryReadParameterUpdate(SoncaBleReply reply, out IReadOnlyDictionary<ushort, ushort> parameters)
    {
        var result = new Dictionary<ushort, ushort>();
        parameters = result;
        if (reply.Command is not (0x03 or 0x84) || reply.Status != 0 || reply.Payload.Length == 0 || reply.Payload.Length % 4 != 0) return false;
        var data = reply.Payload;
        for (int i = 0; i < data.Length; i += 4)
            result[(ushort)((data[i] << 8) | data[i + 1])] = (ushort)((data[i + 2] << 8) | data[i + 3]);
        return true;
    }

    public static bool TryMuteRegister(string channel, out ushort register, out bool inverted)
    {
        inverted = channel is "Echo" or "Reverb";
        register = channel switch { "Mic" => MicMute, "Music" => MusicMute, "Master" => MasterMute,
            "Echo" => EchoEnable, "Reverb" => ReverbEnable, "Live" => LiveMute, _ => 0 };
        return register != 0;
    }

    public static bool TryReadSnapshot(SoncaBleReply reply, out IReadOnlyDictionary<ushort, ushort> parameters)
    {
        var result = new Dictionary<ushort, ushort>();
        parameters = result;
        // C360 CMD=02: flag 00, embedded length (pairs + 3), BE NRPN/value
        // pairs, embedded check byte. The decoder has verified outer CRC8.
        var data = reply.Payload;
        if (reply.Command != SoncaBleProtocol.CmdGetParam || reply.Status != 0 || data.Length < 7
            || ((data[0] << 8) | data[1]) != data.Length || (data.Length - 3) % 4 != 0) return false;
        for (int i = 2; i < data.Length - 1; i += 4)
        {
            ushort address = (ushort)((data[i] << 8) | data[i + 1]);
            if (!result.TryAdd(address, (ushort)((data[i + 2] << 8) | data[i + 3]))) return false;
        }
        return result.Count > 0;
    }
}
