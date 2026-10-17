using System.Windows;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private const int StandardSampleRate = 44100;
    private DateTime _lastSampleRateWarningPollUtc;
    private int? _lastObservedCaptureRate;
    private string _sampleRateWarningText = "";
    private string _sampleRateSyncFailure = "";

    private void EnsureSelectedStandardSampleRates(bool beforeMeasurement = false)
    {
        if (GetSelectedPlaybackSampleRate() != StandardSampleRate) { _sampleRateSyncFailure = ""; return; }
        if (_audioEngine == null
            || _activeMeasurementTask != null || _routingScopeWindow != null
            || (!beforeMeasurement && IsModelTransitionBusy)
            || ((Application.Current?.MainWindow as MainWindow)?.IsStandardMeasurementBusy ?? false)) return;
        _sampleRateSyncFailure = "";
        var devices = new[] { SelectedPlaybackDevice, SelectedRecordingDevice }.Where(d => d != null).Cast<MMDevice>().ToArray();
        var pending = devices.Where(d => ReadEndpointSampleRate(d) != StandardSampleRate).ToArray();
        if (pending.Length == 0) return;
        StopRoutingHeadroomMonitor();
        _audioEngine.StopAllAudio();
        _audioEngine.CaptureSession.Reset();
        foreach (var device in pending)
        {
            bool success = AudioSampleRateSetup.EnsureStandard(device, out string message);
            if (message.Length > 0) AppendLog(success ? "Đồng bộ 44.1 kHz" : "Cảnh báo 44.1 kHz", message);
            if (!success) _sampleRateSyncFailure += message + " ";
        }
        _lastObservedCaptureRate = null;
        UpdateSampleRateWarning();
    }

    private static int? ReadEndpointSampleRate(MMDevice? device)
    {
        if (device == null) return null;
        try { return FastTrackDeviceSetup.ReadMixSampleRate(device.ID); }
        catch { return null; }
    }

    private static string BuildSampleRateWarning(int appRate, int? outputRate, int? inputRate, int? actualCaptureRate)
    {
        var mismatches = new List<string>();
        static string Rate(int rate) => (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " kHz";
        if (appRate != StandardSampleRate) mismatches.Add("App " + Rate(appRate));
        if (outputRate is int output && output != StandardSampleRate) mismatches.Add("ngõ phát Windows " + Rate(output));
        if (inputRate is int input && input != StandardSampleRate) mismatches.Add("ngõ thu Windows " + Rate(input));
        if (actualCaptureRate is int actual && actual != StandardSampleRate) mismatches.Add("lượt vừa thu " + Rate(actual));
        return mismatches.Count == 0 ? "" : "CẢNH BÁO SAMPLE RATE — chuẩn đo 44.1 kHz. "
            + string.Join("; ", mismatches) + ". App thử tự đồng bộ trước đo khi chọn 44.1 kHz và thiết bị rảnh. Nếu chưa thành công, kiểm tra Default Format của hai ngõ trong Windows Sound; đo lại sau khi đồng bộ.";
    }

    private void UpdateSampleRateWarning(int? actualCaptureRate = null)
    {
        if (actualCaptureRate.HasValue) _lastObservedCaptureRate = actualCaptureRate;
        _sampleRateWarningText = BuildSampleRateWarning(GetSelectedPlaybackSampleRate(),
            ReadEndpointSampleRate(SelectedPlaybackDevice), ReadEndpointSampleRate(SelectedRecordingDevice), _lastObservedCaptureRate);
        if (_sampleRateSyncFailure.Length > 0) _sampleRateWarningText += " " + _sampleRateSyncFailure;
        if (TxtSampleRateWarning == null || BorderSampleRateWarning == null) return;
        TxtSampleRateWarning.Text = _sampleRateWarningText;
        BorderSampleRateWarning.Visibility = _sampleRateWarningText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
