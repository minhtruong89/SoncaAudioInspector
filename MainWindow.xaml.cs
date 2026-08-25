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

        [System.Text.Json.Serialization.JsonPropertyName("assemblyLayoutLocked")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public bool itemLayoutLocked { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ProductIdLayoutConfig? productIdLayout { get; set; }
    }
    public class ProductIdLayoutConfig
    {
        public double? layoutX { get; set; }
        public double? layoutY { get; set; }
        public double? layoutXRatio { get; set; }
        public double? layoutYRatio { get; set; }
        public double scale { get; set; } = 1.0;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int rotation { get; set; }
    }
    public class ItemSlotConfig
    {
        public int slot { get; set; }
        public string name { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutX { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutY { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutXRatio { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? layoutYRatio { get; set; }
        public double scale { get; set; } = 1.0;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
        public int rotation { get; set; }
    }
    public class TestItems
    {
        public InOutConfig InOut { get; set; } = new InOutConfig();
    }
    public class InOutConfig
    {
        public string Description { get; set; } = "";
        public DevicesConfig Devices { get; set; } = new DevicesConfig();
        public List<TestConfig> Tests { get; set; } = new List<TestConfig>();
    }
    public class DevicesConfig
    {
        public Dictionary<string, string> Input { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> Output { get; set; } = new Dictionary<string, string>();
    }
    public class TestConfig
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Playback Out")]
        public string PlaybackOut { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("Recording In")]
        public string RecordingIn { get; set; } = "";

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
        private CheckingConfig _checkingConfig = new CheckingConfig();
        private string? _lastBomDirectory;
        private List<ProductInfo> _serverProducts = new List<ProductInfo>();
        private string? _lastQrCode;
        private Task<ProductResolveResult?>? _creatingProductTask;
        private Task<ProductInfo?>? _backgroundItemSyncTask;
        private string? _backgroundItemFingerprint;
        private string? _qrSessionModel;

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
            ComboModels.SelectedIndex = -1;
            LoadLastSerialNumber();
            _ = LoadServerModelsAsync();
        }

        private void LoadLastSerialNumber()
        {
            // Product IDs are scanned per BOM session. Never restore the last
            // serial because it can accidentally pair the next component set
            // with the previous product.
            TxtSerialNumber.Clear();
            UpdateQrBarcode();
        }

    private void LoadCheckingConfig()
        {
            try
            {
                string configPath = GetCheckingConfigReadPath();
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

        private bool SaveCheckingConfig()
        {
            try
            {
                string configPath = GetUserCheckingConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                string json = JsonSerializer.Serialize(_checkingConfig ?? new CheckingConfig(), new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(configPath, json);
                try
                {
                    string portablePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
                    if (!string.Equals(portablePath, configPath, StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(portablePath, json);
                    }
                }
                catch { }
                return true;
            }
            catch
            {
                // Config sync is best-effort; the app can keep the in-memory defaults.
                return false;
            }
        }

        private static string GetUserCheckingConfigPath() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoncaAudioInspector",
            "checking_config.json");

        private static string GetCheckingConfigReadPath()
        {
            string userPath = GetUserCheckingConfigPath();
            return File.Exists(userPath)
                ? userPath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checking_config.json");
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
                    .OfType<string>()
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
            
            string selectedModelName = ComboModels.SelectedItem.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(selectedModelName)) return;
            // Reload the dedicated item file so edits saved while the app is open
            // take effect immediately when a model is selected.
            try
            {
                string configPath = GetCheckingConfigReadPath();
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
            _qrScanView?.SetLayoutLocked(_checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, selectedModelName, StringComparison.OrdinalIgnoreCase))?.itemLayoutLocked == true);
            bool firstQrModelSelection = _qrScanView != null
                && ReferenceEquals(MainContentArea.Content, _qrScanView)
                && !string.Equals(_qrSessionModel, selectedModelName, StringComparison.OrdinalIgnoreCase);
            if (firstQrModelSelection)
            {
                TxtSerialNumber.Clear();
                _qrScanView?.SetDefaultProductCode("");
            }
            else
            {
                UpdateQrBarcode();
            }
            _ = LoadModelLayoutFromServerAsync(selectedModelName, refreshQrView: true);
            if (_qrScanView != null && ReferenceEquals(MainContentArea.Content, _qrScanView))
            {
                _qrSessionModel = selectedModelName;
                _qrScanView.SetProductModel(selectedModelName, _checkingConfig.models.FirstOrDefault(value =>
                    string.Equals(value.model, selectedModelName, StringComparison.OrdinalIgnoreCase))?.productIdLayout);
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
            }
            else
            {
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
            }
            var modelConfig = _checkingConfig.models.FirstOrDefault(m => m.model == selectedModelName);
            if (modelConfig == null || modelConfig.testItems?.InOut == null) return;

            bool success = _audioRoutingView.ApplyModelDevices(modelConfig.testItems.InOut, out string? missingMessage);
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

        private void TxtSerialNumber_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (BtnAddProduct is null || TxtSerialNumber is null || ComboModels is null) return;

            BtnAddProduct.IsEnabled = _creatingProductTask is null
                && !string.IsNullOrWhiteSpace(TxtSerialNumber.Text)
                && !string.Equals(TxtSerialNumber.Text.Trim(), "DEFAULT-00001", StringComparison.OrdinalIgnoreCase)
                && ComboModels.SelectedItem != null;
            UpdateQrBarcode();
        }

        private void TxtSerialNumber_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            e.Handled = true;
            StartProductFromSerial();
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
            await ServerEngine.LogoutAsync(clearRememberedLogin: true);

            // Open LoginWindow and close current MainWindow
            LoginWindow login = new LoginWindow();
            App.Current.MainWindow = login;
            login.Show();
            this.Close();
        }

        private void BtnScanQr_Click(object sender, RoutedEventArgs e)
        {
            string modelName = _qrSessionModel ?? "";
            if (!string.IsNullOrWhiteSpace(modelName)) SelectModel(modelName);
            IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(modelName);
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            _qrScanView = new QrScanWindow(itemSlots, modelConfig?.productIdLayout, modelName, focusSerialOnLoad: !string.IsNullOrWhiteSpace(modelName));
            _qrScanView.ScanCompleted += QrScanView_ScanCompleted;
            _qrScanView.ItemCommitted += QrScanView_ItemCommitted;
            _qrScanView.CancelRequested += QrScanView_CancelRequested;
            _qrScanView.AddItemRequested += QrScanView_AddItemRequested;
            _qrScanView.LayoutSaveRequested += QrScanView_LayoutSaveRequested;
            _qrScanView.BomImportRequested += QrScanView_BomImportRequested;
            _qrScanView.SetLayoutLocked(_checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase))?.itemLayoutLocked == true);
            
            _qrScanView.ShowProductDetails(ServerEngine.CurrentProduct, ServerEngine.CurrentProduct?.ProductCode);
            MainContentArea.Content = _qrScanView;
            SwitchToTab("QrScan");
            if (string.IsNullOrWhiteSpace(modelName))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ComboModels.Focus();
                    ComboModels.IsDropDownOpen = true;
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private async void QrScanView_ScanCompleted(object? sender, QrScanCompletedEventArgs e)
        {
            if (ComboModels.SelectedItem == null)
            {
                ModernMessageBox.Show(this, "Hãy chọn Model ở thanh trên trước.", "Thiếu Model", ModernMessageBox.MessageBoxType.Warning);
                ComboModels.Focus();
                ComboModels.IsDropDownOpen = true;
                return;
            }

            SetSyncStatus(true, "Đang đồng bộ...");
            if (_qrScanView != null)
            {
                _qrScanView.IsEnabled = false;
            }

            bool syncSucceeded = false;
            try
            {
                syncSucceeded = await ProcessQrScanAsync(e.ProductQrCode, e.ScannedItems);
            }
            catch (Exception ex)
            {
                SetSyncStatus(false, "Lỗi đồng bộ");
                ModernMessageBox.Show(this, ex.Message, "Lỗi đồng bộ", ModernMessageBox.MessageBoxType.Error);
            }
            finally
            {
                SetSyncStatus(false);
                if (_qrScanView != null)
                {
                    _qrScanView.IsEnabled = true;
                }
                _creatingProductTask = null;
                _backgroundItemSyncTask = null;
                _backgroundItemFingerprint = null;
                _qrScanView?.SetItemInputsEnabled(true);
                if (syncSucceeded)
                {
                    TxtSerialNumber.Clear();
                    TxtSerialNumber.Focus();
                    TxtSerialNumber.SelectAll();
                    _qrScanView?.ShowSyncSuccessForOneSecond();
                }
                else
                {
                    BtnAddProduct.IsEnabled = true;
                }
            }
        }

        private void SetSyncStatus(bool isSyncing, string? message = null)
        {
            _qrScanView?.SetSyncStatus(isSyncing, message);
        }

        private async void QrScanView_CancelRequested(object? sender, EventArgs e)
        {
            Task<ProductResolveResult?>? resolveTask = _creatingProductTask;
            Task<ProductInfo?>? itemSyncTask = _backgroundItemSyncTask;
            IReadOnlyList<string> itemCodes = _qrScanView?.GetEnteredItemCodes() ?? Array.Empty<string>();

            // Invalidate callbacks immediately so a late server response cannot
            // repopulate the canceled scan session.
            _creatingProductTask = null;
            _backgroundItemSyncTask = null;
            _backgroundItemFingerprint = null;
            TxtSerialNumber.Clear();
            BtnAddProduct.IsEnabled = false;
            _visualAIView.SetCurrentProduct(null);
            _audioRoutingView.SetCurrentProduct(null);
            _qrScanView?.ResetCancelledSession();

            if (resolveTask is null)
            {
                BtnAddProduct.IsEnabled = true;
                TxtSerialNumber.Focus();
                return;
            }

            _qrScanView?.SetSyncStatus(true, "Đang hủy dữ liệu quét nền...");
            ProductResolveResult? resolution = null;
            try
            {
                resolution = await resolveTask;
                if (itemSyncTask is not null)
                {
                    await itemSyncTask;
                }

                if (resolution is { Created: true })
                {
                    bool rolledBack = await ServerEngine.RollbackNewProductAsync(resolution.Product, itemCodes);
                    if (!rolledBack)
                    {
                        _qrScanView?.ShowTransientError(ServerEngine.LastError ?? "Không thể xóa dữ liệu quét nền");
                    }
                }
            }
            catch
            {
                _qrScanView?.ShowTransientError("Không thể hoàn tất hủy dữ liệu quét nền");
            }
            finally
            {
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetSyncStatus(false, "Đã hủy · hãy quét barcode sản phẩm ở thanh trên");
                TxtSerialNumber.Focus();
            }
        }

        private void QrScanView_ItemCommitted(object? sender, QrItemCommittedEventArgs e)
        {
            if (!e.AllItemsReady)
            {
                _qrScanView?.SetSyncStatus(false, $"Đã nhận Item {e.SlotIndex} · chuyển ô tiếp theo");
                return;
            }

            string productId = _qrScanView?.ProductQrCode ?? "";
            if (e.ScannedItems.GroupBy(item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)
                || e.ScannedItems.Any(item => string.Equals(item.Code.Trim(), productId.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _qrScanView?.ShowTransientError("Barcode item bị trùng · hãy quét lại");
                return;
            }

            StartBackgroundItemSync(productId, e.ScannedItems);
        }

        private void StartBackgroundItemSync(string productId, IReadOnlyList<QrItemScan> scannedItems)
        {
            if (_creatingProductTask is null || string.IsNullOrWhiteSpace(productId)) return;

            string fingerprint = CreateQrFingerprint(productId, scannedItems);
            if (_backgroundItemSyncTask is not null
                && string.Equals(_backgroundItemFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _backgroundItemFingerprint = fingerprint;
            _qrScanView?.SetItemInputsEnabled(false);
            _qrScanView?.SetSyncStatus(true, "Đã nhận đủ item · đang đồng bộ nền...");
            Task<ProductInfo?> syncTask = SyncItemsInBackgroundAsync(_creatingProductTask, scannedItems);
            _backgroundItemSyncTask = syncTask;
            _ = WatchBackgroundItemSyncAsync(syncTask, scannedItems);
        }

        private async Task<ProductInfo?> SyncItemsInBackgroundAsync(
            Task<ProductResolveResult?> resolveTask,
            IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductResolveResult? resolution = await resolveTask;
            if (resolution is null) return null;
            if (!resolution.Created) return resolution.Product;

            IReadOnlyList<ItemSlotConfig> slots = GetItemSlotsForModel(resolution.Product.Model);
            List<ProductItemLinkInput> items = scannedItems
                .Select(scan => new { Scan = scan, Slot = slots.FirstOrDefault(slot => slot.slot == scan.SlotIndex) })
                .Where(value => value.Slot != null)
                .Select(value => new ProductItemLinkInput(value.Scan.Code, value.Slot!.name, value.Scan.SlotIndex))
                .ToList();

            if (items.Count != scannedItems.Count) return null;
            return await ServerEngine.LinkProductItemsAsync(resolution.Product, items);
        }

        private async Task WatchBackgroundItemSyncAsync(
            Task<ProductInfo?> syncTask,
            IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductInfo? product = await syncTask;
            if (!ReferenceEquals(_backgroundItemSyncTask, syncTask)) return;

            if (product is null)
            {
                _backgroundItemSyncTask = null;
                _backgroundItemFingerprint = null;
                _qrScanView?.SetItemInputsEnabled(true);
                string error = ServerEngine.LastError ?? "Đồng bộ nền thất bại · hãy kiểm tra lại item";
                _qrScanView?.SetSyncStatus(false, error);
                RejectServerItems(scannedItems, error);
                return;
            }

            _qrScanView?.SetSyncStatus(false, "Item đã đồng bộ nền · bấm Gửi để hoàn tất");
        }

        private static string CreateQrFingerprint(string productId, IReadOnlyList<QrItemScan> scannedItems)
        {
            string itemPart = string.Join("|", scannedItems
                .OrderBy(item => item.SlotIndex)
                .Select(item => $"{item.SlotIndex}:{item.Code.Trim().ToUpperInvariant()}"));
            return $"{productId.Trim().ToUpperInvariant()}|{itemPart}";
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

            ApplyQrLayout(modelConfig);
            int currentCount = modelConfig.itemCount > 0 ? modelConfig.itemCount : modelConfig.items?.Count ?? 0;
            if (currentCount >= 20)
            {
                ModernMessageBox.Show(this, "Giao diện hiện hỗ trợ tối đa 20 item cho một model.", "Đủ số item", ModernMessageBox.MessageBoxType.Info);
                return;
            }

            modelConfig.itemCount = currentCount + 1;
            List<ItemSlotConfig> slots = EnsureItemSlots(modelConfig);
            double sharedScale = slots
                .Where(item => item.slot <= currentCount)
                .Select(item => item.scale)
                .FirstOrDefault(value => value > 0);
            slots.First(item => item.slot == modelConfig.itemCount).scale = sharedScale > 0 ? sharedScale : 1.0;
            _qrScanView?.SetItemSlots(slots, preserveCodes: true);
        }

        private async void QrScanView_LayoutSaveRequested(object? sender, EventArgs e)
        {
            string modelName = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                ModernMessageBox.Show(this, "Hãy chọn model trước khi lưu bố trí item.", "Chưa chọn model", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            ApplyQrLayout(modelConfig);
            bool localSaved = SaveCheckingConfig();
            bool serverSaved = await ServerEngine.SaveModelScanLayoutAsync(
                modelName,
                modelConfig.items,
                modelConfig.itemLayoutLocked);

            if (localSaved)
            {
                _qrScanView?.ShowLayoutSaved(modelName);
            }
            if (!localSaved || !serverSaved)
            {
                ModernMessageBox.Show(this,
                    !localSaved
                        ? "Không thể ghi checking_config.json. Hãy kiểm tra quyền ghi của thư mục ứng dụng."
                        : ServerEngine.LastError ?? "Đã lưu trên máy nhưng chưa thể lưu cấu hình lên server.",
                    "Lưu cấu hình chưa hoàn tất",
                    ModernMessageBox.MessageBoxType.Error);
            }
        }

        private async void QrScanView_BomImportRequested(object? sender, EventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn đồng thời 2 file BOM_MODELS và ITEMS",
                Filter = "Excel/CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|Tất cả file (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true,
                InitialDirectory = GetNearestBomDirectory()
            };
            if (dialog.ShowDialog(this) != true) return;
            _lastBomDirectory = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault());

            BomImportPackage package;
            try
            {
                package = BomCsvParser.ParsePackage(dialog.FileNames);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(this, ex.Message, "BOM/Items không hợp lệ", ModernMessageBox.MessageBoxType.Error);
                return;
            }

            SetSyncStatus(true, "Đang đồng bộ quy ước BOM và danh mục Items...");
            _qrScanView?.SetItemInputsEnabled(false);
            bool imported = false;
            string? importError = null;
            try
            {
                BomImportResult? result = await ServerEngine.ImportBomAsync(package.BomRows, package.ItemRows);
                if (result is null || result.Definitions.Count == 0)
                {
                    importError = ServerEngine.LastError ?? "Server không trả kết quả import BOM.";
                    SetSyncStatus(false, importError);
                    ModernMessageBox.Show(this,
                        importError,
                        "Import BOM thất bại",
                        ModernMessageBox.MessageBoxType.Error);
                    return;
                }

                foreach (BomDefinitionInfo definition in result.Definitions)
                {
                    ApplyBomDefinitionToLocalConfig(definition);
                }
                SaveCheckingConfig();

                BomDefinitionInfo first = result.Definitions[0];
                _qrSessionModel = first.SpeakerModel;
                SelectModel(first.SpeakerModel);
                ModelConfig config = ApplyBomDefinitionToLocalConfig(first);
                _qrScanView?.SetItemSlots(config.items);
                _qrScanView?.SetLayoutLocked(true);
                _qrScanView?.SetProductModel(first.SpeakerModel, config.productIdLayout);
                _qrScanView?.SetBomDefinition(first);
                _qrScanView?.SetSyncStatus(false,
                    $"Đã import {result.ImportedModels} model · {result.ImportedItems} items");
                imported = true;

                ModernMessageBox.Show(this,
                    $"Đã đồng bộ BOM và Items lên server.\n\nModel: {result.ImportedModels}\nItems: {result.ImportedItems}\nDanh sách: {string.Join(", ", result.Definitions.Select(value => value.SpeakerModel).Distinct(StringComparer.OrdinalIgnoreCase))}\n\nBOM chỉ quy định theo model; hãy quét barcode sản phẩm để bắt đầu.",
                    "Import BOM thành công",
                    ModernMessageBox.MessageBoxType.Info);
                TxtSerialNumber.Clear();
                TxtSerialNumber.Focus();
            }
            finally
            {
                _qrScanView?.SetItemInputsEnabled(true);
                if (!imported) SetSyncStatus(false, importError ?? "Import BOM chưa hoàn tất");
            }
        }

        private string GetNearestBomDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_lastBomDirectory) && Directory.Exists(_lastBomDirectory))
            {
                return _lastBomDirectory;
            }

            string? directory = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    bool hasBomFile = Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(path =>
                            (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                                || Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                            && Path.GetFileName(path).Contains("BOM", StringComparison.OrdinalIgnoreCase));
                    if (hasBomFile) return directory;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                string? parent = Directory.GetParent(directory)?.FullName;
                if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)) break;
                directory = parent;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private ModelConfig ApplyBomDefinitionToLocalConfig(BomDefinitionInfo definition)
        {
            _checkingConfig ??= new CheckingConfig();
            ModelConfig? config = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, definition.SpeakerModel, StringComparison.OrdinalIgnoreCase));
            if (config is null)
            {
                config = new ModelConfig { model = definition.SpeakerModel };
                _checkingConfig.models.Add(config);
            }

            Dictionary<int, ItemSlotConfig> existing = (config.items ?? new List<ItemSlotConfig>())
                .GroupBy(item => item.slot)
                .ToDictionary(group => group.Key, group => group.First());
            config.items = definition.Components
                .OrderBy(component => component.SlotIndex)
                .Select(component =>
                {
                    existing.TryGetValue(component.SlotIndex, out ItemSlotConfig? layout);
                    return new ItemSlotConfig
                    {
                        slot = component.SlotIndex,
                        name = component.ComponentName,
                        layoutX = layout?.layoutX,
                        layoutY = layout?.layoutY,
                        layoutXRatio = layout?.layoutXRatio,
                        layoutYRatio = layout?.layoutYRatio,
                        scale = layout?.scale > 0 ? layout.scale : 1.0,
                        rotation = layout?.rotation ?? 0
                    };
                })
                .ToList();
            config.itemCount = config.items.Count;
            config.itemLayoutLocked = true;
            return config;
        }

        private async Task<bool> LoadModelLayoutFromServerAsync(string? modelName, bool refreshQrView)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            ModelScanLayoutInfo? serverLayout = await ServerEngine.GetModelScanLayoutAsync(modelName);
            if (serverLayout?.Items is not { Count: > 0 }) return false;

            _checkingConfig ??= new CheckingConfig();
            ModelConfig? modelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelConfig == null)
            {
                modelConfig = new ModelConfig { model = modelName };
                _checkingConfig.models.Add(modelConfig);
            }

            modelConfig.items = serverLayout.Items
                .OrderBy(item => item.slot)
                .Take(20)
                .ToList();
            modelConfig.itemCount = modelConfig.items.Count;
            modelConfig.itemLayoutLocked = serverLayout.Locked;
            SaveCheckingConfig();

            if (refreshQrView)
            {
                _qrScanView?.SetItemSlots(modelConfig.items, preserveCodes: true);
                _qrScanView?.SetLayoutLocked(modelConfig.itemLayoutLocked);
            }
            return true;
        }

        private void ApplyQrLayout(ModelConfig modelConfig)
        {
            if (_qrScanView == null) return;
            List<ItemSlotConfig> editedLayout = _qrScanView.GetItemLayout().ToList();
            ProductIdLayoutConfig editedProductId = _qrScanView.GetProductIdLayout();

            if (modelConfig.itemLayoutLocked)
            {
                Dictionary<int, ItemSlotConfig> positionsBySlot = editedLayout.ToDictionary(item => item.slot);
                foreach (ItemSlotConfig item in modelConfig.items)
                {
                    if (!positionsBySlot.TryGetValue(item.slot, out ItemSlotConfig? edited)) continue;
                    item.layoutX = edited.layoutX;
                    item.layoutY = edited.layoutY;
                    item.layoutXRatio = edited.layoutXRatio;
                    item.layoutYRatio = edited.layoutYRatio;
                }

                modelConfig.productIdLayout ??= new ProductIdLayoutConfig();
                modelConfig.productIdLayout.layoutX = editedProductId.layoutX;
                modelConfig.productIdLayout.layoutY = editedProductId.layoutY;
                modelConfig.productIdLayout.layoutXRatio = editedProductId.layoutXRatio;
                modelConfig.productIdLayout.layoutYRatio = editedProductId.layoutYRatio;
            }
            else
            {
                modelConfig.items = editedLayout;
                modelConfig.productIdLayout = editedProductId;
            }

            modelConfig.itemCount = modelConfig.items.Count;
        }

        private async Task<bool> ProcessQrScanAsync(string qrCode, IReadOnlyList<QrItemScan> scannedItems)
        {
            qrCode = qrCode.Trim();
            if (string.IsNullOrWhiteSpace(qrCode))
            {
                ModernMessageBox.Show(this, "Chưa nhận được ID (Serial sản phẩm) từ máy quét.", "Chưa có ID sản phẩm", ModernMessageBox.MessageBoxType.Warning);
                return false;
            }

            _lastQrCode = qrCode;
            BtnScanQr.IsEnabled = false;
            try
            {
                string selectedModel = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
                string selectedSerial = TxtSerialNumber.Text.Trim();
                string scannedSerial = qrCode;
                string idPrefix = selectedModel + " - ";
                if (!string.IsNullOrWhiteSpace(selectedModel)
                    && qrCode.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    scannedSerial = qrCode[idPrefix.Length..].Trim();
                }
                ProductInfo? product;
                if (_creatingProductTask is not null)
                {
                    ProductResolveResult? resolution = await _creatingProductTask;
                    product = resolution?.Product;
                    if (product is null)
                    {
                        ModernMessageBox.Show(this,
                            ServerEngine.LastError ?? "Không thể thêm sản phẩm lên server.",
                            "Không thể thêm sản phẩm",
                            ModernMessageBox.MessageBoxType.Error);
                        return false;
                    }
                }
                else
                {
                    product = await ServerEngine.GetProductByQrCodeAsync(qrCode);
                    if (product is null)
                    {
                        product = await ServerEngine.GetProductBySerialAsync(scannedSerial);
                    }
                }

                // A printed barcode may be different from the server barcode. If the
                // operator has already selected the matching serial/model, use
                // that authoritative server lookup instead of rejecting a valid
                // product just because the scanned payload is an alias.
                if (product is null
                    && !string.IsNullOrWhiteSpace(selectedSerial)
                    && !string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase))
                {
                    ProductInfo? bySerial = await RequestProductStatusAsync(selectedSerial, selectedModel);
                    if (bySerial is not null)
                    {
                        product = bySerial;
                    }
                }

                if (product is null)
                {
                    string detail = ServerEngine.LastError ?? "Mã này chưa được đăng ký trên server.";
                    ModernMessageBox.Show(this,
                        $"Đã nhận barcode: {qrCode}\n\n{detail}\n\nNếu đây là sản phẩm mới, hãy chọn model rồi nhấn Add Product.",
                        "Không tìm thấy sản phẩm",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(selectedModel)
                    && !string.IsNullOrWhiteSpace(product.Model)
                    && !string.Equals(selectedModel, product.Model.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Model đang chọn ({selectedModel}) không khớp model của sản phẩm ({product.Model}).\n\nChỉ đồng bộ khi chọn đúng model.",
                        "Sai model",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }

                bool placeholderSerial = string.IsNullOrWhiteSpace(selectedSerial)
                    || string.Equals(selectedSerial, "DEFAULT-00001", StringComparison.OrdinalIgnoreCase);
                bool scannedProductSerial = !string.IsNullOrWhiteSpace(product.SerialNumber)
                    && string.Equals(scannedSerial, product.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!placeholderSerial
                    && !scannedProductSerial
                    && !string.IsNullOrWhiteSpace(product.SerialNumber)
                    && !string.Equals(selectedSerial, product.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Serial đang nhập ({selectedSerial}) không khớp serial của sản phẩm ({product.SerialNumber}).\n\nChỉ đồng bộ khi chọn đúng serial.",
                        "Sai serial number",
                        ModernMessageBox.MessageBoxType.Warning);
                    return false;
                }

                TxtSerialNumber.Text = product.SerialNumber ?? qrCode;
                SelectModel(product.Model);
                IReadOnlyList<ItemSlotConfig> itemSlots = GetItemSlotsForModel(product.Model);
                string fingerprint = CreateQrFingerprint(qrCode, scannedItems);
                if (_creatingProductTask is not null)
                {
                    ProductResolveResult? resolution = await _creatingProductTask;
                    if (resolution is { Created: false })
                    {
                        _visualAIView.SetCurrentProduct(product);
                        _audioRoutingView.SetCurrentProduct(product);
                        _qrScanView?.ShowProductDetails(product, qrCode);
                        _qrScanView?.AddToHistoryAndReset(product, scannedItems, itemSlots);
                        return true;
                    }
                }

                if (_backgroundItemSyncTask is not null
                    && string.Equals(_backgroundItemFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    ProductInfo? backgroundProduct = await _backgroundItemSyncTask;
                    if (backgroundProduct is null)
                    {
                        RejectServerItems(scannedItems, ServerEngine.LastError ?? "Không thể đồng bộ danh sách item.");
                        _qrScanView?.SetItemInputsEnabled(true);
                        return false;
                    }

                    product = backgroundProduct;
                    _visualAIView.SetCurrentProduct(product);
                    _audioRoutingView.SetCurrentProduct(product);
                    _qrScanView?.ShowProductDetails(product, qrCode);
                    _qrScanView?.AddToHistoryAndReset(product, scannedItems, itemSlots);
                    return true;
                }

                var existingItemCodes = (product.Items ?? new List<ProductItemInfo>())
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code))
                    .Select(item => item.Code!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var duplicateScans = scannedItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code)
                        && existingItemCodes.Contains(item.Code.Trim()))
                    .ToList();
                if (duplicateScans.Count > 0)
                {
                    string duplicateSlots = string.Join(", ", duplicateScans.Select(item => $"Item {item.SlotIndex}"));
                    _qrScanView?.RejectItems(
                        duplicateScans.Select(item => item.SlotIndex),
                        $"Barcode sản phẩm đã quét trước đó bị trùng · quét lại: {duplicateSlots}");
                    return false;
                }

                List<QrItemScan> scansToLink = scannedItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.Code)
                        && !existingItemCodes.Contains(item.Code.Trim()))
                    .ToList();
                var itemsToLink = scansToLink
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
                        RejectServerItems(scansToLink, ServerEngine.LastError ?? "Không thể đồng bộ danh sách item.");
                        return false;
                    }
                    else
                    {
                        product = updated;
                    }
                }
                _visualAIView.SetCurrentProduct(product);
                _audioRoutingView.SetCurrentProduct(product);
                _qrScanView?.ShowProductDetails(product, qrCode);
                
                _qrScanView?.AddToHistoryAndReset(product, scansToLink, itemSlots);
                return true;
            }
            finally
            {
                BtnScanQr.IsEnabled = true;
            }
        }

        private static bool IsDuplicateItemError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            string normalized = error.Trim().ToLowerInvariant();
            return normalized.Contains("trùng")
                || normalized.Contains("duplicate")
                || normalized.Contains("already exists")
                || normalized.Contains("đã tồn tại")
                || normalized.Contains("da ton tai");
        }

        private void RejectServerItems(IReadOnlyList<QrItemScan> scannedItems, string error)
        {
            List<QrItemScan> rejected = scannedItems
                .Where(scan => !string.IsNullOrWhiteSpace(scan.Code)
                    && error.Contains(scan.Code.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rejected.Count == 0)
            {
                _qrScanView?.ShowTransientError(error);
                return;
            }

            string message = IsDuplicateItemError(error)
                ? $"Barcode {rejected[0].Code} đã thuộc sản phẩm đã quét trước đó"
                : error;
            _qrScanView?.RejectItems(rejected.Select(scan => scan.SlotIndex), message);
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

        private void BtnAddProduct_Click(object sender, RoutedEventArgs e)
        {
            StartProductFromSerial();
        }

        private void StartProductFromSerial()
        {
            if (_creatingProductTask != null) return;
            string scannedValue = TxtSerialNumber.Text.Trim();
            string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
            string serial = scannedValue;
            string barcodeOrQr;

            if (BomCsvParser.TryParseProductQr(scannedValue, out BomProductQrValue? productQr) && productQr is not null)
            {
                if (!string.IsNullOrWhiteSpace(model)
                    && !string.Equals(BomCsvParser.NormalizeModelKey(model), productQr.ModelKey, StringComparison.OrdinalIgnoreCase))
                {
                    ModernMessageBox.Show(this,
                        $"Barcode thuộc model {productQr.ModelKey}, không khớp model đang chọn {model}.",
                        "Sai Model",
                        ModernMessageBox.MessageBoxType.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(model))
                {
                    model = ComboModels.Items.Cast<object>()
                        .Select(item => item?.ToString() ?? "")
                        .FirstOrDefault(item => string.Equals(
                            BomCsvParser.NormalizeModelKey(item),
                            productQr.ModelKey,
                            StringComparison.OrdinalIgnoreCase))
                        ?? productQr.ModelKey;
                    SelectModel(model);
                }
                serial = productQr.ProductSerial;
                barcodeOrQr = productQr.Normalized;
                TxtSerialNumber.Text = serial;
            }
            else
            {
                barcodeOrQr = $"{model} - {serial}";
            }

            if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(model))
            {
                ModernMessageBox.Show(this, "Vui lòng chọn Model và nhập Serial Number trước.", "Thiếu thông tin", ModernMessageBox.MessageBoxType.Warning);
                return;
            }

            _qrSessionModel = model;
            _qrScanView?.SetDefaultProductCode(barcodeOrQr);
            _qrScanView?.PrepareNewProduct(barcodeOrQr);
            _qrScanView?.SetBomDefinition(null);
            _qrScanView?.SetItemInputsEnabled(false);
            BtnAddProduct.IsEnabled = false;
            _backgroundItemSyncTask = null;
            _backgroundItemFingerprint = null;
            Task<ProductResolveResult?> resolveTask = ServerEngine.ResolveProductAsync(barcodeOrQr, serial, model);
            _creatingProductTask = resolveTask;

            SwitchToTab("QrScan");
            _qrScanView?.SetDefaultProductCode(barcodeOrQr);
            _qrScanView?.PrepareNewProduct(barcodeOrQr);
            _qrScanView?.SetBomDefinition(null);
            _qrScanView?.SetItemInputsEnabled(false);
            _ = WatchProductResolutionAsync(resolveTask, barcodeOrQr, model);
        }

        private async Task WatchProductResolutionAsync(
            Task<ProductResolveResult?> resolveTask,
            string productId,
            string selectedModel)
        {
            ProductResolveResult? resolution = await resolveTask;
            if (!ReferenceEquals(_creatingProductTask, resolveTask)) return;

            if (resolution is null)
            {
                _creatingProductTask = null;
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetItemInputsEnabled(true);
                _qrScanView?.SetSyncStatus(false, "Không thể kiểm tra hoặc tạo sản phẩm");
                ModernMessageBox.Show(this,
                    ServerEngine.LastError ?? "Không thể kiểm tra hoặc thêm sản phẩm lên server.",
                    "Không thể thêm sản phẩm",
                    ModernMessageBox.MessageBoxType.Error);
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
                return;
            }

            ProductInfo product = resolution.Product;
            string productModel = product.Model ?? selectedModel;
            if (!string.Equals(productModel, selectedModel, StringComparison.OrdinalIgnoreCase))
            {
                _creatingProductTask = null;
                BtnAddProduct.IsEnabled = true;
                _qrScanView?.SetItemInputsEnabled(true);
                ModernMessageBox.Show(this,
                    $"ID {productId} đã thuộc Model {productModel}.",
                    "Sai Model",
                    ModernMessageBox.MessageBoxType.Warning);
                TxtSerialNumber.Focus();
                TxtSerialNumber.SelectAll();
                return;
            }

            _visualAIView.SetCurrentProduct(product);
            _audioRoutingView.SetCurrentProduct(product);
            string effectiveProductId = productId;
            if (resolution.Bom is not null)
            {
                ModelConfig bomConfig = ApplyBomDefinitionToLocalConfig(resolution.Bom);
                SaveCheckingConfig();
                SelectModel(resolution.Bom.SpeakerModel);
                _qrScanView?.SetItemSlots(bomConfig.items);
                _qrScanView?.SetLayoutLocked(true);
                _qrScanView?.SetProductModel(resolution.Bom.SpeakerModel, bomConfig.productIdLayout);
                _qrScanView?.SetBomDefinition(resolution.Bom);
            }
            else
            {
                _qrScanView?.SetBomDefinition(null);
            }
            _qrScanView?.SetDefaultProductCode(effectiveProductId);
            _qrScanView?.ShowProductDetails(product, effectiveProductId);

            if (resolution.Created)
            {
                _qrScanView?.SetSyncStatus(false, "Đã thêm sản phẩm nền · tiếp tục quét item");
                _qrScanView?.SetItemInputsEnabled(true);
                _qrScanView?.FocusItem(1);
                return;
            }

            // The selected model layout is already in memory. Avoid another
            // network round-trip on the hot scan path; model selection keeps
            // the server layout refreshed independently.
            ModelConfig? existingModelConfig = _checkingConfig.models.FirstOrDefault(value =>
                string.Equals(value.model, productModel, StringComparison.OrdinalIgnoreCase));
            _qrScanView?.SetItemSlots(existingModelConfig?.items);
            _qrScanView?.SetLayoutLocked(existingModelConfig?.itemLayoutLocked == true);
            _qrScanView?.SetProductModel(productModel, existingModelConfig?.productIdLayout);
            _qrScanView?.SetDefaultProductCode(effectiveProductId);
            _qrScanView?.LoadProductItemsFromServer(product);
            _qrScanView?.SetItemInputsEnabled(true);
            _qrScanView?.SetSyncStatus(false, "ID đã tồn tại · đã tải dữ liệu cũ từ server");
            _qrScanView?.ShowTransientError("Serial đã tồn tại · đã tải dữ liệu cũ");
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
            if (product is not null)
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
                    string modelName = _qrSessionModel ?? "";
                    if (!string.IsNullOrWhiteSpace(modelName)) SelectModel(modelName);
                    ModelConfig? modelConfig = _checkingConfig?.models.FirstOrDefault(value =>
                        string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase));
                    _qrScanView = new QrScanWindow(GetItemSlotsForModel(modelName), modelConfig?.productIdLayout, modelName, focusSerialOnLoad: !string.IsNullOrWhiteSpace(modelName));
            _qrScanView.ScanCompleted += QrScanView_ScanCompleted;
            _qrScanView.ItemCommitted += QrScanView_ItemCommitted;
            _qrScanView.CancelRequested += QrScanView_CancelRequested;
                    _qrScanView.AddItemRequested += QrScanView_AddItemRequested;
                    _qrScanView.LayoutSaveRequested += QrScanView_LayoutSaveRequested;
                    _qrScanView.BomImportRequested += QrScanView_BomImportRequested;
                    _qrScanView.SetLayoutLocked(_checkingConfig?.models.FirstOrDefault(value =>
                        string.Equals(value.model, modelName, StringComparison.OrdinalIgnoreCase))?.itemLayoutLocked == true);
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

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
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
                // Keep the window alive briefly so the server can revoke the
                // access and refresh tokens. A fixed 500 ms wait was often too
                // short for a real HTTPS request and caused re-entry to look
                // like a persisted session.
                e.Cancel = true;
                _isLoggingOut = true;
                try
                {
                    using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await ServerEngine.LogoutAsync(cancellation.Token);
                }
                catch
                {
                    // Local cleanup in LogoutAsync still happens if the network
                    // is unavailable; the server-side token expiry is the
                    // fallback for an unexpected shutdown.
                }
                finally
                {
                    Close();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _audioEngine?.Dispose();
            base.OnClosed(e);
        }
    }
}
