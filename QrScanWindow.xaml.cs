using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
#if !WIN_X86
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
#endif
using ZXing;
using ZXing.Common;
using ZXing.Rendering;
using WpfWindow = System.Windows.Window;

namespace SoncaAudioInspector
{
    public sealed class QrItemScan
    {
        public int SlotIndex { get; init; }
        public string Code { get; init; } = "";
    }

    public sealed class QrItemSlotView : System.ComponentModel.INotifyPropertyChanged
    {
        public int Slot { get; init; }
        public string DisplayName { get; init; } = "";

        private string _code = "";
        public string Code 
        { 
            get => _code; 
            set 
            { 
                if (_code != value) 
                {
                    _code = value; 
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Code)));
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BoxColor)));
                }
            } 
        }

        public Brush BoxColor => string.IsNullOrWhiteSpace(Code) ? new SolidColorBrush(Color.FromRgb(39, 39, 42)) : new SolidColorBrush(Color.FromRgb(16, 185, 129));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class BarcodeCardView
    {
        public string Caption { get; init; } = "";
        public string Code { get; init; } = "";
        public BitmapSource? Image { get; init; }
        public string? ItemName { get; init; }
    }

    public sealed class HistoryItemView
    {
        public string ProductTitle { get; init; } = "";
        public IReadOnlyList<HistoryScannedItemView> ScannedItems { get; init; } = Array.Empty<HistoryScannedItemView>();
    }

    public sealed class HistoryScannedItemView
    {
        public string Name { get; init; } = "";
        public string Code { get; init; } = "";
    }

    public sealed class QrScanCompletedEventArgs : EventArgs
    {
        public string ProductQrCode { get; }
        public IReadOnlyList<QrItemScan> ScannedItems { get; }

        public QrScanCompletedEventArgs(string productQrCode, IReadOnlyList<QrItemScan> scannedItems)
        {
            ProductQrCode = productQrCode;
            ScannedItems = scannedItems;
        }
    }

    public partial class QrScanWindow : UserControl
    {
        private const int MaxItems = 20;
        private TextBox? _activeScanTarget;
        private int _itemCount;
#if !WIN_X86
        private VideoCapture? _capture;
        private Thread? _cameraThread;
#endif
        private volatile bool _cameraRunning;
        private int _frameNumber;
        private string _lastDecodedCode = "";
        private DateTime _lastDecodedAt = DateTime.MinValue;
        private bool _isUnloaded;
        private static int _scanCount = 1;

        public event EventHandler<QrScanCompletedEventArgs>? ScanCompleted;
        public event EventHandler? AddItemRequested;

        private WpfWindow? OwnerWindow => WpfWindow.GetWindow(this);
        public string ProductQrCode => TxtProductQr.Text.Trim();
        public IReadOnlyList<QrItemScan> ScannedItems { get; private set; } = new List<QrItemScan>();

        public QrScanWindow(IReadOnlyList<ItemSlotConfig>? itemSlots = null)
        {
            InitializeComponent();
            TxtScanCounter.Text = "SCAN";
            SetItemSlots(itemSlots);
        }

        public void SetItemSlots(IReadOnlyList<ItemSlotConfig>? itemSlots)
        {
            IReadOnlyList<ItemSlotConfig> slots = itemSlots is { Count: > 0 }
                ? itemSlots.OrderBy(item => item.slot).Take(MaxItems).ToList()
                : DefaultSlots();

            _itemCount = Math.Max(1, Math.Min(MaxItems, slots.Count));
            ItemRows.ItemsSource = slots.Select(item => new QrItemSlotView
            {
                Slot = item.slot,
                DisplayName = $"ITEM {item.slot} · {item.name}",
            }).ToList();
        }

        public void ShowProductDetails(ProductInfo? product, string? productBarcode = null)
        {
            if (product is null)
            {
                ProductDetailText.Text = "Chưa có sản phẩm được chọn";
                BarcodeCards.ItemsSource = null;
                return;
            }

            ProductDetailText.Text =
                $"Sản phẩm: {product.DisplayName}\n" +
                $"Model: {product.Model ?? "-"}\n" +
                $"Serial: {product.SerialNumber ?? "-"}\n" +
                $"QA: {product.QaStatus ?? "-"} · QC: {product.QcStatus ?? "-"}";

            var cards = new List<BarcodeCardView>();
            string? productCode = FirstNonEmpty(productBarcode, product.ProductCode, product.SerialNumber);
            if (!string.IsNullOrWhiteSpace(productCode))
            {
                cards.Add(new BarcodeCardView
                {
                    Caption = "SẢN PHẨM · BARCODE",
                    Code = productCode,
                    Image = CreateBarcodeBitmap(productCode, force1D: true),
                });
            }

            foreach (ProductItemInfo item in product.Items.OrderBy(value => value.SlotIndex ?? int.MaxValue))
            {
                if (string.IsNullOrWhiteSpace(item.Code)) continue;
                cards.Add(new BarcodeCardView
                {
                    Caption = $"ITEM {item.SlotIndex?.ToString() ?? "-"} · {item.Name ?? "Item"}",
                    Code = item.Code,
                    ItemName = item.Name,
                    Image = CreateBarcodeBitmap(item.Code, force1D: true),
                });
            }
            BarcodeCards.ItemsSource = cards;
        }

        public void ShowBarcodePreview(string caption, string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show(OwnerWindow, "Chưa có mã để hiển thị barcode.", "Chưa có mã", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ProductDetailText.Text = $"{caption}\nMã: {code.Trim()}";
            BarcodeCards.ItemsSource = new[]
            {
                new BarcodeCardView
                {
                    Caption = caption,
                    Code = code.Trim(),
                    Image = CreateBarcodeBitmap(code.Trim(), force1D: true),
                },
            };
        }

        private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static bool IsProbablyQrCode(string code) => code.Length > 25 || code.Contains("://") || code.Contains("{") || code.Contains("\n");

        private static BitmapSource? CreateBarcodeBitmap(string code, bool force1D = false)
        {
            try
            {
                bool isQr = !force1D && IsProbablyQrCode(code);
                var writer = new BarcodeWriterPixelData
                {
                    Format = isQr ? BarcodeFormat.QR_CODE : BarcodeFormat.CODE_128,
                    Options = isQr ? 
                        new ZXing.QrCode.QrCodeEncodingOptions { Width = 300, Height = 300, Margin = 1 } :
                        new EncodingOptions { Width = 520, Height = 140, Margin = 10, PureBarcode = false },
                };
                PixelData pixels = writer.Write(code);
                var bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0);
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static IReadOnlyList<ItemSlotConfig> DefaultSlots() => new List<ItemSlotConfig>
        {
            new ItemSlotConfig { slot = 1, name = "Item 1" },
            new ItemSlotConfig { slot = 2, name = "Item 2" },
            new ItemSlotConfig { slot = 3, name = "Item 3" },
        };

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            SourceCombo_SelectionChanged(SourceCombo, null!);
            BeginScan(TxtProductQr);
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            StopCamera();
        }

        private static bool IsEnter(KeyEventArgs e) => e.Key == Key.Enter || e.Key == Key.Return;

        private void BeginScan(TextBox target)
        {
            _activeScanTarget = target;
            target.Focus();
            target.SelectAll();
            TxtScanStatus.Text = "Đã chọn ô · chờ mã quét";
            TxtScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }

        private void CompleteScan(TextBox source, TextBox? next)
        {
            if (string.IsNullOrWhiteSpace(source.Text))
            {
                BeginScan(source);
                return;
            }

            if (next != null)
            {
                BeginScan(next);
                return;
            }

            TxtScanStatus.Text = "Đã nhận đủ mã · sẵn sàng gửi server";
            ConfirmAndClose();
        }

        private void TxtProductQr_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsEnter(e)) return;
            e.Handled = true;
            CompleteScan(TxtProductQr, FindItemTextBox(1));
        }

        private void ItemTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (!IsEnter(e) || sender is not TextBox source || !int.TryParse(source.Tag?.ToString(), out int slot)) return;
            e.Handled = true;
            CompleteScan(source, FindItemTextBox(slot + 1));
        }

        public void SetDefaultProductCode(string code)
        {
            TxtProductQr.Text = code;
            TxtProductQr.Focus();
        }

        public void FocusItem(int slot)
        {
            TextBox? box = FindItemTextBox(slot);
            if (box != null) BeginScan(box);
        }

        private TextBox? FindItemTextBox(int slot)
        {
            for (int index = 0; index < ItemRows.Items.Count; index++)
            {
                if (ItemRows.Items[index] is not QrItemSlotView item || item.Slot != slot) continue;
                if (ItemRows.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container)
                {
                    return FindVisualChild<TextBox>(container);
                }
            }
            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                T? nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private IEnumerable<TextBox> GetItemTextBoxes()
        {
            foreach (object item in ItemRows.Items)
            {
                if (item is not QrItemSlotView slot) continue;
                TextBox? textBox = FindItemTextBox(slot.Slot);
                if (textBox != null) yield return textBox;
            }
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e) => ConfirmAndClose();

        private void ConfirmAndClose()
        {
            if (string.IsNullOrWhiteSpace(ProductQrCode))
            {
                MessageBox.Show(OwnerWindow, "Hãy quét mã sản phẩm trước.", "Thiếu mã sản phẩm", MessageBoxButton.OK, MessageBoxImage.Warning);
                BeginScan(TxtProductQr);
                return;
            }

            List<TextBox> inputs = GetItemTextBoxes().ToList();
            TextBox? empty = inputs.FirstOrDefault(input => string.IsNullOrWhiteSpace(input.Text));
            if (empty != null)
            {
                MessageBox.Show(OwnerWindow, $"Hãy quét đủ {_itemCount} item trước khi gửi server.", "Thiếu item", MessageBoxButton.OK, MessageBoxImage.Warning);
                BeginScan(empty);
                return;
            }

            var duplicateGroups = inputs.GroupBy(x => x.Text.Trim())
                                        .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
                                        .ToList();
            if (duplicateGroups.Any())
            {
                MessageBox.Show(OwnerWindow, $"Bạn đã quét trùng barcode item: {duplicateGroups.First().Key}. Hãy quét lại các item bị trùng.", "Trùng barcode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (inputs.Any(x => string.Equals(x.Text.Trim(), ProductQrCode, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(OwnerWindow, $"Barcode của item không được trùng với barcode của sản phẩm!", "Trùng Barcode", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ScannedItems = inputs.Select(input => new QrItemScan
            {
                SlotIndex = int.TryParse(input.Tag?.ToString(), out int slot) ? slot : 0,
                Code = input.Text.Trim(),
            }).Where(item => item.SlotIndex > 0).ToList();
            ScanCompleted?.Invoke(this, new QrScanCompletedEventArgs(ProductQrCode, ScannedItems));
        }

        private void BtnCreateBarcode_Click(object sender, RoutedEventArgs e)
        {
            string code = new[] { _activeScanTarget?.Text.Trim() ?? "", ProductQrCode }
                .Concat(GetItemTextBoxes().Select(input => input.Text.Trim()))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                MessageBox.Show(OwnerWindow, "Hãy nhập hoặc quét mã trước khi in mã.", "Chưa có mã", MessageBoxButton.OK, MessageBoxImage.Warning);
                BeginScan(TxtProductQr);
                return;
            }

            try
            {
                bool isQr = IsProbablyQrCode(code);
                var writer = new BarcodeWriterPixelData
                {
                    Format = isQr ? BarcodeFormat.QR_CODE : BarcodeFormat.CODE_128,
                    Options = isQr ? 
                        new ZXing.QrCode.QrCodeEncodingOptions { Width = 300, Height = 300, Margin = 1 } :
                        new EncodingOptions { Width = 520, Height = 150, Margin = 12, PureBarcode = false },
                };
                PixelData pixels = writer.Write(code);
                var bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0);

                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "barcodes");
                Directory.CreateDirectory(directory);
                string safeCode = string.Concat(code.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
                string path = Path.Combine(directory, $"barcode_{safeCode}_{DateTime.Now:yyyyMMddHHmmss}.png");
                using var stream = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                TxtScanStatus.Text = $"Đã tạo barcode: {Path.GetFileName(path)}";
                
                if (sender != null)
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerWindow, $"Không thể tạo barcode: {ex.Message}", "Tạo barcode", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            TxtProductQr.Clear();
            foreach (TextBox input in GetItemTextBoxes()) input.Clear();
            BeginScan(TxtProductQr);
        }

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IpPanel is null) return;
            string source = (SourceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DroidCam";
            IpPanel.Visibility = source == "DroidCam" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
#if WIN_X86
            MessageBox.Show(OwnerWindow, "Camera scan cần bản x64 của app.", "Camera", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
#else
            if (_cameraRunning) return;
            string source = (SourceCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DroidCam";
            try
            {
                _capture = source switch
                {
                    "USB Camera 0" => new VideoCapture(0),
                    "USB Camera 1" => new VideoCapture(1),
                    _ => new VideoCapture($"http://{DeviceIPText.Text.Trim()}:{DevicePortText.Text.Trim()}{NormalizePath(DevicePathText.Text)}")
                };
                if (_capture is null || !_capture.IsOpened())
                {
                    MessageBox.Show(OwnerWindow, "Không mở được camera. Kiểm tra IP/port/path và điện thoại.", "Camera", MessageBoxButton.OK, MessageBoxImage.Error);
                    _capture?.Dispose();
                    _capture = null;
                    return;
                }

                _cameraRunning = true;
                _cameraThread = new Thread(CameraLoop) { IsBackground = true };
                _cameraThread.Start();
                BtnCameraConnect.IsEnabled = false;
                BtnCameraDisconnect.IsEnabled = true;
                CameraStatusLabel.Text = "Camera online · đang scan";
                CameraStatusLabel.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerWindow, $"Lỗi camera: {ex.Message}", "Camera", MessageBoxButton.OK, MessageBoxImage.Error);
            }
#endif
        }

        private static string NormalizePath(string path)
        {
            path = path.Trim();
            return string.IsNullOrWhiteSpace(path) ? "/video" : (path.StartsWith("/") ? path : "/" + path);
        }

#if !WIN_X86
        private void CameraLoop()
        {
            while (_cameraRunning)
            {
                if (_capture is null || !_capture.IsOpened()) break;
                using var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    var bitmap = WriteableBitmapConverter.ToWriteableBitmap(frame);
                    bitmap.Freeze();
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        CameraFeedImage.Source = bitmap;
                        NoCameraText.Visibility = Visibility.Collapsed;
                    });
                }
                catch { }

                if (++_frameNumber % 3 == 0)
                {
                    string? decoded = TryDecode(frame);
                    if (!string.IsNullOrWhiteSpace(decoded) &&
                        (!string.Equals(decoded, _lastDecodedCode, StringComparison.Ordinal) || DateTime.UtcNow - _lastDecodedAt > TimeSpan.FromSeconds(1.5)))
                    {
                        _lastDecodedCode = decoded;
                        _lastDecodedAt = DateTime.UtcNow;
                        string code = decoded;
                        _ = Dispatcher.BeginInvoke(() => ApplyCameraCode(code));
                    }
                }
                Thread.Sleep(25);
            }
        }

        private static string? TryDecode(Mat frame)
        {
            try
            {
                using var rgb = new Mat();
                Cv2.CvtColor(frame, rgb, ColorConversionCodes.BGR2RGB);
                int length = checked((int)(rgb.Total() * rgb.ElemSize()));
                byte[] bytes = new byte[length];
                Marshal.Copy(rgb.Data, bytes, 0, length);
                var source = new RGBLuminanceSource(bytes, rgb.Width, rgb.Height, RGBLuminanceSource.BitmapFormat.RGB24);
                return new BarcodeReaderGeneric().Decode(source)?.Text;
            }
            catch
            {
                return null;
            }
        }
#endif

        private void ApplyCameraCode(string code)
        {
            if (!_cameraRunning || string.IsNullOrWhiteSpace(code)) return;
            TextBox target = _activeScanTarget ?? TxtProductQr;
            target.Text = code.Trim();
            TxtScanStatus.Text = $"Camera đã nhận: {code.Trim()}";
            TxtScanStatus.Foreground = Brushes.LightGreen;

            TextBox? next = ReferenceEquals(target, TxtProductQr)
                ? FindItemTextBox(1)
                : int.TryParse(target.Tag?.ToString(), out int slot) ? FindItemTextBox(slot + 1) : null;
            CompleteScan(target, next);
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e) => StopCamera();

        private void StopCamera()
        {
            _cameraRunning = false;
#if !WIN_X86
            _cameraThread?.Join(1000);
            _cameraThread = null;
            _capture?.Dispose();
            _capture = null;
#endif
            if (!Dispatcher.CheckAccess()) return;
            BtnCameraConnect.IsEnabled = true;
            BtnCameraDisconnect.IsEnabled = false;
            CameraFeedImage.Source = null;
            NoCameraText.Visibility = Visibility.Visible;
            CameraStatusLabel.Text = "Offline";
            CameraStatusLabel.Foreground = Brushes.OrangeRed;
        }

        private void ToggleHistorySection_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = ToggleHistorySection.IsChecked ?? false;
            HistoryList.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        }

        private BarcodeCardView? _updatingServerItem;
        
        private void UpdateServerItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Tag is BarcodeCardView card)
            {
                if (string.IsNullOrEmpty(card.ItemName)) return; // Only allow updating items, not products
                _updatingServerItem = card;
                TxtOldItemCode.Text = card.Code;
                TxtNewItemCode.Text = "";
                UpdateItemOverlay.Visibility = Visibility.Visible;
                TxtNewItemCode.Focus();
            }
        }

        private void UpdateItem_Click(object sender, RoutedEventArgs e)
        {
            // Optional: local item update before saving
        }

        private void BtnCancelUpdateItem_Click(object sender, RoutedEventArgs e)
        {
            UpdateItemOverlay.Visibility = Visibility.Collapsed;
            _updatingServerItem = null;
        }

        private async void BtnConfirmUpdateItem_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingServerItem == null) return;
            string newCode = TxtNewItemCode.Text.Trim();
            if (string.IsNullOrWhiteSpace(newCode))
            {
                MessageBox.Show(OwnerWindow, "Vui lòng nhập mã barcode mới.", "Thiếu mã mới", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ServerEngine.CurrentProduct == null || string.IsNullOrWhiteSpace(ServerEngine.CurrentProduct.Id))
            {
                MessageBox.Show(OwnerWindow, "Chưa có sản phẩm hiện tại.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BtnConfirmUpdateItem.IsEnabled = false;
            try
            {
                ProductInfo? updatedProduct = await ServerEngine.UpdateProductItemStatusAsync(ServerEngine.CurrentProduct.Id, _updatingServerItem.Code, "inactive");
                if (updatedProduct == null)
                {
                    MessageBox.Show(OwnerWindow, ServerEngine.LastError ?? "Không thể cập nhật item cũ thành inactive.", "Lỗi server", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Add the new item
                int slotIndex = int.TryParse(new string(_updatingServerItem.Caption.Where(char.IsDigit).ToArray()), out int s) ? s : 1;
                ProductInfo? finalProduct = await ServerEngine.LinkProductItemAsync(updatedProduct, newCode, _updatingServerItem.ItemName ?? "Item", slotIndex);
                if (finalProduct == null)
                {
                    MessageBox.Show(OwnerWindow, ServerEngine.LastError ?? "Không thể thêm item mới.", "Lỗi server", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show(OwnerWindow, "Cập nhật barcode item thành công!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateItemOverlay.Visibility = Visibility.Collapsed;
                ShowProductDetails(finalProduct, finalProduct.SerialNumber);
            }
            finally
            {
                BtnConfirmUpdateItem.IsEnabled = true;
            }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<HistoryItemView> _historyItems = new();

        public void AddToHistoryAndReset(ProductInfo product, IReadOnlyList<QrItemScan> scannedItems, IReadOnlyList<ItemSlotConfig> slots)
        {
            // Mặc định in ra barcode sản phẩm
            _activeScanTarget = TxtProductQr;
            BtnCreateBarcode_Click(null, null);

            _scanCount++;
            TxtScanCounter.Text = "SCAN";
            var historyItem = new HistoryItemView
            {
                ProductTitle = $"SẢN PHẨM / BARCODE: {ProductQrCode}",
                ScannedItems = scannedItems.Select(s => new HistoryScannedItemView
                {
                    Name = $"ITEM {s.SlotIndex} · {slots.FirstOrDefault(c => c.slot == s.SlotIndex)?.name ?? "Unknown"}",
                    Code = s.Code
                }).ToList()
            };
            
            _historyItems.Insert(0, historyItem); // Add to top of history
            
            if (HistoryList.ItemsSource == null)
            {
                HistoryList.ItemsSource = _historyItems;
            }

            // Reset UI
            TxtProductQr.Clear();
            foreach (TextBox input in GetItemTextBoxes()) input.Clear();
            BeginScan(TxtProductQr);
        }
    }
}
