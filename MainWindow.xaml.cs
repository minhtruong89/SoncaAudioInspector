using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NAudio.CoreAudioApi;
using ScottPlot;

using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SoncaAudioInspector
{
    public class CheckingConfig
    {
        public List<ModelConfig> models { get; set; } = new List<ModelConfig>();
    }
    public class ModelConfig
    {
        public string model { get; set; } = "";
        public TestItems testItems { get; set; } = new TestItems();
        
        [System.Text.Json.Serialization.JsonPropertyName("assemblyCount")]
        public int itemCount { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("assemblyItems")]
        public List<ItemSlotConfig> items { get; set; } = new List<ItemSlotConfig>();
    }
    public class ItemSlotConfig
    {
        public int slot { get; set; }
        public string name { get; set; } = "";
    }
    public class TestItems
    {
        public InOutConfig InOut { get; set; }
    }
    public class InOutConfig
    {
        public string Description { get; set; }
        public DevicesConfig Devices { get; set; }
        public List<TestConfig> Tests { get; set; }
    }
    public class DevicesConfig
    {
        public Dictionary<string, string> Input { get; set; }
        public Dictionary<string, string> Output { get; set; }
    }
    public class TestConfig
    {
        public string id { get; set; }
        public string name { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("Playback Out")]
        public string PlaybackOut { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("Recording In")]
        public string RecordingIn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Playback Volume")]
        public double? PlaybackVolume { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("Recording Gain")]
        public double? RecordingGain { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("THD Limit")]
        public double? ThdLimit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("RubBuzz Test Freq")]
        public double? RubBuzzTestFreq { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("RubBuzz Limit")]
        public double? RubBuzzLimit { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("enable")]
        public bool? Enable { get; set; }

        public bool IsEnabled => Enable ?? true;
    }

    public class AppConfig
    {
        public double PlaybackVolume { get; set; } = 80;
        public double RecordingGain { get; set; } = 100;
        public double FreqTolerance { get; set; } = 3.0;
        public double ThdLimit { get; set; } = 0.5;
        public bool UseUsbPlayback { get; set; } = true;
        public string LastSerialNumber { get; set; } = "";
    }

    public class DeviceItem
    {
        public MMDevice Device { get; set; }
        public string DisplayName { get; set; }

        public DeviceItem(MMDevice device, string displayName)
        {
            Device = device;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }


    public partial class MainWindow : Window
    {
        private AudioEngine _audioEngine;
        private TestRunner _testRunner;

        private AudioRouting _audioRoutingView;
        private VisualAI _visualAIView;
        private QrScanWindow? _qrScanView;
        private CheckingConfig _checkingConfig;
        private List<ProductInfo> _serverProducts = new List<ProductInfo>();
        private string? _lastQrCode;

        public MainWindow()
        {
            InitializeComponent();
            
            _audioEngine = new AudioEngine();
            _testRunner = new TestRunner(_audioEngine);

            // Instantiate views
            _audioRoutingView = new AudioRouting();
            _audioRoutingView.InitializeRouting(_audioEngine, _testRunner);

            _visualAIView = new VisualAI();

            // Set logged in staff ID information dynamically
            if (!string.IsNullOrEmpty(ServerEngine.UserName))
            {
                TxtStaffWelcome.Text = $"Xin chào, {ServerEngine.UserName}";
            }
            else
            {
                TxtStaffWelcome.Text = "Xin chào, Nhân viên";
            }

            // Default to Audio Routing tab
            SwitchToTab("AudioRouting");

            // Load configurations for models selection
            LoadCheckingConfig();
            LoadLastSerialNumber();
            _ = LoadServerModelsAsync();
        }

        private void LoadLastSerialNumber()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "routing_value.json");
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppConfig>(json);
                    if (config != null && !string.IsNullOrEmpty(config.LastSerialNumber))
                    {
                        TxtSerialNumber.Text = config.LastSerialNumber;
                    }
                }
            }
            catch { }
            UpdateQrBarcode();
        }

    private void LoadCheckingConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    _checkingConfig = JsonSerializer.Deserialize<CheckingConfig>(json) ?? new CheckingConfig();
                    
                    if (_checkingConfig != null && _checkingConfig.models != null)
                    {
                        ComboModels.Items.Clear();
                        foreach (var m in _checkingConfig.models)
                        {
                            EnsureItemSlots(m);
                            ComboModels.Items.Add(m.model);
                        }
                    }
                }
                else
                {
                    _checkingConfig = new CheckingConfig();
                }
            }
            catch
            {
                _checkingConfig ??= new CheckingConfig();
            }
        }

        private void EnsureAllModelsHaveItemSlots()
        {
            if (_checkingConfig?.models != null)
            {
                foreach (ModelConfig model in _checkingConfig.models)
                {
                    EnsureItemSlots(model);
                }
            }
        }

        private static List<ItemSlotConfig> EnsureItemSlots(ModelConfig model)
        {
            model.items ??= new List<ItemSlotConfig>();
            int count = Math.Max(model.itemCount, model.items.Count);
            count = Math.Max(1, Math.Min(20, count == 0 ? 3 : count));
            model.itemCount = count;
            
            for (int i = 1; i <= count; i++)
            {
                if (!model.items.Any(value => value.slot == i))
                {
                    model.items.Add(new ItemSlotConfig
                    {
                        slot = i,
                        name = $"Item {i}"
                    });
                }
            }
            
            model.items = model.items
                .Where(value => value.slot >= 1 && value.slot <= count)
                .OrderBy(value => value.slot)
                .ToList();
            return model.items;
        }

        private void SaveCheckingConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                string json = JsonSerializer.Serialize(_checkingConfig ?? new CheckingConfig(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(configPath, json);
            }
            catch
            {
                // Config sync is best-effort; the app can keep the in-memory defaults.
            }
        }

        private IReadOnlyList<ItemSlotConfig> GetItemSlotsForModel(string? modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return new List<ItemSlotConfig>();
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            return modelConfig?.items ?? new List<ItemSlotConfig>();
        }

        private async Task LoadServerModelsAsync()
        {
            try
            {
                var products = await ServerEngine.GetProductsAsync(1, 100);
                _serverProducts = products.ToList();

                var serverModels = _serverProducts
                    .Select(p => p.Model ?? p.ProductCode ?? p.Name)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v)
                    .ToList();

                if (serverModels.Count == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    _checkingConfig ??= new CheckingConfig();
                    foreach (string model in serverModels)
                    {
                        bool exists = ComboModels.Items.Cast<object>().Any(item =>
                            string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase));

                        if (!exists)
                        {
                            ComboModels.Items.Add(model);
                        }

                        ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                            string.Equals(value.model, model, StringComparison.OrdinalIgnoreCase));
                        if (modelConfig == null)
                        {
                            modelConfig = new ModelConfig { model = model, itemCount = 3 };
                            _checkingConfig.models.Add(modelConfig);
                        }
                        EnsureItemSlots(modelConfig);
                    }
                    SaveCheckingConfig();
                });
            }
            catch
            {
                // Keep local checking_config.json models when server is offline or unauthorized.
            }
        }

        private void ComboModels_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ComboModels.SelectedItem == null || _checkingConfig == null) return;
            
            string selectedModelName = ComboModels.SelectedItem.ToString();
            // Reload the dedicated item file so edits saved while the app is open
            // take effect immediately when a model is selected.
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var newConfig = JsonSerializer.Deserialize<CheckingConfig>(json) ?? new CheckingConfig();
                    // Merge new values into existing object to not break references or replace entirely
                    _checkingConfig = newConfig;
                }
            }
            catch { }
            
            EnsureAllModelsHaveItemSlots();
            _qrScanView?.SetItemSlots(GetItemSlotsForModel(selectedModelName));
            UpdateQrBarcode();
            var modelConfig = _checkingConfig.models.FirstOrDefault(m => m.model == selectedModelName);
            if (modelConfig == null || modelConfig.testItems?.InOut == null) return;

            bool success = _audioRoutingView.ApplyModelDevices(modelConfig.testItems.InOut, out string missingMessage);
            if (!success)
            {
                ModernMessageBox.Show(this, 
                    $"Chưa đủ các ngõ vào và ra đã định nghĩa\n\nThiếu ngõ:\n{missingMessage}", 
                    "Không Đạt Cấu Hình Thiết Bị", 
                    ModernMessageBox.MessageBoxType.Warning);
            }
        }

        private void TxtSerialNumber_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateQrBarcode();
        }

        private void UpdateQrBarcode()
        {
            if (_qrScanView != null)
            {
                string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
                string serial = TxtSerialNumber.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(modelName) && !string.IsNullOrEmpty(serial) && serial != "DEFAULT-00001")
                {
                    _qrScanView.SetDefaultProductCode($"{modelName} - {serial}");
                }
            }
        }

        private bool _isLoggingOut = false;

        private async void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            // Set flag to true to skip OnClosing prompt
            _isLoggingOut = true;

            // Reset cached auth values in ServerEngine
            await ServerEngine.LogoutAsync();

            // Open LoginWindow and close current MainWindow
            LoginWindow login = new LoginWindow();
            App.Current.MainWindow = login;
            login.Show();
            this.Close();
        }

        private void BtnScanQr_Click(object sender, RoutedEventArgs e)
        {
            string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(modelName);
            _qrScanView = new QrScanWindow(itemSlots);
            _qrScanView.ScanCompleted += QrScanView_ScanCompleted;
            
            string serial = TxtSerialNumber.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(modelName) && !string.IsNullOrEmpty(serial))
            {
                _qrScanView.SetDefaultProductCode($"{modelName} - {serial}");
            }
            
            _qrScanView.ShowProductDetails(ServerEngine.CurrentProduct, ServerEngine.CurrentProduct?.ProductCode);
            MainContentArea.Content = _qrScanView;
            SwitchToTab("QrScan");
        }

        private async void QrScanView_ScanCompleted(object? sender, QrScanCompletedEventArgs e)
        {
            if (_qrScanView != null)
            {
                _qrScanView.IsEnabled = false;
            }

            try
            {
                await ProcessQrScanAsync(e.ProductQrCode, e.ScannedItems);
            }
            finally
            {
                if (_qrScanView != null)
                {
                    _qrScanView.IsEnabled = true;
                }
            }
        }

        private void QrScanView_AddItemRequested(object? sender, EventArgs e)
        {
            string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(modelName))
            {
                ModernMessageBox.Show(this, "Hãy chọn model trước khi thêm item.", "Chưa chọn model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                modelConfig = new ModelConfig { model = modelName, itemCount = 1 };
                _checkingConfig.models.Add(modelConfig);
            }

            int currentCount = modelConfig.itemCount > 0 ? modelConfig.itemCount : modelConfig.items?.Count ?? 0;
            if (currentCount >= 20)
            {
                ModernMessageBox.Show(this, "Giao diện hiện hỗ trợ tối đa 20 item cho một model.", "Đủ số item", ModernMessageBox.MessageBoxType.Info);
                return;
            }

            modelConfig.itemCount = currentCount + 1;
            EnsureItemSlots(modelConfig);
            SaveCheckingConfig();
            _qrScanView?.SetItemSlots(modelConfig.items);
            ModernMessageBox.Show(this,
                $"Đã thêm Item {modelConfig.itemCount} cho model {modelName} và lưu vào checking_config.json.",
                "Đã cập nhật cấu hình item",
                ModernMessageBox.MessageBoxType.Info);
        }

        private async Task ProcessQrScanAsync(string qrCode, IReadOnlyList<QrItemScan> scannedItems)
        {
            qrCode = qrCode.Trim();
            if (string.IsNullOrWhiteSpace(qrCode))
            {
                ModernMessageBox.Show(this, "Chưa nhận được mã từ máy quét QR.", "Chưa có mã QR", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            _lastQrCode = qrCode;
            BtnScanQr.IsEnabled = false;
            try
            {
                string selectedModel = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
                string selectedSerial = TxtSerialNumber.Text.Trim();
                ProductInfo? product = await ServerEngine.GetProductByQrCodeAsync(qrCode);

                // A printed QR may be different from the server barcode. If the
                // operator has already selected the matching serial/model, use
                // that authoritative server lookup instead of rejecting a valid
                // product just because the QR payload is an alias.
                bool usedSerialFallback = false;
                if (product is null
                    && !string.IsNullOrWhiteSpace(selectedSerial)
                    && !string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase))
                {
                    ProductInfo? bySerial = await RequestProductStatusAsync(selectedSerial, selectedModel);
                    if (bySerial is not null)
                    {
                        product = bySerial;
                        usedSerialFallback = true;
                    }
                }

                if (product is null)
                {
                    string detail = ServerEngine.LastError ?? "Mã này chưa được đăng ký trên server.";
                    ModernMessageBox.Show(this,
                        $"Đã nhận mã QR: {qrCode}\n\n{detail}\n\nNếu đây là sản phẩm mới, hãy chọn model rồi nhấn Add Product.",
                        "Không tìm thấy sản phẩm",
                        ModernMessageBox.MessageBoxType.Warning);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(selectedModel)
                    && !string.IsNullOrWhiteSpace(product.Model)
                    && !string.Equals(selectedModel, product.Model.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Model đang chọn ({selectedModel}) không khớp model của sản phẩm ({product.Model}).\n\nChỉ đồng bộ khi chọn đúng model.",
                        "Sai model",
                        ModernMessageBox.MessageBoxType.Warning);
                    return;
                }

                bool placeholderSerial = string.IsNullOrWhiteSpace(selectedSerial)
                    || string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase);
                if (!placeholderSerial
                    && !string.IsNullOrWhiteSpace(product.SerialNumber)
                    && !string.Equals(selectedSerial, product.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Serial đang nhập ({selectedSerial}) không khớp serial của sản phẩm ({product.SerialNumber}).\n\nChỉ đồng bộ khi chọn đúng serial.",
                        "Sai serial number",
                        ModernMessageBox.MessageBoxType.Warning);
                    return;
                }

                TxtSerialNumber.Text = product.SerialNumber ?? qrCode;
                SelectModel(product.Model);
                IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(product.Model);
                var itemsToLink = scannedItems
                    .Select(scannedItem => new
                    {
                        Scan = scannedItem,
                        Slot = itemSlots.FirstOrDefault(value => value.slot == scannedItem.SlotIndex)
                    })
                    .Where(value => value.Slot != null)
                    .Select(value => new ProductItemLinkInput(
                        value.Scan.Code,
                        value.Slot!.name,
                        value.Scan.SlotIndex))
                    .ToList();

                int linkedCount = itemsToLink.Count;
                if (linkedCount > 0)
                {
                    ProductInfo? updated = await ServerEngine.LinkProductItemsAsync(product, itemsToLink);
                    if (updated == null)
                    {
                        ModernMessageBox.Show(this,
                            ServerEngine.LastError ?? "Không thể đồng bộ danh sách item.",
                            "Lỗi đồng bộ item",
                            ModernMessageBox.MessageBoxType.Error);
                        return;
                    }
                    product = updated;
                }
                _visualAIView.SetCurrentProduct(product);
                _audioRoutingView.SetCurrentProduct(product);
                _qrScanView?.ShowProductDetails(product, qrCode);
                
                ModernMessageBox.Show(this,
                    $"Đã quét QR: {qrCode}\nSerial: {product.SerialNumber ?? "-"}\nModel: {product.Model ?? "-"}\n"
                    + (usedSerialFallback ? "Đã đối chiếu theo Serial/Model.\n" : "")
                    + $"Đã đồng bộ item: {linkedCount}/{itemSlots.Count}",
                    "Đồng bộ QR thành công",
                    ModernMessageBox.MessageBoxType.Info);
                    
                _qrScanView?.AddToHistoryAndReset(product, scannedItems, itemSlots);
            }
            finally
            {
                BtnScanQr.IsEnabled = true;
            }
        }

        private void SelectModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            foreach (object item in ComboModels.Items)
            {
                if (string.Equals(item?.ToString(), model, StringComparison.OrdinalIgnoreCase))
                {
                    ComboModels.SelectedItem = item;
                    return;
                }
            }

            ComboModels.Items.Add(model);
            ComboModels.SelectedItem = model;
        }

        private async void BtnAddProduct_Click(object sender, RoutedEventArgs e)
        {
            string serial = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(model))
            {
                ModernMessageBox.Show(this, "Vui lòng nhập Serial Number và chọn Model để thêm sản phẩm!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            BtnAddProduct.IsEnabled = false;
            try
            {
                string barcodeOrQr = $"{model} - {serial}";
                ProductInfo? product = await ServerEngine.AddProductAsync(barcodeOrQr, serial, model);
                if (product != null)
                {
                    ModernMessageBox.Show(this, $"Sản phẩm {serial} (Model: {model}) đã được thêm thành công lên server!", "Thêm thành công", ModernMessageBox.MessageBoxType.Info);
                    
                    // Optionally, update the visual AI view product if the user adds it while testing
                    _visualAIView.SetCurrentProduct(product);
                    _audioRoutingView.SetCurrentProduct(product);
                    
                    SwitchToTab("QrScan");
                    _qrScanView?.ShowProductDetails(product, barcodeOrQr);
                    _qrScanView?.FocusItem(1);
                }
                else
                {
                    ModernMessageBox.Show(this, ServerEngine.LastError ?? "Có lỗi xảy ra khi thêm sản phẩm.", "Lỗi thêm sản phẩm", ModernMessageBox.MessageBoxType.Error);
                }
            }
            finally
            {
                BtnAddProduct.IsEnabled = true;
            }
        }

        private async void BtnCheckStatus_Click(object sender, RoutedEventArgs e)
        {
            string serial = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serial))
            {
                ModernMessageBox.Show(this, "Vui lòng nhập hoặc quét mã Serial Number trước khi kiểm tra!", "Thông báo", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ProductInfo? product = await RequestProductStatusAsync(serial, model);
            bool passed = product is not null;
            if (passed)
            {
                _visualAIView.SetCurrentProduct(product);
                _audioRoutingView.SetCurrentProduct(product);
                _qrScanView?.ShowProductDetails(product, product.ProductCode ?? serial);
                string details = $"Thiết bị (Serial: {serial}) đã được kiểm tra trạng thái thành công!";
                if (!string.IsNullOrWhiteSpace(product?.Model))
                {
                    details += $"\nModel: {product.Model}";
                }
                if (!string.IsNullOrWhiteSpace(product?.ProductCode))
                {
                    details += $"\nMã sản phẩm: {product.ProductCode}";
                }
                if (!string.IsNullOrWhiteSpace(product?.QaStatus))
                {
                    details += $"\nTrạng thái QA: {product.QaStatus}";
                }
                if (!string.IsNullOrWhiteSpace(product?.QcStatus))
                {
                    details += $"\nTrạng thái QC: {product.QcStatus}";
                }

                ModernMessageBox.Show(this, details, "Kết quả trạng thái", ModernMessageBox.MessageBoxType.Info);
            }
            else
            {
                _visualAIView.SetCurrentProduct(null);
                _audioRoutingView.SetCurrentProduct(null);
                string errorMsg = ServerEngine.LastError != null && ServerEngine.LastError.Contains("Không tìm thấy")
                    ? $"Thiết bị (Serial: {serial}) chưa tồn tại trên server. Vui lòng đăng ký sản phẩm trước!"
                    : ServerEngine.LastError ?? $"Thiết bị (Serial: {serial}) có trạng thái không khả dụng hoặc lỗi kết nối!";
                ModernMessageBox.Show(this, errorMsg, "Chưa đăng ký sản phẩm", ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async Task<ProductInfo?> RequestProductStatusAsync(string serialNumber, string model)
        {
            var product = await ServerEngine.CheckProductStatusAsync(serialNumber, model);
            if (product == null && ServerEngine.LastError != null && ServerEngine.LastError.Contains("Không tìm thấy"))
            {
                product = await ServerEngine.GetProductBySerialAsync(serialNumber);
            }
            return product;
        }

        private void SwitchToTab(string tabName)
        {
            if (tabName == "AudioRouting")
            {
                MainContentArea.Content = _audioRoutingView;
                
                // Highlight active button (Green theme)
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)); // neon green
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27)); // Dark background
                
                // Muted tab
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
                BtnTabVisualAI.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17)); // Darker
                BtnScanQr.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
                BtnScanQr.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
                BtnScanQr.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
            }
            else if (tabName == "VisualAI")
            {
                MainContentArea.Content = _visualAIView;

                // Highlight active button (Blue theme)
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246)); // neon blue
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(59, 130, 246));
                BtnTabVisualAI.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
                
                // Muted tab
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122)); // muted zinc-500
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)); // zinc-800
                BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
                BtnScanQr.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
                BtnScanQr.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
                BtnScanQr.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
            }
            else if (tabName == "QrScan")
            {
                if (_qrScanView == null)
                {
                    _qrScanView = new QrScanWindow(GetItemSlotsForModel(ComboModels.SelectedItem?.ToString()));
                    _qrScanView.ScanCompleted += QrScanView_ScanCompleted;
                    _qrScanView.AddItemRequested += QrScanView_AddItemRequested;
                    _qrScanView.ShowProductDetails(ServerEngine.CurrentProduct, ServerEngine.CurrentProduct?.ProductCode);
                    UpdateQrBarcode();
                }
                MainContentArea.Content = _qrScanView;

                BtnScanQr.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(167, 139, 250));
                BtnScanQr.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 92, 246));
                BtnScanQr.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27));
                BtnTabAudioRouting.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
                BtnTabAudioRouting.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
                BtnTabAudioRouting.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
                BtnTabVisualAI.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(113, 113, 122));
                BtnTabVisualAI.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42));
                BtnTabVisualAI.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 17));
            }
        }

        private void BtnTabAudioRouting_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("AudioRouting");
        }

        private void BtnTabVisualAI_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("VisualAI");
        }

        private bool _isFullscreen = false;
        private WindowStyle _previousWindowStyle;
        private WindowState _previousWindowState;
        private ResizeMode _previousResizeMode;

        private void BtnToggleFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullscreen)
            {
                _previousWindowStyle = this.WindowStyle;
                _previousWindowState = this.WindowState;
                _previousResizeMode = this.ResizeMode;

                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                this.WindowStyle = _previousWindowStyle;
                this.ResizeMode = _previousResizeMode;
                this.WindowState = _previousWindowState;
                _isFullscreen = false;
            }
        }

        private void BtnMinimizeApp_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnCloseApp_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DependencyObject dep = (DependencyObject)e.OriginalSource;
                while (dep != null)
                {
                    if (dep is System.Windows.Controls.Primitives.ButtonBase || 
                        dep is System.Windows.Controls.TextBox || 
                        dep is System.Windows.Controls.ComboBox)
                    {
                        return;
                    }
                    if (dep is System.Windows.Media.Visual)
                    {
                        dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                    }
                    else
                    {
                        dep = System.Windows.LogicalTreeHelper.GetParent(dep);
                    }
                }
                this.DragMove();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isLoggingOut)
            {
                base.OnClosing(e);
                return;
            }

            bool exit = ModernMessageBox.Show(
                this,
                "Bạn có muốn thoát chương trình Sonca Audio Inspector không?", 
                "Xác nhận thoát", 
                ModernMessageBox.MessageBoxType.Confirmation);

            if (!exit)
            {
                e.Cancel = true;
            }
            else
            {
                try { ServerEngine.LogoutAsync().Wait(500); } catch { }
                base.OnClosing(e);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioEngine?.Dispose();
            base.OnClosed(e);
        }
    }
}
