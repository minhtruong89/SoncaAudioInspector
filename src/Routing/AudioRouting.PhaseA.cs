using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    public static DevicesConfig EnabledModelDeviceRequirements(InOutConfig config) => new()
    {
        Input = config.Devices.Input.Where(p => config.Tests.Any(t => t.IsEnabled && t.PlaybackOut == p.Key)).ToDictionary(p => p.Key, p => p.Value),
        Output = config.Devices.Output.Where(p => config.Tests.Any(t => t.IsEnabled && t.RecordingIn == p.Key)).ToDictionary(p => p.Key, p => p.Value)
    };
    private string? _deviceSerial;
    private MeasurementIdentity? _measurementIdentity;
    public void ClearForConfigurationChange()
    {
        // Keep already persisted official QA markers available for later retry. This operation only clears the display/RAM.
        _pendingQaCompletionSession = null; _autoTestSessionFolder = null;
        CancelAndDiscardCurrentMeasurement();
    }
    private void FreezeMeasurementIdentity() => _measurementIdentity = (Application.Current.MainWindow as MainWindow)?.MeasurementContext.Snapshot();
    private string ResolveC360RunSerial(string model, bool productSuite, bool findReference, string? resumeSerial)
    {
        if (productSuite || ResultPersistencePolicy.IsTrial)
        {
            var main = Application.Current.MainWindow as MainWindow;
            string serial = resumeSerial ?? main?.ResolveMeasurementSerial(model, _deviceSerial) ?? GenerateAutoTestId(model);
            SetCurrentProduct(null);
            main?.SetMeasurementSerialText(serial);
            TxtProductSerial.Text = serial;
            FreezeMeasurementIdentity();
            return serial;
        }
        return resumeSerial ?? (findReference ? "REFERENCE_" : "MANUAL_") + DateTime.Now.ToString("HHmmssfff");
    }
    public void SetDeviceSerial(string? serial) => _deviceSerial = string.IsNullOrWhiteSpace(serial) ? null : ProductMeasurementContext.ValidateSerial(serial);
    internal void RefreshSerialGate() => UpdateAutoTestStartButton();
    private bool _confirmingAutoTestQa;
    private bool EnsureSerialReady()
    {
        if (BtnStartAutoTest != null)
            BtnStartAutoTest.ToolTip = "Bấm để xem kết quả QA rồi bắt đầu đo.";
        return true;
    }
    private async Task<bool> ConfirmAutoTestQaBeforeStartAsync()
    {
        if (_confirmingAutoTestQa) return false;
        if (Application.Current.MainWindow is not MainWindow main) return true;
        bool resume = _c360AutoTestResumeSession is { } saved && GetC360AutoTestResumeSession(saved.Model) != null
            || _autoTestResumeSession is { } generic && CanResumeAutoTestSession(generic.Model);
        _confirmingAutoTestQa = true;
        try
        {
            if (!await main.ConfirmAutoTestQaAsync(resume))
            {
                UpdateAutoTestStartButton();
                return false;
            }
            FreezeMeasurementIdentity();
            if (main.MeasurementContext.OperatorNote is { Length: > 0 } note)
                AppendLog("Ghi chú", note);
            return true;
        }
        finally
        {
            _confirmingAutoTestQa = false;
        }
    }
    private bool ResumeIdentityMatches(string serial) => Application.Current.MainWindow is not MainWindow main
        || string.Equals(main.TxtSerialNumber.Text.Trim(), serial, StringComparison.Ordinal);
    private void CompleteProductVerdict(bool passed)
    {
        var context = _measurementIdentity ?? (Application.Current.MainWindow as MainWindow)?.MeasurementContext.Snapshot();
        var comparison = QaComparison.Compare(passed ? "PASS" : "FAIL", context?.QaVerdict ?? "NONE");
        TxtQaComparison.Text = comparison.Text;
        TxtQaComparison.Foreground = (Brush)new BrushConverter().ConvertFromString(comparison.Color)!;
        TxtTrialResult.Text = ResultPersistencePolicy.IsTrial ? ResultPersistencePolicy.TrialLabel : "";
        string detail = $"Serial {context?.Serial} · {context?.Source} · QA {context?.QaVerdict ?? "NONE"} · {comparison.Text} · xác nhận {context?.QaConfirmedAt:O} · {context?.QaConfirmedBy}";
        AppendLog("So sánh QA", detail);
        foreach (var row in _autoTestCases)
            row.MeasurementValues = row.MeasurementValues.Concat(new[] { new MeasurementValue("So sánh QA", detail) }).ToArray();
    }
    internal Func<bool>? TrialReferenceConfirmation { get; set; }
    private bool ConfirmTrialReference() => !ResultPersistencePolicy.IsTrial
        || (TrialReferenceConfirmation?.Invoke() ?? (MessageBox.Show(Window.GetWindow(this), "Line chuẩn sẽ lưu theo cấu hình thử", "Tìm line chuẩn", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK));
}
