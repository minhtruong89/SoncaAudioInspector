using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace SoncaAudioInspector;

public sealed record BleDeviceInfo(
    ulong Address,
    string Name,
    short Rssi,
    bool IsLikelySonca,
    DateTime LastSeen)
{
    public string FormattedAddress =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public string DisplayText =>
        $"{Name} (RSSI: {Rssi} dBm) [{FormattedAddress}]" + (IsLikelySonca ? " ★ [Sonca/Acnos]" : "");
}

public sealed class SoncaBleManager : IDisposable
{
    private static readonly Lazy<SoncaBleManager> _instance = new(() => new SoncaBleManager());
    public static SoncaBleManager Instance => _instance.Value;

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _connectedDevice;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private readonly ConcurrentDictionary<ulong, BleDeviceInfo> _discoveredDevices = new();

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public bool IsConnected => _connectedDevice != null && _connectedDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public string ConnectedDeviceName => _connectedDevice?.Name ?? "";
    public string ConnectedDeviceAddress => _connectedDevice != null
        ? string.Join(":", Enumerable.Range(0, 6).Select(i => ((_connectedDevice.BluetoothAddress >> (8 * (5 - i))) & 0xFF).ToString("X2")))
        : "";

    public string ActiveServiceUuid => _writeCharacteristic?.Service?.Uuid.ToString() ?? "";
    public string ActiveWriteCharUuid => _writeCharacteristic?.Uuid.ToString() ?? "";
    public string ActiveNotifyCharUuid => _notifyCharacteristic?.Uuid.ToString() ?? "";

    // Events
    public event Action<BleDeviceInfo>? DeviceDiscovered;
    public event Action<bool, string>? ConnectionStatusChanged;
    public event Action<byte[], string>? DataSent;
    public event Action<byte[], string>? DataReceived;
    public event Action<string>? LogMessage;

    public IReadOnlyCollection<BleDeviceInfo> DiscoveredDevices => _discoveredDevices.Values.ToList();

    public void StartScanning()
    {
        StopScanning();

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += (_, e) =>
        {
            LogMessage?.Invoke($"[BLE] Đã dừng quét (Lý do: {e.Error}).");
        };

        try
        {
            _watcher.Start();
            LogMessage?.Invoke("[BLE] Đang quét các thiết bị Bluetooth Low Energy gần đây...");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE] Lỗi khi bật quét BLE: {ex.Message}");
        }
    }

    public void StopScanning()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.Received -= OnAdvertisementReceived;
                _watcher.Stop();
            }
            catch { }
            _watcher = null;
        }
    }

    public void ClearDiscoveredDevices()
    {
        _discoveredDevices.Clear();
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        string name = args.Advertisement.LocalName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            // Bỏ qua thiết bị không phát tên để tránh spam danh sách
            return;
        }

        bool isSonca = SoncaBleProtocol.IsLikelySoncaDevice(name);
        var info = new BleDeviceInfo(args.BluetoothAddress, name, args.RawSignalStrengthInDBm, isSonca, DateTime.Now);

        _discoveredDevices[args.BluetoothAddress] = info;
        DeviceDiscovered?.Invoke(info);
    }

    public async Task<bool> ConnectAsync(ulong bluetoothAddress)
    {
        Disconnect();
        StopScanning();

        ConnectionStatusChanged?.Invoke(false, "Đang kết nối tới thiết bị...");
        LogMessage?.Invoke($"[BLE] Bắt đầu kết nối tới 0x{bluetoothAddress:X12}...");

        try
        {
            _connectedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (_connectedDevice == null)
            {
                ConnectionStatusChanged?.Invoke(false, "Không thể mở đối tượng BluetoothLEDevice.");
                LogMessage?.Invoke("[BLE] Kết nối thất bại: thiết bị không phản hồi.");
                return false;
            }

            _connectedDevice.ConnectionStatusChanged += OnDeviceConnectionStatusChanged;

            // Truy vấn GATT Services
            var servicesResult = await _connectedDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                ConnectionStatusChanged?.Invoke(false, $"Lỗi truy vấn Services ({servicesResult.Status})");
                LogMessage?.Invoke($"[BLE] Không đọc được GATT Services: {servicesResult.Status}");
                return false;
            }

            var services = servicesResult.Services;
            LogMessage?.Invoke($"[BLE] Tìm thấy {services.Count} GATT Service(s).");

            GattDeviceService? targetService = null;

            // Ưu tiên 1: Service có UUID Sonca 5343 hoặc standard 0xFFF0
            targetService = services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.PrimaryServiceUuid)
                         ?? services.FirstOrDefault(s => s.Uuid == SoncaBleProtocol.StandardUartServiceUuid)
                         ?? services.FirstOrDefault(s => s.Uuid.ToString().StartsWith("0000fff0", StringComparison.OrdinalIgnoreCase))
                         ?? services.FirstOrDefault(s => s.Uuid.ToString().StartsWith("00005343", StringComparison.OrdinalIgnoreCase));

            // Nếu không có, tìm service đầu tiên chứa characteristic có cờ Write
            if (targetService == null)
            {
                foreach (var s in services)
                {
                    var chars = await s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    if (chars.Status == GattCommunicationStatus.Success && chars.Characteristics.Any(c =>
                        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
                        c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)))
                    {
                        targetService = s;
                        break;
                    }
                }
            }

            if (targetService == null && services.Count > 0)
            {
                targetService = services[0];
            }

            if (targetService == null)
            {
                ConnectionStatusChanged?.Invoke(false, "Không tìm thấy Service phù hợp trên thiết bị.");
                return false;
            }

            LogMessage?.Invoke($"[BLE] Sử dụng Service: {targetService.Uuid}");

            var charResult = await targetService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charResult.Status != GattCommunicationStatus.Success)
            {
                ConnectionStatusChanged?.Invoke(false, "Không đọc được Characteristics.");
                return false;
            }

            var characteristics = charResult.Characteristics;

            // Tìm Write Characteristic
            _writeCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid1)
                               ?? characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.WriteCharacteristicUuid2)
                               ?? characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
                               ?? characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));

            // Tìm Notify/Read Characteristic
            _notifyCharacteristic = characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid1)
                                ?? characteristics.FirstOrDefault(c => c.Uuid == SoncaBleProtocol.NotifyCharacteristicUuid2)
                                ?? characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
                                ?? characteristics.FirstOrDefault(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate));

            if (_writeCharacteristic == null)
            {
                ConnectionStatusChanged?.Invoke(false, "Không tìm thấy Characteristic ghi (Write).");
                return false;
            }

            LogMessage?.Invoke($"[BLE] Write Char: {_writeCharacteristic.Uuid} (Properties: {_writeCharacteristic.CharacteristicProperties})");

            // Kích hoạt nhận thông báo (Notification)
            if (_notifyCharacteristic != null)
            {
                try
                {
                    _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
                    var cccdValue = _notifyCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                        : GattClientCharacteristicConfigurationDescriptorValue.Notify;

                    var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);
                    LogMessage?.Invoke($"[BLE] Đăng ký lắng nghe Notify Char: {_notifyCharacteristic.Uuid} (Kết quả: {status})");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[BLE] Cảnh báo kích hoạt Notify: {ex.Message}");
                }
            }

            ConnectionStatusChanged?.Invoke(true, $"Đã kết nối: {_connectedDevice.Name}");
            LogMessage?.Invoke($"[BLE] ★ Kết nối thành công tới {_connectedDevice.Name}!");

            // Tự động gửi gói SCA=INFO để bắt tay và đọc trạng thái loa
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                await SendInfoQueryAsync();
            });

            return true;
        }
        catch (Exception ex)
        {
            ConnectionStatusChanged?.Invoke(false, $"Lỗi kết nối: {ex.Message}");
            LogMessage?.Invoke($"[BLE] Ngoại lệ khi kết nối: {ex}");
            Disconnect();
            return false;
        }
    }

    private void OnDeviceConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        bool connected = sender.ConnectionStatus == BluetoothConnectionStatus.Connected;
        ConnectionStatusChanged?.Invoke(connected, connected ? $"Đang kết nối: {sender.Name}" : "Thiết bị đã ngắt kết nối.");
        LogMessage?.Invoke($"[BLE] Trạng thái kết nối đổi: {sender.ConnectionStatus}");
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] bytes = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(bytes);

            string hex = BitConverter.ToString(bytes).Replace("-", " ");
            string ascii = Encoding.ASCII.GetString(bytes.Select(b => (b >= 32 && b <= 126) ? b : (byte)'.').ToArray());

            string desc = $"HEX: {hex} | ASCII: {ascii}";
            if (SoncaBleProtocol.TryParsePacket(bytes, out byte cmd, out byte[] payload))
            {
                desc += $" [Sonca CMD 0x{cmd:X2}, Len {payload.Length}]";
            }

            DataReceived?.Invoke(bytes, desc);
            LogMessage?.Invoke($"[RX] {desc}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE RX Error] {ex.Message}");
        }
    }

    public async Task<bool> SendPacketAsync(byte[] data, string? description = null)
    {
        if (_writeCharacteristic == null || _connectedDevice == null)
        {
            LogMessage?.Invoke("[BLE TX] Chưa kết nối tới loa hoặc thiếu Write Characteristic.");
            return false;
        }

        try
        {
            var writer = new DataWriter();
            writer.WriteBytes(data);
            var buffer = writer.DetachBuffer();

            var writeOption = _writeCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;

            var result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeOption);
            if (result.Status == GattCommunicationStatus.Success)
            {
                string hex = BitConverter.ToString(data).Replace("-", " ");
                string desc = description ?? $"Packet {data.Length} bytes";
                DataSent?.Invoke(data, $"{desc} -> {hex}");
                LogMessage?.Invoke($"[TX] {desc} -> {hex}");
                return true;
            }
            else
            {
                LogMessage?.Invoke($"[BLE TX] Gửi thất bại: {result.Status}");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[BLE TX Error] {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendInfoQueryAsync()
    {
        byte[] packet = SoncaBleProtocol.BuildInfoPacket();
        return await SendPacketAsync(packet, "SCA=INFO (Bắt tay & Đọc thông tin loa)");
    }

    public async Task<bool> SetParamAsync(byte channelType, ushort regAddr, ushort value, string paramName)
    {
        byte[] packet = SoncaBleProtocol.BuildParamPacket(channelType, regAddr, value);
        return await SendPacketAsync(packet, $"{paramName} = {value} (Reg 0x{regAddr:X4}, Ch 0x{channelType:X2})");
    }

    public void Disconnect()
    {
        if (_notifyCharacteristic != null)
        {
            try
            {
                _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
            }
            catch { }
            _notifyCharacteristic = null;
        }

        _writeCharacteristic = null;

        if (_connectedDevice != null)
        {
            try
            {
                _connectedDevice.ConnectionStatusChanged -= OnDeviceConnectionStatusChanged;
                _connectedDevice.Dispose();
            }
            catch { }
            _connectedDevice = null;
            ConnectionStatusChanged?.Invoke(false, "Đã ngắt kết nối.");
            LogMessage?.Invoke("[BLE] Đã ngắt kết nối thiết bị.");
        }
    }

    public void Dispose()
    {
        StopScanning();
        Disconnect();
    }
}
