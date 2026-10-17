using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

public sealed partial class SoncaBleManager
{
    private readonly SemaphoreSlim _connectGate = new(1, 1), _writeGate = new(1, 1);
    private readonly List<GattDeviceService> _openServices = new();
    private GattSession? _session;
    private volatile bool _controlReady, _protocolReady;
    private readonly SoncaBleReplyDecoder _replyDecoder = new();
    private TaskCompletionSource<SoncaBleReply>? _infoReply;
    private int _connectionGeneration;
    public bool IsConnecting { get; private set; }

    public static async Task<IReadOnlyList<BluetoothAudioPeer>> ConnectedAudioPeersAsync()
    {
        var peers = new List<BluetoothAudioPeer>();
        var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        foreach (var info in infos.Where(i => SoncaBleProtocol.IsLikelySoncaDevice(i.Name)))
        {
            using var device = await BluetoothDevice.FromIdAsync(info.Id);
            if (device?.ConnectionStatus == BluetoothConnectionStatus.Connected) peers.Add(new(device.BluetoothAddress, info.Name.Trim()));
        }
        return peers;
    }

    public async Task<bool> ConnectAsync(ulong bluetoothAddress)
    {
        if (!await _connectGate.WaitAsync(0)) return false;
        Disconnect();
        int generation = _connectionGeneration;
        IsConnecting = true;
        StopScanning();
        ConnectionStatusChanged?.Invoke(false, "Đang mở dịch vụ điều khiển BLE...");
        void CheckCurrent()
        {
            if (_connectionGeneration != generation) throw new OperationCanceledException("Đã hủy kết nối BLE.");
        }
        try
        {
            LogMessage?.Invoke($"[BLE] Kết nối điều khiển 0x{bluetoothAddress:X12}; Bluetooth âm thanh là kết nối riêng.");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            if (device == null) throw new InvalidOperationException("Không mở được thiết bị BLE.");
            if (_connectionGeneration != generation) { device.Dispose(); CheckCurrent(); }
            _connectedDevice = device;
            device.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;
            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            CheckCurrent();
            if (_session?.CanMaintainConnection == true) _session.MaintainConnection = true;
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            _openServices.AddRange(services.Services);
            CheckCurrent();
            if (services.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đọc được dịch vụ BLE: {services.Status}.");
            var service = services.Services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.PrimaryServiceUuid)
                ?? services.Services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.StandardUartServiceUuid);
            if (service == null) throw new InvalidOperationException("Thiết bị không có dịch vụ điều khiển Sonca 5343/FFF0; không dùng kênh ghi bất kỳ.");
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            CheckCurrent();
            if (chars.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đọc được kênh điều khiển: {chars.Status}.");
            bool Writable(GattCharacteristic c) => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
                || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
            _writeCharacteristic = chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid1 && Writable(c))
                ?? chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid2 && Writable(c));
            _notifyCharacteristic = chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid1)
                ?? chars.Characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid2);
            if (_writeCharacteristic == null || _notifyCharacteristic == null) throw new InvalidOperationException("Thiếu kênh ghi/nhận phản hồi Sonca; BLE chưa sẵn sàng điều khiển.");
            var properties = _notifyCharacteristic.CharacteristicProperties;
            var notifyMode = properties.HasFlag(GattCharacteristicProperties.Notify) ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                : properties.HasFlag(GattCharacteristicProperties.Indicate) ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : throw new InvalidOperationException("Kênh nhận không hỗ trợ Notify/Indicate.");
            _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
            var subscription = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(notifyMode).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            CheckCurrent();
            if (subscription != GattCommunicationStatus.Success) throw new InvalidOperationException($"Không đăng ký được phản hồi loa: {subscription}.");
            _controlReady = true;
            _infoReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!await SendInfoQueryAsync()) throw new InvalidOperationException("GATT đã mở nhưng không gửi được truy vấn thông tin loa.");
            var info = await _infoReply.Task.WaitAsync(TimeSpan.FromSeconds(6));
            SupportsMixerControls = SoncaBleMixerProfile.SupportsInfo(info);
            _protocolReady = true;
            _mixerReadNotBefore = DateTime.UtcNow.AddMilliseconds(1500);
            LogMessage?.Invoke($"[BLE] INFO được loa xác nhận: {info.Payload.Length} byte.");
            CheckCurrent();
            LogMessage?.Invoke($"[BLE] Sẵn sàng: service {service.Uuid}, write {_writeCharacteristic.Uuid}, notify {_notifyCharacteristic.Uuid}. Gửi thành công qua GATT chưa thay thế việc đọc lại trạng thái DSP.");
            ConnectionStatusChanged?.Invoke(IsConnected, $"BLE sẵn sàng: {device.Name}");
            return IsConnected;
        }
        catch (Exception ex)
        {
            bool currentAttempt = _connectionGeneration == generation;
            // The connect gate still excludes a subsequent attempt, including
            // when a manual disconnect cancelled this one while WinRT awaited.
            Disconnect();
            if (currentAttempt)
            {
                ConnectionStatusChanged?.Invoke(false, ex.Message);
                LogMessage?.Invoke("[BLE] " + ex.Message);
            }
            return false;
        }
        finally { IsConnecting = false; _connectGate.Release(); }
    }

    private void ReleaseGattServices()
    {
        if (_session != null)
        {
            try { _session.MaintainConnection = false; _session.Dispose(); } catch { }
            _session = null;
        }
        foreach (var service in _openServices) { try { service.Dispose(); } catch { } }
        _openServices.Clear();
    }
}
