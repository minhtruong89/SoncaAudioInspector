using System.Windows;

namespace SoncaAudioInspector;

/// <summary>v1.5.6 (owner 2026-11-19): what the operator sees around a C360 Auto Test.
/// - IO008 shows the chirp Rè graph (the uploaded QA image) and the Rè verdict, never the mic pre-check sweep;
/// - after BLE switched every input, one popup lists the speaker inputs and sound-card ports, closes by itself and the run continues;
/// - errors stay on screen until read (an auto-closing warning hid them after 4 s);
/// - at the end one popup says whether the result reached the server (FACTORY FLOOR QA &amp; APP TRACKER + QA/QC process).</summary>
public partial class AudioRouting
{
    /// <summary>Test hook: offline checks capture (message, title, type) instead of opening dialogs.</summary>
    internal Action<string, string, ModernMessageBox.MessageBoxType>? C360PopupPresenter { get; set; }

    public static (PinkRubBuzzRun? Pink, ChirpRubBuzzRun? Chirp, ConfiguredTestResult? Result) RubBuzzOf(AutoTestCaseItem row)
        => (row.AdditionalResults.Select(r => r.PinkRubBuzz).FirstOrDefault(p => p != null),
            row.AdditionalResults.Select(r => r.ChirpRubBuzz).FirstOrDefault(c => c != null),
            row.AdditionalResults.FirstOrDefault());

    /// <summary>IO008 verdict for the status column, the result view and popups: Pink % and Chirp dB against their limits,
    /// or the reason the Rè test could not run.</summary>
    public static string RubBuzzVerdictLine(AutoTestCaseItem row)
    {
        var (pink, chirp, result) = RubBuzzOf(row);
        var parts = new List<string>();
        if (pink != null)
            parts.Add(pink.Metrics.IsValid && pink.Verdict is "PASS" or "FAIL"
                ? $"Pink {pink.Verdict}: rè lặp lại {pink.Metrics.RepeatCoherencePct:0.0}% (PASS < {(pink.Limits ?? new()).AbsoluteFailPct:0.#}%)"
                : $"Pink {pink.Verdict}: {pink.Message}");
        if (chirp != null)
            parts.Add(chirp.Metrics.IsValid && chirp.Verdict is "PASS" or "FAIL"
                ? $"Chirp {chirp.Verdict}: đỉnh rè {chirp.Metrics.PeakBumpDb:0.0} dB tại {chirp.Metrics.PeakBumpHz:0} Hz (PASS < {(chirp.Limits ?? new()).AbsoluteFailDb:0.#} dB)"
                : $"Chirp {chirp.Verdict}: {chirp.Message}");
        if (parts.Count == 0 && result != null) parts.Add($"Rè {result.Status}: {result.Reason}");
        return parts.Count == 0 ? "Rè: chưa đo (bước kiểm mic đo chưa đạt)." : string.Join(" · ", parts);
    }

    private void ShowC360RubBuzzChirpPlot(AutoTestCaseItem row)
    {
        _frequencyChartRefreshTimer.Stop();
        ShowInlineImpulse(null);
        HideFrequencyHover();
        TxtFrequencyChartHeading.Text = row.Id + " · RÈ (RUB & BUZZ) · ĐỒ THỊ CHIRP 20–2000 Hz";
        var (_, chirp, result) = RubBuzzOf(row);
        if (chirp != null && chirp.Metrics.FrequenciesHz.Length > 0)
            RubBuzzQaGraph.Draw(PlotFreqResponse.Plot, chirp, $"{row.Id} · {row.Name} · Chirp rè");
        else
        {
            PlotFreqResponse.Plot.Clear();
            PlotFreqResponse.Plot.Title($"{row.Id} · Rè: chưa có đồ thị chirp — {result?.Reason ?? row.ThdStatus}");
        }
        PlotFreqResponse.Refresh();
    }

    private void ShowC360RubBuzzResult(AutoTestCaseItem row)
    {
        ShowC360RubBuzzChirpPlot(row);
        ClearThdPlots();
        TxtQualityChartHeading.Text = "RÈ (RUB & BUZZ)";
        PanelThdInspection.Visibility = Visibility.Collapsed;
        TxtThdStatus.Text = "";
        TxtThdVerdictBadge.Text = "RÈ · " + row.StatusDisplay;
        TxtThdVerdictBadge.Foreground = row.StatusBrush;
        var (pink, chirp, _) = RubBuzzOf(row);
        TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(row, _activeInOutConfig) + "\n" + RubBuzzVerdictLine(row)
            + (pink != null ? "\n\nPink — " + pink.Summary : "") + (chirp != null ? "\n\nChirp — " + chirp.Summary : "")
            + "\n\nKiểm mic đo trước bài Rè: " + row.DistortionText.Split('\n')[0];
        UpdateMeasurementDetails();
    }

    private void C360Popup(string message, string title, ModernMessageBox.MessageBoxType type, bool persistent = false)
    {
        if (C360PopupPresenter is { } capture) { capture(message, title, type); return; }
        if (SignalWarningPresenter is { } present) { present(message, title); return; }
        var owner = Window.GetWindow(this);
        if (persistent && type == ModernMessageBox.MessageBoxType.Warning) ModernMessageBox.ShowPersistentWarning(owner, message, title);
        else ModernMessageBox.Show(owner, message, title, type);
    }

    /// <summary>Text of the "ngõ đo" popup after BLE switched and read back every input the plan needs.</summary>
    public static string InputReport(string speaker, string? rssi, IEnumerable<string> available, IEnumerable<string> confirmed,
        IEnumerable<string> missing, string soundCards, IEnumerable<string> tests)
    {
        var lack = missing.ToArray();
        return $"{speaker}{(string.IsNullOrEmpty(rssi) ? "" : " · " + rssi)}\n"
            + $"Ngõ loa báo hỗ trợ: {string.Join(", ", available)}\n"
            + $"Đã chuyển và đọc lại: {string.Join(", ", confirmed)}\n"
            + (lack.Length > 0 ? $"CHƯA XÁC NHẬN: {string.Join(", ", lack)} — các bài dùng ngõ này sẽ không đo được.\n" : "")
            + (soundCards.Length > 0 ? soundCards + "\n" : "")
            + $"Sẽ đo: {string.Join(" · ", tests)}"
            + (lack.Length > 0 ? "" : "\n\nTự đóng và bắt đầu đo sau vài giây.");
    }

    /// <summary>End-of-run server outcome (null = nothing to say, e.g. a single test or LINE CHUẨN search).</summary>
    public static (string Message, ModernMessageBox.MessageBoxType Type)? UploadOutcome(bool productSuite, bool findReference, bool singleTest,
        bool valid, bool sendEnabled, bool uploaded, ServerEngine.AudioQaUploadReceipt? receipt, string? pendingReason, string? role,
        IEnumerable<string> notConcluded)
    {
        if (!productSuite || findReference || singleTest) return null;
        if (!sendEnabled) return ("Không gửi server: ô \"Gửi kết quả lên server\" đang tắt.", ModernMessageBox.MessageBoxType.Warning);
        if (!valid)
            return ("Chưa gửi server: còn bài chưa kết luận PASS/FAIL — " + string.Join(", ", notConcluded)
                + ". Sửa nguyên nhân rồi bấm TIẾP TỤC ĐO TỰ ĐỘNG; các bài đã xong được giữ lại.", ModernMessageBox.MessageBoxType.Warning);
        if (uploaded && receipt != null)
            return ($"Đã gửi lên server ({receipt.DataSet}): {receipt.ImageUrls.Length} ảnh QA · log {receipt.LogId}.\n"
                + "Xem ở FACTORY FLOOR QA & APP TRACKER (/report) và Quy trình QA/QC của sản phẩm."
                + (role is { Length: > 0 } r && !string.Equals(r, "STAFF", StringComparison.OrdinalIgnoreCase)
                    ? $"\nLưu ý: tài khoản đang đăng nhập là {r}; Quy trình QA/QC chỉ hiện kết quả của tài khoản STAFF (vẫn có trong /report)." : ""),
                ModernMessageBox.MessageBoxType.Info);
        return ("Chưa gửi được kết quả lên server: " + (pendingReason ?? "không rõ nguyên nhân")
            + "\nKết quả đã lưu tại máy; bấm GỬI LẠI KẾT QUẢ CHỜ khi có mạng / đăng nhập lại.", ModernMessageBox.MessageBoxType.Error);
    }
}
