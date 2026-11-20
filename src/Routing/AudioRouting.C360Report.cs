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
                ? $"Pink {pink.Verdict}: rè lặp lại {PinkRubBuzzMetrics.DisplayCoherence(pink.Metrics.RepeatCoherencePct)} (PASS < {(pink.Limits ?? new()).AbsoluteFailPct:0.#}%)"
                : $"Pink {pink.Verdict}: {pink.Message}");
        if (chirp != null)
            parts.Add(chirp.Metrics.HasMeasuredPeak && chirp.Verdict is "PASS" or "FAIL"
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
        var (_, chirp, _) = RubBuzzOf(row);
        RubBuzzQaGraph.Draw(PlotFreqResponse.Plot, chirp, $"{row.Id} · {row.Name} · Chirp rè",
            route: AudioQaRouteDetails.Route(row, _activeInOutConfig));
        PlotFreqResponse.Refresh();
    }

    private void ShowC360RubBuzzResult(AutoTestCaseItem row)
    {
        ShowC360RubBuzzChirpPlot(row);
        ClearThdPlots();
        TxtQualityChartHeading.Text = "RÈ (RUB & BUZZ)";
        PanelThdInspection.Visibility = Visibility.Collapsed;
        TxtThdStatus.Text = "";
        TxtThdVerdictBadge.Text = row.Config.IsOptional ? row.StatusDisplay : "RÈ · " + row.StatusDisplay;
        TxtThdVerdictBadge.Foreground = row.StatusBrush;
        BorderThdVerdictBadge.BorderBrush = row.StatusBrush;
        var (pink, chirp, _) = RubBuzzOf(row);
        TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(row, _activeInOutConfig) + "\n" + RubBuzzVerdictLine(row)
            + (pink != null ? "\n\nPink — " + pink.Summary : "") + (chirp != null ? "\n\nChirp — " + chirp.Summary : "")
            + "\n\nKiểm mic đo trước bài Rè: " + row.DistortionText.Split('\n')[0];
        UpdateMeasurementDetails();
    }

    /// <summary>Owner 2026-11-19 (A2): shared plot for IO009 gain-before-feedback (on-screen review and FRA-IO009.png).
    /// Optional tests always render gray title with measurement numbers only — never PASS/FAIL/ĐẠT/KHÔNG ĐẠT.</summary>
    public static void DrawC360FeedbackGainPlot(ScottPlot.Plot plot, AutoTestCaseItem row)
    {
        plot.Clear();
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101011");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D4D4D8"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#34343B");
        bool optional = row.Config?.IsOptional == true;
        plot.Axes.Title.Label.Text = $"{row.Id} · {row.Name} · {row.StatusDisplay}";
        plot.Axes.Title.Label.ForeColor = optional ? ScottPlot.Color.FromHex("#A1A1AA") : ScottPlot.Color.FromHex("#F4F4F5");
        plot.Axes.Left.Label.Text = "Đỉnh hẹp so với nền (dB)";
        plot.Axes.Bottom.Label.Text = "Gain vòng (dB) · cam: FBX tắt · xanh: FBX bật";
        if (row.FeedbackGain is { } loop && (loop.Off.Steps.Count > 0 || loop.On.Steps.Count > 0))
        {
            double margin = row.Config?.FeedbackGain?.HowlMarginDb ?? new FeedbackGainSettings().HowlMarginDb;
            var thresh = plot.Add.HorizontalLine(margin);
            thresh.Color = ScottPlot.Color.FromHex("#71717A");
            thresh.LinePattern = ScottPlot.LinePattern.Dashed;
            foreach (var (run, color) in new[] { (loop.Off, ScottPlot.Colors.Orange), (loop.On, ScottPlot.Colors.DeepSkyBlue) })
            {
                if (run.Steps.Count == 0) continue;
                // One point per gain step (peak peak-to-median at that step) so the curve stays legible.
                var grouped = run.Steps.GroupBy(s => Math.Round(s.GainDb, 2)).OrderBy(g => g.Key)
                    .Select(g => (Gain: g.Key, Peak: g.Max(x => x.PeakToMedianDb))).ToArray();
                var line = plot.Add.Scatter(grouped.Select(x => x.Gain).ToArray(), grouped.Select(x => x.Peak).ToArray());
                line.Color = color; line.LineWidth = 2; line.MarkerSize = 5;
            }
            plot.Axes.AutoScale();
        }
        else
        {
            var note = plot.Add.Text(optional ? $"TÙY CHỌN — {row.OptionalMeasurementSummary}" : row.DistortionText.Split('\n')[0], 0, 0);
            note.LabelFontColor = ScottPlot.Color.FromHex("#A1A1AA");
            note.LabelFontSize = 14;
            note.Alignment = ScottPlot.Alignment.MiddleCenter;
            plot.Axes.SetLimits(-1, 1, -1, 1);
        }
    }

    private void ShowC360FeedbackGainResult(AutoTestCaseItem row)
    {
        _frequencyChartRefreshTimer.Stop();
        ShowInlineImpulse(null);
        HideFrequencyHover();
        TxtFrequencyChartHeading.Text = row.Config.IsOptional
            ? row.Id + " · GAIN TRƯỚC HÚ (TÙY CHỌN)"
            : row.Id + " · GAIN TRƯỚC HÚ";
        DrawC360FeedbackGainPlot(PlotFreqResponse.Plot, row);
        PlotFreqResponse.Refresh();
        ClearThdPlots();
        TxtQualityChartHeading.Text = row.Config.IsOptional ? "GAIN TRƯỚC HÚ (TÙY CHỌN)" : "GAIN TRƯỚC HÚ";
        PanelThdInspection.Visibility = Visibility.Collapsed;
        TxtThdStatus.Text = "";
        TxtThdVerdictBadge.Text = row.StatusDisplay;
        TxtThdVerdictBadge.Foreground = row.StatusBrush;
        BorderThdVerdictBadge.BorderBrush = row.StatusBrush;
        TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(row, _activeInOutConfig) + "\n" + row.DistortionText;
        UpdateMeasurementDetails();
    }

    private static void SaveC360FeedbackGainGraphPng(AutoTestCaseItem row, string destinationPath)
    {
        var plot = new ScottPlot.Plot();
        DrawC360FeedbackGainPlot(plot, row);
        SaveServerGraphPng(plot, destinationPath);
    }

    /// <summary>Owner 2026-11-19 (A1/A2): renders an honest 800×450 status card PNG when an unconcluded or optional step
    /// has no measured curve, so FAIL-suite uploads still include a clear image for every step instead of failing or plotting noise.</summary>
    public static void SaveStatusPlaceholderGraphPng(AutoTestCaseItem row, string destinationPath)
    {
        var plot = new ScottPlot.Plot();
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#121214");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101011");
        plot.Axes.Color(ScottPlot.Color.FromHex("#71717A"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#27272A");
        bool optional = row.Config?.IsOptional == true;
        plot.Axes.Title.Label.Text = $"{row.Id} · {row.Name} · {row.StatusDisplay}";
        plot.Axes.Title.Label.ForeColor = optional ? ScottPlot.Color.FromHex("#A1A1AA")
            : C360VerdictPolicy.IsFailStatus(row.Status) ? ScottPlot.Color.FromHex("#FA8072") : ScottPlot.Color.FromHex("#FACC15");
        string body = optional
            ? (string.IsNullOrWhiteSpace(row.OptionalMeasurementSummary) ? "TÙY CHỌN — chưa có số đo" : "TÙY CHỌN — " + row.OptionalMeasurementSummary)
            : C360VerdictPolicy.IsFailStatus(row.Status)
                ? "KHÔNG ĐẠT — " + C360VerdictPolicy.FirstMeaningfulLine(row.DistortionText)
                : "CHƯA KẾT LUẬN — " + C360VerdictPolicy.ShortUnconcludedReason(row);
        var label = plot.Add.Text(body, 0, 0);
        label.LabelFontColor = optional ? ScottPlot.Color.FromHex("#A1A1AA")
            : C360VerdictPolicy.IsFailStatus(row.Status) ? ScottPlot.Color.FromHex("#FA8072") : ScottPlot.Color.FromHex("#FCD34D");
        label.LabelFontSize = 16;
        label.LabelBold = true;
        label.Alignment = ScottPlot.Alignment.MiddleCenter;
        plot.Axes.SetLimits(-1, 1, -1, 1);
        SaveServerGraphPng(plot, destinationPath);
    }

    private void UpsertAutoTestGraphPath(string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        _autoTestGraphPaths.RemoveAll(p => string.Equals(System.IO.Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
        _autoTestGraphPaths.Add(path);
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

    /// <summary>End-of-run server outcome (null = nothing to say, e.g. a single test or LINE CHUẨN search).
    /// Owner 2026-11-19 (A1 #6): when <paramref name="passed"/> is provided, shows
    /// "Đã gửi server: KHÔNG ĐẠT — IO001 (mất echo); IO008 chưa kết luận (…)" + log ID.</summary>
    public static (string Message, ModernMessageBox.MessageBoxType Type)? UploadOutcome(bool productSuite, bool findReference, bool singleTest,
        bool valid, bool sendEnabled, bool uploaded, ServerEngine.AudioQaUploadReceipt? receipt, string? pendingReason, string? role,
        IEnumerable<string> notConcluded, bool? passed = null, string? headlineDetails = null)
    {
        if (!productSuite || findReference || singleTest) return null;
        if (!sendEnabled) return ("Không gửi server: ô \"Gửi kết quả lên server\" đang tắt.", ModernMessageBox.MessageBoxType.Warning);
        if (!valid)
            return ("Chưa gửi server: chưa có bài KHÔNG ĐẠT và còn bài chưa kết luận — " + string.Join("; ", notConcluded)
                + ".\nBấm trực tiếp vào bài chưa kết luận để đo lại, hoặc bấm TIẾP TỤC ĐO TỰ ĐỘNG; các bài đã xong được giữ lại.", ModernMessageBox.MessageBoxType.Warning);
        if (uploaded && receipt != null)
        {
            string roleNote = role is { Length: > 0 } r && !string.Equals(r, "STAFF", StringComparison.OrdinalIgnoreCase)
                ? $"\nLưu ý: tài khoản đang đăng nhập là {r}; Quy trình QA/QC chỉ hiện kết quả của tài khoản STAFF (vẫn có trong /report)." : "";
            if (passed.HasValue)
            {
                string headline = passed.Value
                    ? "Đã gửi server: ĐẠT"
                    : "Đã gửi server: KHÔNG ĐẠT" + (string.IsNullOrWhiteSpace(headlineDetails) ? "" : " — " + headlineDetails);
                return ($"{headline}\nMã log: {receipt.LogId} ({receipt.DataSet}) · {receipt.ImageUrls.Length} ảnh QA.\n"
                    + "Xem ở FACTORY FLOOR QA & APP TRACKER (/report) và Quy trình QA/QC của sản phẩm." + roleNote,
                    ModernMessageBox.MessageBoxType.Info);
            }
            return ($"Đã gửi lên server ({receipt.DataSet}): {receipt.ImageUrls.Length} ảnh QA · log {receipt.LogId}.\n"
                + "Xem ở FACTORY FLOOR QA & APP TRACKER (/report) và Quy trình QA/QC của sản phẩm." + roleNote,
                ModernMessageBox.MessageBoxType.Info);
        }
        return ("Chưa gửi được kết quả lên server: " + (pendingReason ?? "không rõ nguyên nhân")
            + "\nKết quả đã lưu tại máy; bấm GỬI LẠI KẾT QUẢ CHỜ khi có mạng / đăng nhập lại.", ModernMessageBox.MessageBoxType.Error);
    }
}
