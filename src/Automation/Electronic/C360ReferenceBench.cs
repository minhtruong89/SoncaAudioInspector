using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

/// <summary>Reference and production share the same routed suite, frozen plan and acquisition backend.</summary>
/// <param name="autoReference">Product Auto Test only: asked when a stage has no matching LINE CHUẨN. Returning true captures
/// and saves the LINE CHUẨN from the unit on the bench first (the same 3-run rule as TÌM LINE CHUẨN), then measures against it.</param>
public sealed class C360ReferenceBench(IC360ElectronicBench bench, C360ModelPlan plan, string model,
    string referenceDirectory, string mixerKey, bool findReference, double tolerance,
    Func<int, IC360ElectronicBench>? runBench = null, bool requireTrialReference = true,
    Func<C360ElectronicStage, TestConfig, CancellationToken, Task<bool>>? autoReference = null) : IC360ElectronicBench
{
    public string DeviceIdentity => bench.DeviceIdentity;
    public Task<IReadOnlyDictionary<ushort, ushort>> ReadAsync(CancellationToken token) => bench.ReadAsync(token);
    public Task WriteAsync(ushort register, ushort value, CancellationToken token) => bench.WriteAsync(register, value, token);
    public void StopAudio() => bench.StopAudio();
    public static bool RequiresReference(C360ElectronicStage stage) => stage is C360ElectronicStage.LineIn or C360ElectronicStage.UsbA
        or C360ElectronicStage.MicDry or C360ElectronicStage.Bluetooth or C360ElectronicStage.AcousticMic;
    /// <summary>Stages that never use a LINE CHUẨN (owner 2026-11-19): echo/reverb, FBX frequency shift, gain before feedback, Rè.</summary>
    public static bool UsesNoReference(C360ElectronicStage stage) => stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift
        or C360ElectronicStage.AcousticRubBuzz or C360ElectronicStage.MicFeedbackGain;
    public static C360Reference? TryLoad(string path, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
            return saved?.Key == key && saved.IsUsable ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public static string ReferencePath(string referenceDirectory, string key) => Path.Combine(referenceDirectory, "c360-" + key + ".json");
    /// <summary>True when the reference identity of this stage is known before the stage runs (BT/USB A endpoints only appear after
    /// BLE switches the source, so their key is resolved inside the stage).</summary>
    public static bool CanCheckBeforeRun(C360ModelPlan plan, C360ElectronicStage stage)
        => stage is not (C360ElectronicStage.Bluetooth or C360ElectronicStage.UsbA)
            || stage == C360ElectronicStage.UsbA && plan.PlaybackIds.TryGetValue(stage, out string? id) && !string.IsNullOrWhiteSpace(id);
    /// <summary>Stages of this plan that need a LINE CHUẨN and have none matching the current configuration and mixer state.</summary>
    public static IReadOnlyList<TestConfig> MissingReferences(C360ModelPlan plan, string model, string referenceDirectory, string mixerKey)
        => plan.Stages.Where(stage => RequiresReference(stage) && CanCheckBeforeRun(plan, stage)
                && TryLoad(ReferencePath(referenceDirectory, C360Reference.ConfigurationKey(model, plan, stage, mixerKey)),
                    C360Reference.ConfigurationKey(model, plan, stage, mixerKey)) == null)
            .Select(stage => plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage)).ToArray();
    public async Task<C360ElectronicResult> CaptureAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        try { return await CaptureCoreAsync(stage, setup, token); }
        catch (BluetoothPlaybackUnavailableException ex) when (stage == C360ElectronicStage.Bluetooth)
        {
            token.ThrowIfCancellationRequested();
            return new(stage, "NO_SIGNAL", (findReference ? "Chưa lưu LINE CHUẨN: " : "") + ex.Message);
        }
    }
    private async Task<C360ElectronicResult> CaptureCoreAsync(C360ElectronicStage stage, C360ElectronicSetup setup, CancellationToken token)
    {
        var test = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage);
        if (bench is IC360AcquisitionPreparation prepare)
        {
            setup = await prepare.PrepareAsync(stage, setup, token);
            if (stage == C360ElectronicStage.Bluetooth && string.IsNullOrWhiteSpace(setup.BluetoothPlaybackId))
                return new(stage, "NO_SIGNAL", "Đã chọn nguồn BT nhưng chưa thấy ngõ Bluetooth Windows của đúng loa đang điều khiển. Kết nối âm thanh BT rồi đo lại.");
            if (stage == C360ElectronicStage.UsbA && string.IsNullOrWhiteSpace(setup.UsbPlaybackId))
                return new(stage, "NO_SIGNAL", $"Đã xác nhận BLE ở USB A nhưng Windows chưa có ngõ phát {setup.UsbPlaybackName}. Kiểm tra kết nối USB A rồi đo lại.");
        }
        async Task<C360ElectronicResult> CaptureOnce() => bench is IC360AcquisitionPreparation prepared
            ? await prepared.CapturePreparedAsync(stage, setup, token) : await bench.CaptureAsync(stage, setup, token);
        // Owner 2026-11-19: effect (IO001 echo/reverb), FBX (IO002 di tần, IO009 gain trước hú) and Rè (IO008) are judged by their own
        // criteria only. They never read, compare against, ask for or save a LINE CHUẨN — not in Auto Test, not in TÌM LINE CHUẨN.
        if (UsesNoReference(stage)) return await CaptureOnce();
        string key = C360Reference.ConfigurationKey(model, plan, stage, mixerKey, setup);
        string path = ReferencePath(referenceDirectory, key);
        if (!findReference)
        {
            var saved = TryLoad(path, key);
            string autoNote = "";
            if (saved == null && RequiresReference(stage) && autoReference != null && await autoReference(stage, test, token))
            {
                var made = await SaveReferenceAsync(stage, setup, test, key, path, token);
                if (made.Reference == null)
                    // Owner 2026-11-19 (A1 #7, A6): unfinished auto-LINE-CHUẨN is always INCONCLUSIVE (yellow), never FAIL.
                    return made with { Status = "INCONCLUSIVE", MissingReference = true,
                        Summary = "Tự lấy LINE CHUẨN chưa xong — chưa kết luận. " + made.Summary };
                saved = made.Reference;
                autoNote = "ĐÃ TỰ LẤY LINE CHUẨN từ loa đang đo. ";
            }
            // Trial Auto Test needs a trial reference before any stimulus. A manual "ĐO LINE" still measures and shows the
            // response so the signal path can be checked; without a matching reference it is never a verdict (INVALID below).
            if (saved == null && ResultPersistencePolicy.IsTrial && requireTrialReference)
                return new(stage, "INVALID", "Chưa có LINE CHUẨN THỬ khớp cấu hình. Chạy TÌM LINE CHUẨN THỬ trước Auto Test.") { MissingReference = true };
            var current = await CaptureOnce();
            if (saved != null)
            {
                var compared = saved.Compare(current, test, tolerance) with { Reference = saved };
                return autoNote.Length == 0 ? compared : compared with { Summary = autoNote + compared.Summary };
            }
            bool requiresReference = RequiresReference(stage);
            return current with { Status = requiresReference && current.Status == "PASS" ? "INVALID" : current.Status,
                MissingReference = requiresReference && current.Status is "PASS" or "FAIL",
                Summary = current.Summary + (stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift
                    ? " Chưa có LINE CHUẨN khớp cấu hình hiện tại. Chấm theo kỳ vọng của bài đo."
                    : current.Status == "PASS" ? " Chưa có LINE CHUẨN khớp cấu hình hiện tại. Chưa có LINE CHUẨN — không kết luận. Chạy Tìm LINE CHUẨN cho bài này rồi đo lại."
                    : current.Status == "FAIL" ? " Chưa có LINE CHUẨN khớp cấu hình; đáp tuyến chưa được so."
                    : "") };
        }
        // Reuse is keyed by the complete measurement configuration, not just a route label.
        if (TryLoad(path, key) is { } existing)
            return new C360ElectronicResult(stage, "PASS", "ĐÃ CÓ LINE CHUẨN khớp cấu hình; bỏ qua đo lại.") { Reference = existing, FrequencyShift = existing.FrequencyShift };
        return await SaveReferenceAsync(stage, setup, test, key, path, token);
    }
    /// <summary>Owner 2026-11-19 (A6): returns why a capture cannot count toward a response LINE CHUẨN, or null if usable.
    /// THD exceeding limit does NOT disqualify a response-reference capture (THD still judges the product measurement).</summary>
    public static string? ResponseReferenceSkipReason(C360ElectronicStage stage, C360ElectronicResult current)
    {
        if (stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift)
            return current.Status == "PASS" ? null : $"trạng thái {current.Status}";
        if (string.Equals(current.Status, "NO_SIGNAL", StringComparison.OrdinalIgnoreCase))
            return "không có tín hiệu";
        if (current.Measurement == null)
            return "thiếu dữ liệu đáp tuyến";
        if (current.Measurement.IsClipped)
            return "bản thu bị clipping";
        if (string.Equals(current.Measurement.Validity, "INVALID", StringComparison.OrdinalIgnoreCase))
            return "bản thu không hợp lệ";
        if (current.Status is not ("PASS" or "FAIL"))
        {
            string line = C360VerdictPolicy.FirstMeaningfulLine(current.Summary);
            return line.Length > 0 ? line : $"trạng thái {current.Status}";
        }
        return null;
    }

    private async Task<C360ElectronicResult> SaveReferenceAsync(C360ElectronicStage stage, C360ElectronicSetup setup, TestConfig test,
        string key, string path, CancellationToken token)
    {
        var captures = new List<(int Run, C360ElectronicResult Result)>();
        var skipReasons = new List<string>();
        string pairReason = "";
        C360ElectronicResult? lastCapture = null;
        for (int run = 1; run <= 3; run++)
        {
            token.ThrowIfCancellationRequested();
            var current = await (runBench?.Invoke(run) ?? bench).CaptureAsync(stage, setup, token);
            lastCapture = current;
            if (ResponseReferenceSkipReason(stage, current) is string skip)
            {
                skipReasons.Add($"lượt {run}/3 không tính vào chuẩn vì {skip}");
                if (captures.Count + (3 - run) < 2) break;
                continue;
            }
            captures.Add((run, current));
            for (int previous = 0; previous < captures.Count - 1; previous++)
            {
                try
                {
                    var reference = C360Reference.Create(key, new[] { captures[previous].Result, current }, test);
                    token.ThrowIfCancellationRequested(); Directory.CreateDirectory(referenceDirectory);
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(reference, C360Reference.Json));
                    var readback = JsonSerializer.Deserialize<C360Reference>(File.ReadAllText(path), C360Reference.Json);
                    if (readback?.Key != key || readback.Runs < 2) throw new IOException("Không đọc lại được mẫu chuẩn vừa lưu.");
                    return current with { Status = "PASS", Summary = $"ĐÃ LƯU LINE CHUẨN: trung bình lượt {captures[previous].Run} và {run}. " + current.Summary, Reference = reference };
                }
                catch (InvalidOperationException ex) { pairReason = $"lượt {captures[previous].Run} và {run} không tính vào chuẩn vì {ex.Message}"; }
            }
        }
        int? offRate = captures.Select(c => c.Result.Measurement?.SampleRate).FirstOrDefault(rate => rate is > 0 and not 44100);
        var reasons = new List<string>(skipReasons);
        if (pairReason.Length > 0) reasons.Add(pairReason);
        string details = reasons.Count > 0 ? string.Join("; ", reasons) : "chưa có hai lượt lặp ổn định.";
        string prefix = captures.Count >= 2
            ? "CHUẨN CHƯA ỔN ĐỊNH: đã đo 3 lượt hợp lệ, chưa có hai lượt lặp ổn định. "
            : "Chưa lưu LINE CHUẨN: ";
        return (lastCapture ?? new C360ElectronicResult(stage, "INCONCLUSIVE", details)) with
        {
            Status = "INCONCLUSIVE",
            MissingReference = true,
            Summary = prefix + details
                + (offRate is int rate ? $" Bản thu ở {rate} Hz, chuẩn đo 44100 Hz: đặt Default Format ngõ thu 44,1 kHz rồi tìm lại." : "")
        };
    }
}
