namespace SoncaAudioInspector;

public sealed record SoncaBleRouteReport(byte Original, byte[] Available, byte[] Confirmed);

public static class SoncaBleRouteVerification
{
    public static async Task<SoncaBleRouteReport> VerifyAsync(ISoncaMixerWriter writer, IReadOnlyList<byte> requested,
        Func<bool> sameDevice, CancellationToken token)
    {
        if (!writer.IsConnected || !writer.SupportsInputRouting || !sameDevice()
            || !await writer.SendPacketAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc ngõ trước Auto Test")
            || !sameDevice() || !writer.MixerState.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort raw)
            || !SoncaBleMixerProfile.TryReadInputSource(raw, out byte original))
            throw new InvalidOperationException("Chưa có trạng thái ngõ BLE hợp lệ; chưa phát tín hiệu đo.");
        var available = Enumerable.Range(0, 6).Select(i => (byte)i).Where(source => SoncaBleMixerProfile.CanSelectInputSource(writer.MixerState, source)).ToArray();
        var confirmed = new List<byte>();
        try
        {
            foreach (byte source in requested.Distinct())
            {
                token.ThrowIfCancellationRequested();
                if (!sameDevice()) throw new InvalidOperationException("Loa BLE đã đổi trong lúc kiểm tra ngõ.");
                if (!available.Contains(source)) continue;
                if (await writer.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(source), "Xác minh " + writer.InputSourceName(source))
                    && sameDevice() && writer.MixerState.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort actual)
                    && SoncaBleMixerProfile.TryReadInputSource(actual, out byte selected) && selected == source) confirmed.Add(source);
            }
        }
        finally
        {
            if (!sameDevice() || !await writer.SendPacketAsync(SoncaBleProtocol.BuildInputSourcePacket(original), "Khôi phục ngõ trước Auto Test")
                || !sameDevice() || !writer.MixerState.TryGetValue(SoncaBleMixerProfile.InputSource, out ushort restored)
                || !SoncaBleMixerProfile.TryReadInputSource(restored, out byte source) || source != original)
                throw new InvalidOperationException("Chưa khôi phục được ngõ BLE trước đo; dừng Auto Test.");
        }
        return new(original, available, confirmed.ToArray());
    }
}
