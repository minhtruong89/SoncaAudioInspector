using System.Windows;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    public ProductMeasurementContext MeasurementContext { get; } = new();
    private bool _settingMeasurementSerial, _acceptingSerial;
    private int _serialVersion;
    private long _lastSerialKey;
    private int _serialKeyBurst;
    private void TrackSerialKey()
    {
        long now = Environment.TickCount64;
        _serialKeyBurst = now - _lastSerialKey <= 100 ? _serialKeyBurst + 1 : 1;
        _lastSerialKey = now;
    }
    private SerialSource KeyboardSerialSource()
    {
        bool scan = _serialKeyBurst >= 3 && Environment.TickCount64 - _lastSerialKey <= 100;
        _serialKeyBurst = 0; return scan ? SerialSource.Scanned : SerialSource.Manual;
    }
    internal Func<string, string?, Task<ProductInfo?>> SerialLookup { get; set; } = ServerEngine.GetProductBySerialAsync;
    internal Func<PreviousQaWindow, bool?>? QaConfirmationPresenter { get; set; }
    internal Func<ModelConfigEditorWindow, bool?>? ModelEditorPresenter { get; set; }
    internal Action<InOutConfig>? ApplySessionModelOverride { get; set; }
    private void ApplySessionModel(InOutConfig config)
    {
        if (ApplySessionModelOverride != null) ApplySessionModelOverride(config);
        else
        {
            CloseDeviceConnectionWindow();
            bool ready = _audioRoutingView.ApplyModelDevices(config, out string? missing);
            StartSelectedModelDeviceMonitor(SessionModelConfigurations.Current.SelectedModel, config);
            if (!ready && !_modelEditorOpen) ShowDeviceConnectionWindow(SessionModelConfigurations.Current.SelectedModel, config, missing);
        }
    }
    private sealed record SerialLookupSnapshot(string Serial, string Model, bool Succeeded, ProductInfo? Product);
    private SerialLookupSnapshot? _cachedSerialLookup;
    internal static string? ServerQaSuggestion(ProductInfo? product)
        => product?.QaStatus is "PASS" or "FAIL" ? product.QaStatus : product?.QcStatus is "PASS" or "FAIL" ? product.QcStatus : null;
    internal static string? ServerQaActor(ProductInfo? product)
        => product?.QaStatus is "PASS" or "FAIL" ? product.QaBy ?? product.QcBy : product?.QcStatus is "PASS" or "FAIL" ? product.QcBy ?? product.QaBy : null;
    private void BtnModelConfig_Click(object sender, RoutedEventArgs e) => OpenModelEditor(false);
    private void InvalidateEnteredSerial()
    {
        if (_settingMeasurementSerial || MeasurementContext == null) return;
        _serialVersion++; _cachedSerialLookup = null; MeasurementContext.Clear();
        if (TxtSerialStatus != null) TxtSerialStatus.Text = string.IsNullOrWhiteSpace(TxtSerialNumber?.Text) ? "" : "Nhấn Enter hoặc bấm Bắt đầu đo để tra QA";
        _audioRoutingView?.RefreshSerialGate();
    }
    public bool IsEnteredSerialReady => string.IsNullOrWhiteSpace(TxtSerialNumber.Text)
        || MeasurementContext.Serial == TxtSerialNumber.Text.Trim() && MeasurementContext.Model == ComboModels.SelectedItem?.ToString();
    private Task AcceptEnteredSerialAsync(SerialSource source) => AcceptEnteredSerialCoreAsync(source);
    internal async Task<bool> AcceptEnteredSerialCoreAsync(SerialSource source)
    {
        if (_acceptingSerial || IsAudioRoutingBusy) return false;
        string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
        try
        {
            if (model.Length == 0) throw new ArgumentException("Chọn model trước khi xác nhận serial.");
            string input = TxtSerialNumber.Text;
            if (BomCsvParser.TryParseProductQr(input.Trim(), out BomProductQrValue? qr) && qr != null)
            {
                if (!string.Equals(BomCsvParser.NormalizeModelKey(model), qr.ModelKey, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("QR không khớp model đang chọn.");
                input = qr.ProductSerial; source = SerialSource.Scanned;
            }
            string serial = ProductMeasurementContext.ValidateSerial(input);
            MeasurementContext.Receive(serial, model, source); SetMeasurementSerialText(serial);
            int version = ++_serialVersion; _acceptingSerial = true;
            await LookupEnteredSerialQaAsync(serial, model, version);
            _audioRoutingView?.RefreshSerialGate();
            return version == _serialVersion && model == ComboModels.SelectedItem?.ToString();
        }
        catch (ArgumentException ex) { TxtSerialStatus.Text = ex.Message; return false; }
        finally { _acceptingSerial = false; }
    }
    private async Task LookupEnteredSerialQaAsync(string serial, string model, int version)
    {
        TxtSerialStatus.Text = "Offline · chưa tra được QA";
        if (!ServerEngine.IsAuthenticated)
        {
            if (version == _serialVersion) _cachedSerialLookup = new(serial, model, false, null);
            return;
        }
        TxtSerialStatus.Text = "Đang tra serial…";
        try
        {
            ProductInfo? product = await SerialLookup(serial, model).WaitAsync(TimeSpan.FromSeconds(3));
            if (version != _serialVersion) return;
            _cachedSerialLookup = new(serial, model, true, product);
            string? suggestion = ServerQaSuggestion(product);
            TxtSerialStatus.Text = product == null
                ? "Serial mới · chưa có QA"
                : suggestion != null ? $"Đã có trên server · QA {suggestion}" : "Đã có trên server · chưa có QA";
        }
        catch
        {
            if (version == _serialVersion)
            {
                _cachedSerialLookup = new(serial, model, false, null);
                TxtSerialStatus.Text = "Offline · chưa tra được QA";
            }
        }
    }
    internal async Task<bool> ConfirmAutoTestQaAsync(bool isResume)
    {
        string model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(TxtSerialNumber.Text))
        {
            if (!IsEnteredSerialReady && !await AcceptEnteredSerialCoreAsync(KeyboardSerialSource()))
                return false;
        }
        else if (!isResume)
        {
            _cachedSerialLookup = null;
            MeasurementContext.Clear();
        }
        if (model.Length == 0) model = ComboModels.SelectedItem?.ToString()?.Trim() ?? "C360";
        string serial = !string.IsNullOrWhiteSpace(TxtSerialNumber.Text)
            && MeasurementContext.Source is SerialSource.Manual or SerialSource.Scanned
            ? MeasurementContext.Serial : "";
        if (serial.Length > 0 && (_cachedSerialLookup == null || _cachedSerialLookup.Serial != serial
            || _cachedSerialLookup.Model != model || !_cachedSerialLookup.Succeeded))
        {
            await LookupEnteredSerialQaAsync(serial, model, _serialVersion);
        }
        bool lookupFailed = serial.Length > 0 && (_cachedSerialLookup == null || !_cachedSerialLookup.Succeeded);
        ProductInfo? product = !lookupFailed ? _cachedSerialLookup?.Product : null;
        string? suggestion = ServerQaSuggestion(product);
        string? qaBy = ServerQaActor(product);
        string? initialVerdict = isResume && MeasurementContext.QaConfirmedAt != null ? MeasurementContext.QaVerdict : null;
        string? initialNote = isResume ? MeasurementContext.OperatorNote : null;
        var popup = new PreviousQaWindow(serial, model, suggestion, qaBy, lookupFailed, isResume, initialVerdict, initialNote)
        {
            Owner = IsVisible ? this : null
        };
        bool? confirmed = QaConfirmationPresenter != null ? QaConfirmationPresenter(popup) : popup.ShowDialog();
        if (confirmed != true) return false;
        MeasurementContext.ConfirmQa(popup.Verdict, ServerEngine.UserName ?? ServerEngine.StaffID ?? Environment.UserName, popup.OperatorNote, model);
        return true;
    }
    internal void SetMeasurementSerialText(string serial)
    {
        _settingMeasurementSerial = true;
        try { TxtSerialNumber.Text = serial; } finally { _settingMeasurementSerial = false; }
    }
    internal string ResolveMeasurementSerial(string model, string? deviceSerial = null)
    {
        string serial = MeasurementContext.Resolve(model, deviceSerial, () => AudioRouting.GenerateAutoTestId(model));
        SetMeasurementSerialText(serial); return serial;
    }
    internal void CompleteMeasurementIdentity()
    {
        _cachedSerialLookup = null; MeasurementContext.Clear(); SetMeasurementSerialText(""); TxtSerialStatus.Text = ""; _serialVersion++;
    }
    internal void SetSerialInputEnabled(bool enabled) => TxtSerialNumber.IsEnabled = BtnModelConfig.IsEnabled = BtnDiscardTrialConfig.IsEnabled = enabled;
    private void ApplyTemporaryModel(ModelConfig candidate, string? originalName)
    {
        if (originalName != null && !string.Equals(candidate.model, originalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Lưu tạm giữ tên model hiện tại.");
        SessionModelConfigurations.Current.Apply(candidate);
        if (!ComboModels.Items.Cast<object>().Any(m => m.ToString() == candidate.model)) ComboModels.Items.Add(candidate.model);
        _audioRoutingView.ClearForConfigurationChange();
        SessionModelConfigurations.Current.SelectedModel = candidate.model;
        ComboModels.SelectedItem = candidate.model;
        ApplySessionModel(SessionModelConfigurations.Current.Resolve(candidate).testItems.InOut);
        RefreshTrialBanner();
    }
    private void RefreshTrialBanner()
    {
        if (TrialConfigBanner == null) return;
        string model = SessionModelConfigurations.Current.SelectedModel;
        var saved = SessionModelConfigurations.Current.SavedAt(model);
        TrialConfigBanner.Visibility = saved == null ? Visibility.Collapsed : Visibility.Visible;
        TxtTrialConfig.Text = SessionModelConfigurations.Current.TrialBanner(model);
    }
    private void BtnDiscardTrialConfig_Click(object sender, RoutedEventArgs e)
    {
        if (IsAudioRoutingBusy || IsStandardMeasurementBusy) return;
        string model = SessionModelConfigurations.Current.SelectedModel;
        _audioRoutingView.ClearForConfigurationChange();
        SessionModelConfigurations.Current.Discard(model);
        var official = _checkingConfig.models.FirstOrDefault(m => string.Equals(m.model, model, StringComparison.OrdinalIgnoreCase));
        if (official != null) ApplySessionModel(official.testItems.InOut);
        else { ComboModels.Items.Remove(model); _audioRoutingView.ClearModelSelection(); }
        RefreshTrialBanner();
    }
}
