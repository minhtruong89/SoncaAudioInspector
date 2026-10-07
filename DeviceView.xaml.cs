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
        private const byte CW_FW_INFO = 0x00;
        private const byte CW_SYSTEM_CONTROL_GET = 0x01;
        private const byte CW_SYSTEM_STATUS = 0x02;
        private const byte CW_ICBM = 0xFB;
        private const byte CW_ICBM_QUERY = 0x01;
        private const byte CW_FW_ENCRYPT = 0xFF;
        private const byte CW_FW_PASS = 0xFF;

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
        private static readonly SolidColorBrush BrushEmerald = CreateFrozenBrush(52, 211, 153);
        private static readonly SolidColorBrush BrushRed = CreateFrozenBrush(248, 113, 113);
        private static readonly SolidColorBrush BrushYellow = CreateFrozenBrush(250, 204, 21);
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

        public DeviceView()
        {
            InitializeComponent();
            AppendLog("SYSTEM", "Khởi tạo tab Device. Sẵn sàng quét thiết bị USB.");
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            ScanDevices();
        }

        private void ScanDevices()
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

                // Chạy tuần tự theo quy trình: CW_FW_ENCRYPT -> ProcessGetAllInfo (CW_FW_INFO, CW_SYSTEM_CONTROL_GET) -> ProcessICBM_Query
                await Task.Run(() => RunHandshakeSequence());
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

        private void RunHandshakeSequence()
        {
            if (_devManager == null || _currentDevice == null) return;

            try
            {
                // ==========================================
                // 1. GỬI CW_FW_ENCRYPT (0xFF)
                // ==========================================
                AppendLog("TX", "Gửi CW_FW_ENCRYPT (0xFF)...");
                byte[] outWriteData = PrepareSendData(CW_FW_ENCRYPT, null);
                byte[] inReadData = new byte[_currentDevice.ReportLength];

                LogTxData(CW_FW_ENCRYPT, outWriteData);
                _devManager.SetOutputReport(outWriteData, outWriteData.Length);
                _devManager.GetInputReport(inReadData, _currentDevice.ReportLength);
                LogRxData(CW_FW_ENCRYPT, inReadData);

                byte[] actualReadData = ParseActualData(inReadData);
                LogActualData(CW_FW_ENCRYPT, actualReadData);

                // Kiểm tra encryption response
                if (actualReadData.Length >= 2 && actualReadData[0] == 0x00 && actualReadData[1] == 0x01)
                {
                    /* ENCRYPTION - PASSWORD NEEDED */
                    AppendLog("ENCRYPT", "Thiết bị yêu cầu mật khẩu mã hóa. Tự động xác thực mật khẩu 77-55-44-88...");

                    // Mật khẩu cố định: 77 55 44 88 -> gửi byte array: 0x88, 0x44, 0x55, 0x77
                    byte[] passData = new byte[] { 0x88, 0x44, 0x55, 0x77 };
                    byte[] passOutData = PrepareSendData(CW_FW_PASS, passData);
                    byte[] passInData = new byte[_currentDevice.ReportLength];

                    LogTxData(CW_FW_PASS, passOutData);
                    _devManager.SetOutputReport(passOutData, passOutData.Length);
                    _devManager.GetInputReport(passInData, _currentDevice.ReportLength);
                    LogRxData(CW_FW_PASS, passInData);

                    byte[] passActualData = ParseActualData(passInData);
                    LogActualData(CW_FW_PASS, passActualData);

                    if (passActualData.Length >= 2 && passActualData[0] == 0x01 && passActualData[1] == 0x01)
                    {
                        AppendLog("PASS", "Xác thực mật khẩu thành công!");
                        _currentDevice.EncryptProcessDone = true;
                    }
                    else
                    {
                        AppendLog("ERROR", "Xác thực mật khẩu thất bại!");
                        return;
                    }
                }
                else
                {
                    AppendLog("ENCRYPT", "Không yêu cầu mật khẩu mã hóa (No encryption).");
                    _currentDevice.EncryptProcessDone = true;
                }

                // ==========================================
                // 2. GỌI ProcessGetAllInfo()
                // ==========================================
                AppendLog("FLOW", "Bắt đầu gọi ProcessGetAllInfo()...");

                // 2a. Gọi CW_FW_INFO (0x00)
                AppendLog("TX", "Gửi CW_FW_INFO (0x00)...");
                byte[] fwInfoOut = PrepareSendData(CW_FW_INFO, null);
                byte[] fwInfoIn = new byte[_currentDevice.ReportLength];

                LogTxData(CW_FW_INFO, fwInfoOut);
                _devManager.SetOutputReport(fwInfoOut, fwInfoOut.Length);
                _devManager.GetInputReport(fwInfoIn, _currentDevice.ReportLength);
                LogRxData(CW_FW_INFO, fwInfoIn);

                byte[] fwInfoActual = ParseActualData(fwInfoIn);
                LogActualData(CW_FW_INFO, fwInfoActual);

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

                // 2b. Gọi CW_SYSTEM_CONTROL_GET (0x01)
                AppendLog("TX", "Gửi CW_SYSTEM_CONTROL_GET (0x01)...");
                byte[] sysCtrlOut = PrepareSendData(CW_SYSTEM_CONTROL_GET, null);
                byte[] sysCtrlIn = new byte[_currentDevice.ReportLength];

                LogTxData(CW_SYSTEM_CONTROL_GET, sysCtrlOut);
                _devManager.SetOutputReport(sysCtrlOut, sysCtrlOut.Length);
                _devManager.GetInputReport(sysCtrlIn, _currentDevice.ReportLength);
                LogRxData(CW_SYSTEM_CONTROL_GET, sysCtrlIn);

                byte[] sysCtrlActual = ParseActualData(sysCtrlIn);
                LogActualData(CW_SYSTEM_CONTROL_GET, sysCtrlActual);

                // ==========================================
                // 3. GỌI ProcessICBM_Query()
                // ==========================================
                AppendLog("FLOW", "Bắt đầu gọi ProcessICBM_Query()...");
                byte[] icbmData = new byte[] { CW_ICBM_QUERY };
                byte[] icbmOut = PrepareSendData(CW_ICBM, icbmData);
                byte[] icbmIn = new byte[_currentDevice.ReportLength];

                LogTxData(CW_ICBM, icbmOut);
                _devManager.SetOutputReport(icbmOut, icbmOut.Length);
                _devManager.GetInputReport(icbmIn, _currentDevice.ReportLength);
                LogRxData(CW_ICBM, icbmIn);

                byte[] icbmActual = ParseActualData(icbmIn);
                LogActualData(CW_ICBM, icbmActual);

                if (icbmActual.Length >= 2)
                {
                    byte bStatus = icbmActual[1];
                    string statusMsg;

                    if (bStatus == 3) // Query OK
                    {
                        statusMsg = "Đã nạp key (OK)";
                        AppendLog("ICBM", "Kết quả ICBM: Đã nạp key OK!");
                    }
                    else if (bStatus == 0 || bStatus == 1)
                    {
                        statusMsg = "Chưa nạp key";
                        AppendLog("ICBM", "Kết quả ICBM: Chưa nạp key.");
                    }
                    else
                    {
                        statusMsg = $"Mã phản hồi: {bStatus}";
                        AppendLog("ICBM", $"Kết quả ICBM phản hồi trạng thái: {bStatus}");
                    }

                    Dispatcher.Invoke(() =>
                    {
                        TxtIcbmStatus.Text = statusMsg;
                        if (bStatus == 3)
                        {
                            TxtIcbmStatus.Foreground = BrushEmerald;
                        }
                        else if (bStatus == 0 || bStatus == 1)
                        {
                            TxtIcbmStatus.Foreground = BrushRed;
                        }
                        else
                        {
                            TxtIcbmStatus.Foreground = BrushYellow;
                        }
                    });
                }
                else
                {
                    AppendLog("WARN", "Dữ liệu trả về từ ICBM Query không hợp lệ.");
                }

                AppendLog("COMPLETE", "Toàn bộ chuỗi xác thực và truy vấn hoàn tất thành công!");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi trong chuỗi giao tiếp handshake: " + ex.Message);
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
                TxtIcbmStatus.Text = "Chưa truy vấn";
                TxtIcbmStatus.Foreground = BrushYellow;

                BtnDisconnect.Visibility = Visibility.Collapsed;
                BtnConnect.Visibility = Visibility.Visible;
                BtnConnect.IsEnabled = ComboDevices.SelectedItem is HIDInfo;
                ComboDevices.IsEnabled = true;

                AppendLog("DISCONNECT", "Đã ngắt kết nối với thiết bị. Tự động quét lại danh sách thiết bị...");

                // Sau khi disconnect thì scan lại để chọn thiết bị kết nối
                ScanDevices();
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "Lỗi khi ngắt kết nối: " + ex.Message);
            }
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

        private byte[] ParseActualData(byte[] fullData)
        {
            if (fullData == null || fullData.Length < 5) return fullData ?? Array.Empty<byte>();

            int dataLength = fullData[4];

            // Kiểm tra format: START_CODE_1 (0xA5), START_CODE_2 (0x5A), END_CODE (0x16)
            if (fullData[1] != START_CODE_1 || fullData[2] != START_CODE_2 ||
                (4 + dataLength + 1 >= fullData.Length) ||
                fullData[4 + dataLength + 1] != END_CODE)
            {
                return fullData;
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
