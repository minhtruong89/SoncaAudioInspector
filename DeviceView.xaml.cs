using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoncaAudioInspector.DeviceUsb;

namespace SoncaAudioInspector
{
    /// <summary>
    /// Interaction logic for DeviceView.xaml
    /// </summary>
    public partial class DeviceView : UserControl
    {
        // Data frame structure
        private const byte START_CODE_1 = 0xA5;
        private const byte START_CODE_2 = 0x5A;
        private const byte END_CODE = 0x16;

        // Control word constants
        private const byte CW_FW_ENCRYPT = 0xFF;
        private const byte CW_FW_PASS = 0xFF;
        private const byte CW_FW_INFO = 0x00;
        private const byte CW_QC = 0xFD;

        // QC Sub-group 0x01: MIC Control
        private const byte QC_GROUP_MIC = 0x01;
        private const byte QC_MIC_RESET = 0x01;
        private const byte QC_MIC_SET_KEYS = 0x02;

        // QC Sub-group 0x02: Ngõ Audio
        private const byte QC_GROUP_AUDIO = 0x02;
        private const byte QC_AUDIO_GET = 0x01;
        private const byte QC_AUDIO_SET = 0x02;

        // Audio options enum: 0: BLUETOOTH, 1: LINE IN, 2: OPTICAL, 3: UNKNOWN
        private static readonly string[] AudioOutOptions = new[]
        {
            "0: BLUETOOTH",
            "1: LINE IN",
            "2: OPTICAL",
            "UNKNOWN"
        };

        // Firmware type dictionary
        private static readonly Dictionary<byte, string> FwTypeDictionary = new Dictionary<byte, string>
        {
            { 0x00, "DU561" },
            { 0x01, "DU562" },
            { 0x02, "DU261" },
            { 0x03, "DU262" },
            { 0x04, "DU56Pro" },
            { 0x20, "AP82xx Karaoke SDK" },
            { 0x21, "AP82xx AudioPlay SDK" },
            { 0x30, "BPxx AudioPlay SDK Series" },
            { 0x31, "BPxx Karaoke SDK Series" }
        };

        // Frozen Brushes for thread-safe UI rendering
        private static readonly SolidColorBrush BrushGreen = CreateFrozenBrush(0x10, 0xB9, 0x81);
        private static readonly SolidColorBrush BrushRed = CreateFrozenBrush(248, 113, 113);
        private static readonly SolidColorBrush BrushGray = CreateFrozenBrush(113, 113, 122);

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private HIDDev? _devManager;
        private HIDInfo? _currentDevice;
        private bool _isBusy;
        private bool _isUpdatingAudioSelection;

        public DeviceView()
        {
            InitializeComponent();
            ComboAudioOut.ItemsSource = AudioOutOptions;
            ComboAudioOut.SelectedIndex = 3; // Mặc định hiển thị UNKNOWN khi chưa kết nối / chưa đọc
            Loaded += DeviceView_Loaded;
            AppendLog("SYSTEM", "Khởi tạo tab Device. Sẵn sàng quét thiết bị USB.");
        }

        private void DeviceView_Loaded(object sender, RoutedEventArgs e)
        {
            // Tự động quét và kết nối vào thiết bị đầu tiên hợp lệ khi mở tab
            if (_devManager == null || !_devManager.IsConnected)
            {
                ScanDevices(autoConnectFirst: true);
            }
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            ScanDevices(autoConnectFirst: false);
        }

        private void ScanDevices(bool autoConnectFirst = false)
        {
            try
            {
                AppendLog("SCAN", "Đang quét các thiết bị USB HID...");
                var allDevs = HIDBrowse.Browse();
                List<HIDInfo> validDevs = new List<HIDInfo>();

                foreach (var dev in allDevs)
                {
                    AppendLog("DEBUG", $"VID: {dev.Vid:X4} | PID: {dev.Pid:X4} | Product: {dev.Product} | Serial: {dev.SerialNumber}");

                    // Điều kiện xác nhận board:
                    // 1. VID chứa 8888
                    // 2. devManager.CheckValid: Usage 55AA, Report Length >= 4
                    if (dev.Vid.ToString("X4").Contains("8888", StringComparison.OrdinalIgnoreCase))
                    {
                        using HIDDev validator = new HIDDev();
                        if (validator.CheckValid(dev))
                        {
                            AppendLog("FOUND DEVICE", $"Phát hiện thiết bị hợp lệ: {dev.Product} (VID: {dev.Vid:X4}, PID: {dev.Pid:X4}, ReportLen: {dev.ReportLength})");
                            validDevs.Add(dev);
                        }
                    }
                }

                ComboDevices.ItemsSource = null;
                ComboDevices.ItemsSource = validDevs;

                if (validDevs.Count > 0)
                {
                    ComboDevices.SelectedIndex = 0;
                    BtnConnect.IsEnabled = true;
                    AppendLog("SCAN", $"Tìm thấy {validDevs.Count} thiết bị phù hợp.");

                    if (autoConnectFirst)
                    {
                        AppendLog("AUTO", "Tự động kết nối vào thiết bị hợp lệ đầu tiên...");
                        _ = ConnectDeviceAsync(validDevs[0]);
                    }
                }
                else
                {
                    BtnConnect.IsEnabled = false;
                    AppendLog("SCAN", "Không tìm thấy thiết bị nào phù hợp điều kiện (VID: 8888, Usage: 55AA).");
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi khi quét thiết bị: " + ex.Message);
            }
        }

        private void ComboDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnConnect.IsEnabled = (ComboDevices.SelectedItem is HIDInfo) && (_devManager == null || !_devManager.IsConnected);
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (ComboDevices.SelectedItem is not HIDInfo selectedDev || _isBusy) return;
            await ConnectDeviceAsync(selectedDev);
        }

        private async Task ConnectDeviceAsync(HIDInfo selectedDev)
        {
            if (_isBusy) return;

            _isBusy = true;
            BtnConnect.IsEnabled = false;
            BtnScan.IsEnabled = false;
            ComboDevices.IsEnabled = false;

            try
            {
                _currentDevice = selectedDev;
                AppendLog("CONNECT", $"Bắt đầu kết nối với {selectedDev.Product} (Path: {selectedDev.Path})...");

                _devManager = new HIDDev();
                bool opened = _devManager.Open(selectedDev);

                if (!opened)
                {
                    AppendLog("ERROR", "device Open Fail - Không thể mở thiết bị!");
                    StatusIndicator.Fill = BrushRed;
                    TxtConnectionStatus.Text = "Mở thiết bị thất bại";
                    _devManager = null;
                    _currentDevice = null;
                    BtnConnect.IsEnabled = true;
                    BtnScan.IsEnabled = true;
                    ComboDevices.IsEnabled = true;
                    return;
                }

                AppendLog("INFO", "device Opened - Đã mở thiết bị thành công!");
                StatusIndicator.Fill = BrushGreen;
                TxtConnectionStatus.Text = $"Đã kết nối: {_currentDevice.Product}";
                BtnConnect.Visibility = Visibility.Collapsed;
                BtnDisconnect.Visibility = Visibility.Visible;

                // Chạy tuần tự theo quy trình: CW_FW_ENCRYPT -> ProcessGetAllInfo (CW_FW_INFO)
                bool handshakeSuccess = await Task.Run(() => RunHandshakeSequence());

                if (handshakeSuccess)
                {
                    // Hiển thị phần giao diện cho command mới xử lý tác vụ QC
                    PanelQc.Visibility = Visibility.Visible;

                    // Đọc ngõ Audio hiện tại
                    await GetAudioModeAsync();
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi trong quá trình kết nối: " + ex.Message);
            }
            finally
            {
                _isBusy = false;
                BtnScan.IsEnabled = true;
            }
        }

        private bool RunHandshakeSequence()
        {
            if (_devManager == null || _currentDevice == null) return false;

            try
            {
                // ==========================================
                // 1. GỬI CW_FW_ENCRYPT (0xFF)
                // ==========================================
                AppendLog("TX", "Gửi CW_FW_ENCRYPT (0xFF)...");
                byte[] actualReadData = ProcessControlWord(CW_FW_ENCRYPT);
                if (actualReadData.Length <= 0)
                {
                    AppendLog("ERROR", "Lệnh CW_FW_ENCRYPT chưa hoàn tất. Dừng handshake.");
                    return false;
                }

                // Kiểm tra encryption response
                if (actualReadData.Length >= 2 && actualReadData[0] == 0x00 && actualReadData[1] == 0x01)
                {
                    /* ENCRYPTION - PASSWORD NEEDED */
                    AppendLog("ENCRYPT", "Thiết bị yêu cầu mật khẩu mã hóa. Tự động xác thực mật khẩu 77-55-44-88...");

                    // Mật khẩu cố định: 77 55 44 88 -> gửi byte array: 0x88, 0x44, 0x55, 0x77
                    byte[] passData = new byte[] { 0x88, 0x44, 0x55, 0x77 };
                    byte[] passActualData = ProcessControlWord(CW_FW_PASS, passData);
                    if (passActualData.Length <= 0)
                    {
                        AppendLog("ERROR", "Lệnh CW_FW_PASS chưa hoàn tất. Dừng handshake.");
                        return false;
                    }

                    if (passActualData.Length >= 2 && passActualData[0] == 0x01 && passActualData[1] == 0x01)
                    {
                        AppendLog("PASS", "Xác thực mật khẩu thành công!");
                        _currentDevice.EncryptProcessDone = true;
                    }
                    else
                    {
                        AppendLog("ERROR", "Xác thực mật khẩu thất bại!");
                        return false;
                    }
                }
                else
                {
                    AppendLog("ENCRYPT", "Không yêu cầu mật khẩu mã hóa (No encryption).");
                    _currentDevice.EncryptProcessDone = true;
                }

                // ==========================================
                // 2. Gọi CW_FW_INFO (0x00)
                // ==========================================
                AppendLog("FLOW", "Bắt đầu gọi ProcessGetAllInfo()...");

                AppendLog("TX", "Gửi CW_FW_INFO (0x00)...");
                byte[] fwInfoActual = ProcessControlWord(CW_FW_INFO);
                if (fwInfoActual.Length <= 0)
                {
                    AppendLog("ERROR", "Lệnh CW_FW_INFO chưa hoàn tất. Dừng handshake.");
                    return false;
                }

                if (fwInfoActual.Length >= 7)
                {
                    byte bFwType = fwInfoActual[0];
                    string fwTypeStr;
                    if (bFwType >= 0x05 && bFwType <= 0x1F)
                    {
                        fwTypeStr = "Other DU Series";
                    }
                    else if (bFwType >= 0x22 && bFwType <= 0x2F)
                    {
                        fwTypeStr = "Other AP82xx Series";
                    }
                    else if (bFwType >= 0x31 && bFwType <= 0xFF)
                    {
                        fwTypeStr = "Reserved";
                    }
                    else
                    {
                        if (!FwTypeDictionary.TryGetValue(bFwType, out fwTypeStr!))
                        {
                            fwTypeStr = "Not support FW";
                        }
                    }

                    _currentDevice.FirmwareType = (int)bFwType;
                    string strFwVersion = $"V{(int)fwInfoActual[1]}.{(int)fwInfoActual[2]}.{(int)fwInfoActual[3]}";
                    string strEffectLibVersion = $"V{(int)fwInfoActual[4]}.{(int)fwInfoActual[5]}.{(int)fwInfoActual[6]}";

                    CW_Firmware_Info fwInfo = new CW_Firmware_Info(fwInfoActual)
                    {
                        FwTypeString = fwTypeStr,
                        FirmwareVersionString = strFwVersion,
                        EffectLibVersionString = strEffectLibVersion
                    };
                    _currentDevice.Firmware_Info = fwInfo;

                    string displayStr = $"[{fwTypeStr}, Version: {strFwVersion}, Effect Lib: {strEffectLibVersion}]";
                    AppendLog("FW_INFO", "Thông tin Firmware: " + displayStr);

                    // Cập nhật lên hàng text giao diện
                    Dispatcher.Invoke(() =>
                    {
                        TxtFwInfo.Text = displayStr;
                    });
                }
                else
                {
                    AppendLog("WARN", "Dữ liệu trả về từ CW_FW_INFO không đủ độ dài tiêu chuẩn (cần >= 7 bytes).");
                }

                AppendLog("COMPLETE", "Toàn bộ chuỗi xác thực và lấy thông tin thiết bị hoàn tất thành công!");
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi trong chuỗi giao tiếp handshake: " + ex.Message);
                return false;
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            DisconnectDevice();
        }

        private void DisconnectDevice()
        {
            try
            {
                if (_devManager != null)
                {
                    _devManager.Close();
                    _devManager = null;
                }

                _currentDevice = null;
                StatusIndicator.Fill = BrushGray;
                TxtConnectionStatus.Text = "Chưa kết nối";
                TxtFwInfo.Text = "[Chưa có dữ liệu]";
                PanelQc.Visibility = Visibility.Collapsed;

                _isUpdatingAudioSelection = true;
                try
                {
                    ComboAudioOut.SelectedIndex = 3; // UNKNOWN
                }
                finally
                {
                    _isUpdatingAudioSelection = false;
                }

                BtnDisconnect.Visibility = Visibility.Collapsed;
                BtnConnect.Visibility = Visibility.Visible;
                BtnConnect.IsEnabled = ComboDevices.SelectedItem is HIDInfo;
                ComboDevices.IsEnabled = true;

                AppendLog("DISCONNECT", "Đã ngắt kết nối với thiết bị. Tự động quét lại danh sách thiết bị...");

                // Sau khi disconnect thì scan lại để chọn thiết bị kết nối
                ScanDevices(autoConnectFirst: false);
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi khi ngắt kết nối: " + ex.Message);
            }
        }

        // ==========================================
        // TIẾN TRÌNH XỬ LÝ CONTROL WORD (USB HID)
        // ==========================================

        /// <summary>
        /// Tiến trình gửi gói tin chứa Control Word và nhận lại dữ liệu payload thực tế (actualReadData).
        /// </summary>
        /// <param name="controlWord">Mã Control Word (ví dụ: CW_FW_ENCRYPT, CW_FW_INFO, CW_QC,...)</param>
        /// <param name="dataField">Payload dữ liệu gửi kèm (nếu có)</param>
        /// <returns>Mảng byte dữ liệu thực tế nhận về từ thiết bị (actualReadData)</returns>
        private byte[] ProcessControlWord(byte controlWord, byte[]? dataField = null)
        {
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null || _currentDevice.ReportLength <= 0)
            {
                AppendLog("WARN", $"Chưa hoàn tất command (0x{controlWord:X2}) - Thiết bị chưa kết nối!");
                return Array.Empty<byte>();
            }

            byte[] outWriteData = PrepareSendData(controlWord, dataField);
            byte[] inReadData = new byte[_currentDevice.ReportLength];

            LogTxData(controlWord, outWriteData);
            _devManager.SetOutputReport(outWriteData, outWriteData.Length);
            _devManager.GetInputReport(inReadData, _currentDevice.ReportLength);
            LogRxData(controlWord, inReadData);

            byte[] actualReadData = ParseActualData(inReadData, controlWord);
            if (actualReadData.Length > 0)
            {
                LogActualData(controlWord, actualReadData);
            }
            else
            {
                AppendLog("WARN", $"Chưa hoàn tất command (0x{controlWord:X2})!");
            }

            return actualReadData;
        }

        // ==========================================
        // TÁC VỤ KIỂM TRA QC (CW_QC: 0xFE)
        // ==========================================

        private async void BtnResetMic_Click(object sender, RoutedEventArgs e)
        {
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null) return;

            await Task.Run(() =>
            {
                try
                {
                    AppendLog("QC", ">>> Gửi lệnh Reset MIC (0x01, 0x01)...");
                    byte[] payload = new byte[] { QC_GROUP_MIC, QC_MIC_RESET };
                    byte[] actualData = ProcessControlWord(CW_QC, payload);
                    if (actualData.Length > 0)
                    {
                        AppendLog("QC", "Hoàn tất gửi lệnh Reset MIC.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "Lỗi khi Reset MIC: " + ex.Message);
                }
            });
        }

        private async void BtnSetMicKeys_Click(object sender, RoutedEventArgs e)
        {
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null) return;

            string mic1 = TxtMic1.Text.Trim();
            string mic2 = TxtMic2.Text.Trim();

            if (string.IsNullOrEmpty(mic1) && string.IsNullOrEmpty(mic2))
            {
                AppendLog("WARN", "Vui lòng nhập chuỗi MIC 1 và MIC 2 trước khi cài đặt!");
                return;
            }

            string combined = $"{mic1} {mic2}";
            await Task.Run(() =>
            {
                try
                {
                    AppendLog("QC", $">>> Gửi lệnh Cài đặt MIC Keys (0x01, 0x02): \"{combined}\"...");
                    byte[] strBytes = Encoding.ASCII.GetBytes(combined);
                    byte[] payload = new byte[2 + strBytes.Length];
                    payload[0] = QC_GROUP_MIC;
                    payload[1] = QC_MIC_SET_KEYS;
                    Array.Copy(strBytes, 0, payload, 2, strBytes.Length);

                    byte[] actualData = ProcessControlWord(CW_QC, payload);
                    if (actualData.Length > 0)
                    {
                        AppendLog("QC", $"Đã hoàn tất cài đặt MIC Keys: \"{combined}\"");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "Lỗi khi cài đặt MIC Keys: " + ex.Message);
                }
            });
        }

        private async void BtnGetAudio_Click(object sender, RoutedEventArgs e)
        {
            await GetAudioModeAsync();
        }

        private async Task GetAudioModeAsync()
        {
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null) return;

            await Task.Run(() =>
            {
                try
                {
                    AppendLog("QC", ">>> Gửi lệnh Đọc ngõ Audio hiện tại (0x02, 0x01)...");
                    byte[] payload = new byte[] { QC_GROUP_AUDIO, QC_AUDIO_GET };
                    byte[] actualData = ProcessControlWord(CW_QC, payload);

                    if (actualData.Length > 0)
                    {
                        // Trích xuất index ngõ audio từ phản hồi
                        int audioIdx;
                        if (actualData.Length >= 3 && actualData[0] == QC_GROUP_AUDIO && actualData[1] == QC_AUDIO_GET)
                        {
                            audioIdx = actualData[2];
                        }
                        else if (actualData.Length >= 2 && actualData[0] == QC_GROUP_AUDIO)
                        {
                            audioIdx = actualData[1];
                        }
                        else
                        {
                            audioIdx = actualData[actualData.Length - 1];
                        }

                        string audioName = GetAudioModeName(audioIdx);
                        AppendLog("QC", $"Ngõ Audio hiện tại: [{audioIdx}] {audioName}");

                        Dispatcher.Invoke(() =>
                        {
                            _isUpdatingAudioSelection = true;
                            try
                            {
                                if (audioIdx >= 0 && audioIdx < 3)
                                {
                                    ComboAudioOut.SelectedIndex = audioIdx;
                                }
                                else
                                {
                                    ComboAudioOut.SelectedIndex = 3; // UNKNOWN
                                }
                            }
                            finally
                            {
                                _isUpdatingAudioSelection = false;
                            }
                        });
                    }
                    else
                    {
                        AppendLog("WARN", "Không lấy được ngõ Audio hiện tại. Hiển thị UNKNOWN.");
                        Dispatcher.Invoke(() =>
                        {
                            _isUpdatingAudioSelection = true;
                            try
                            {
                                ComboAudioOut.SelectedIndex = 3; // UNKNOWN
                            }
                            finally
                            {
                                _isUpdatingAudioSelection = false;
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "Lỗi khi đọc ngõ Audio: " + ex.Message);
                }
            });
        }

        private async void ComboAudioOut_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingAudioSelection) return;
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null) return;

            int selectedIdx = ComboAudioOut.SelectedIndex;
            if (selectedIdx < 0 || selectedIdx > 2) return; // Chỉ gửi lệnh cho các ngõ 0 (BT), 1 (Line In), 2 (Optical); bỏ qua UNKNOWN (index 3)

            await SetAudioModeAsync((byte)selectedIdx);
        }

        private async Task SetAudioModeAsync(byte audioIdx)
        {
            if (_devManager == null || !_devManager.IsConnected || _currentDevice == null) return;

            await Task.Run(() =>
            {
                try
                {
                    string audioName = GetAudioModeName(audioIdx);
                    AppendLog("QC", $">>> Gửi lệnh Chuyển ngõ Audio sang: [{audioIdx}] {audioName} (0x02, 0x02, 0x{audioIdx:X2})...");
                    byte[] payload = new byte[] { QC_GROUP_AUDIO, QC_AUDIO_SET, audioIdx };
                    byte[] actualData = ProcessControlWord(CW_QC, payload);
                    if (actualData.Length > 0)
                    {
                        AppendLog("QC", $"Đã hoàn tất chuyển sang ngõ Audio: [{audioIdx}] {audioName}");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("ERROR", "Lỗi khi chuyển ngõ Audio: " + ex.Message);
                }
            });
        }

        private static string GetAudioModeName(int idx)
        {
            return idx switch
            {
                0 => "BLUETOOTH",
                1 => "LINE IN",
                2 => "OPTICAL",
                _ => "UNKNOWN"
            };
        }

        // ==========================================
        // GÓI TIN & GIAO THỨC PROTOCOL
        // ==========================================
        private byte[] PrepareSendData(byte controlWord, byte[]? dataField)
        {
            if (_currentDevice == null || _currentDevice.ReportLength <= 0)
            {
                return Array.Empty<byte>();
            }

            byte[] result = new byte[_currentDevice.ReportLength];
            int i = 0;
            result[i++] = 0x00;           // Specific byte for window report ID
            result[i++] = START_CODE_1;   // 0xA5
            result[i++] = START_CODE_2;   // 0x5A
            result[i++] = controlWord;

            int dataLen = dataField?.Length ?? 0;
            result[i++] = (byte)dataLen;

            if (dataField != null && dataLen > 0)
            {
                Array.Copy(dataField, 0, result, i, dataLen);
                i += dataLen;
            }

            result[i++] = END_CODE;       // 0x16
            return result;
        }

        private byte[] ParseActualData(byte[] fullData, byte expectedControlWord)
        {
            if (fullData == null || fullData.Length < 5) return Array.Empty<byte>();

            // Kiểm tra format: START_CODE_1 (0xA5), START_CODE_2 (0x5A)
            if (fullData[1] != START_CODE_1 || fullData[2] != START_CODE_2)
            {
                return Array.Empty<byte>();
            }

            // Kiểm tra Control Word của RX và TX phải giống nhau mới hợp lệ
            byte rxControlWord = fullData[3];
            if (rxControlWord != expectedControlWord)
            {
                AppendLog("RX", "NOT SAME CONTROL WORD");
                return Array.Empty<byte>();
            }

            int dataLength = fullData[4];
            if ((4 + dataLength + 1 >= fullData.Length) || fullData[4 + dataLength + 1] != END_CODE)
            {
                return Array.Empty<byte>();
            }

            byte[] actualData = new byte[dataLength];
            Array.Copy(fullData, 5, actualData, 0, dataLength);
            return actualData;
        }

        // ==========================================
        // LOG & DEBUG HELPERS
        // ==========================================
        private void LogTxData(byte controlWord, byte[] data)
        {
            AppendLog("TX", $"(0x{controlWord:X2}) - {ByteArrayToString(data)}");
        }

        private void LogRxData(byte controlWord, byte[] data)
        {
            AppendLog("RX", $"(0x{controlWord:X2}) - {ByteArrayToString(data)}");
        }

        private void LogActualData(byte controlWord, byte[] actualData)
        {
            AppendLog("DEBUG", $"RX actual (0x{controlWord:X2}) [{actualData.Length} bytes] - {ByteArrayToString(actualData)}");
        }

        public static string ByteArrayToString(byte[] ba)
        {
            if (ba == null || ba.Length == 0) return "";
            StringBuilder hex = new StringBuilder(ba.Length * 3);
            for (int i = 0; i < ba.Length; i++)
            {
                hex.Append(ba[i].ToString("X2")).Append(" ");
            }
            return hex.ToString().Trim();
        }

        private void AppendLog(string tag, string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss.fff");
                string logLine = $"[{timeStr}] [{tag}] {message}\r\n";
                TxtDebugLog.AppendText(logLine);
                LogScrollViewer.ScrollToEnd();
            });
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtDebugLog.Clear();
        }
    }
}
