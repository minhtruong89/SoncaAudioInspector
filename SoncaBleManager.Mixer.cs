namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private TaskCompletionSource<IReadOnlyDictionary<ushort, ushort>>? _stateReply;
    public bool SupportsMixerControls { get; private set; }
    public IReadOnlyDictionary<ushort, ushort> MixerState { get; private set; } = new Dictionary<ushort, ushort>();

    public async Task<bool> SendPacketAsync(byte[] data, string? description = null)
    {
        if (data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildInfoPacket())) return await WriteTransportAsync(data, description);
        if (!IsConnected) return false;
        int generation = _connectionGeneration;
        await _transactionGate.WaitAsync();
        try
        {
            if (generation != _connectionGeneration || !IsConnected) return false;
            if (data.AsSpan().SequenceEqual(SoncaBleProtocol.BuildReadMixerPacket())) return await ReadStateCoreAsync(generation);
            if (!SupportsMixerControls || !SoncaBleProtocol.TryParsePacket(data, out var command, out var payload)
                || command != SoncaBleProtocol.CmdSetParam || payload.Length != 4 || data.Length != 9) return false;
            ushort address = (ushort)((payload[0] << 8) | payload[1]), value = (ushort)((payload[2] << 8) | payload[3]);
            if (!SoncaBleMixerProfile.CanWrite(address, value) || !MixerState.ContainsKey(address))
            {
                LogMessage?.Invoke($"[BLE] Chưa xác minh điều khiển 0x{address:X4}; không gửi lệnh ghi.");
                return false;
            }
            if (!await WriteTransportAsync(data, description)) return false;
            // Empty GATT/command ACKs do not prove the DSP value changed.
            // A fresh CRC-checked config is the authoritative acknowledgement.
            if (!await ReadStateCoreAsync(generation)) return false;
            bool confirmed = MixerState.TryGetValue(address, out var actual) && actual == value;
            LogMessage?.Invoke(confirmed ? $"[BLE] Đã đọc lại 0x{address:X4} = {actual}."
                : $"[BLE] Loa chưa xác nhận 0x{address:X4} = {value}; thực tế {actual}.");
            return confirmed;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke("[BLE] Không xác nhận được trạng thái DSP: " + ex.Message);
            return false;
        }
        finally { _transactionGate.Release(); }
    }

    private async Task<bool> ReadStateCoreAsync(int generation)
    {
        var pending = new TaskCompletionSource<IReadOnlyDictionary<ushort, ushort>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stateReply = pending;
        try
        {
            if (!await WriteTransportAsync(SoncaBleProtocol.BuildReadMixerPacket(), "Đọc cấu hình hiện tại (không lưu EEPROM)"))
                throw new InvalidOperationException("Không gửi được yêu cầu đọc trạng thái loa.");
            var state = await pending.Task.WaitAsync(TimeSpan.FromSeconds(6));
            if (generation != _connectionGeneration || !IsConnected) return false;
            MixerState = state;
            MixerStateReceived?.Invoke(state);
            return true;
        }
        catch
        {
            // Responses have no request id. After a timeout, discard the link
            // so a late snapshot cannot acknowledge the next transaction.
            if (generation == _connectionGeneration) Disconnect();
            throw;
        }
        finally { if (ReferenceEquals(_stateReply, pending)) _stateReply = null; }
    }
}
