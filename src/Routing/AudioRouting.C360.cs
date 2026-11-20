using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace SoncaAudioInspector;

public partial class AudioRouting
{
    private CancellationTokenSource? _c360ModelCancellation;
    private TaskCompletionSource? _c360ModelFinished;
    private C360ModelPlan? _runningC360Plan;
    public bool RequiresCancellationForEndpointLoss(string deviceId)
        => _runningC360Plan == null || _currentRunningTestCase?.Config?.SpeakerStage == "Bluetooth"
            && string.Equals(deviceId, SoncaBleManager.Instance.BluetoothPlaybackEndpointId, StringComparison.OrdinalIgnoreCase)
            || _runningC360Plan.RequiresStopOnEndpointLoss(
            _currentRunningTestCase?.Config is { } test ? C360ModelDefinition.Stage(test) : null, deviceId);
    internal Func<IC360ElectronicBench>? C360BenchFactory = null;
    internal Func<IReadOnlyList<C360ModelEndpoint>>? C360EndpointProvider = null;
    internal Func<CancellationToken, Task<bool>>? EnsureC360Connection = null;
    /// <summary>Owner 2026-11-19 (A4): test hook replacing the interactive RetestInvalidStepDialog.</summary>
    internal Func<RetestVolumePlan, RetestVolumeOverride?>? RetestInvalidPrompt = null;

    private sealed record C360SingleRetestContext(C360AutoTestResumeSession Session, TestConfig Target, RetestVolumeOverride VolumeOverride);

    /// <summary>Owner 2026-11-19 (A4): opens "Đo lại IO00x" for an unconcluded row, re-measures only that step with the
    /// one-shot volume override (never mutating model config), recalculates the suite verdict per A1 and re-uploads under the same uploadSessionId.</summary>
    internal async Task RetestC360InvalidRowAsync(AutoTestCaseItem row)
    {
        if (IsTestingBusy || _activeInOutConfig == null || row.Config == null) return;
        string model = (Application.Current.MainWindow as MainWindow)?.ComboModels.SelectedItem?.ToString() ?? "C360";
        var session = GetC360RetestSession(model);
        if (session == null) return;
        var configTest = _activeInOutConfig.Tests.FirstOrDefault(t => t.id == row.Id) ?? row.Config;
        string reason = C360VerdictPolicy.ShortUnconcludedReason(row);
        var plan = RetestVolumePlan.Create(configTest, _activeInOutConfig, reason);
        RetestVolumeOverride? volumeOverride;
        if (RetestInvalidPrompt is { } prompt)
        {
            volumeOverride = prompt(plan);
        }
        else
        {
            var dlg = new RetestInvalidStepDialog(plan) { Owner = Window.GetWindow(this) };
            volumeOverride = dlg.ShowDialog() == true ? dlg.ResultOverride : null;
        }
        if (volumeOverride == null) return;
        await RunC360ModelAutoTestCoreAsync(findReference: false, singleTest: configTest, retest: new(session, configTest, volumeOverride));
    }

    public async Task StopC360ForShutdownAsync()
    {
        if (_measurementDetailsWindow?.Impedance is { } impedance) await impedance.StopForShutdownAsync();
        await ReleaseManualRubBuzzAsync();
        var completed = _c360ModelFinished?.Task;
        _c360ModelCancellation?.Cancel();
        if (completed != null) await completed;
    }

    private C360ElectronicSetup C360MachineDefaults()
    {
        string path = Path.Combine(GetUserDataDirectory(), "c360-electronic.json");
        try { if (File.Exists(path) && JsonSerializer.Deserialize<C360ElectronicSetup>(File.ReadAllText(path)) is { } saved) return saved; }
        catch (Exception ex) when (ex is JsonException or IOException) { AppendLog("C360", ex.Message); }
        return new("unused", "", "unused", _audioEngine.PlaybackSampleRate is 44100 or 48000 ? _audioEngine.PlaybackSampleRate : 44100);
    }

    private C360ModelPlan BuildC360Plan(InOutConfig config)
    {
        var defaults=C360MachineDefaults() with {BleControllerAddress=PreferredModelController(config)};
        if (C360EndpointProvider is { } provide) return C360ModelPlan.Create(config, provide(), defaults);
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<C360ModelEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active)) using (device)
            endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture));
        return C360ModelPlan.Create(config, endpoints, defaults);
    }

    /// <summary>Windows friendly names selected in CẤU HÌNH ĐƯỜNG ÂM THANH, for the model editor's "Lấy ngõ đang chọn".</summary>
    public (string? Playback, string? Recording) SelectedEndpointNames()
    {
        static string? Name(MMDevice? device)
        {
            try { return device?.FriendlyName; }
            catch (System.Runtime.InteropServices.COMException) { return null; }
        }
        return (Name(SelectedPlaybackDevice), Name(SelectedRecordingDevice));
    }

    private bool CheckC360ModelDevices(InOutConfig config, out string missingMessage)
    {
        try { _ = BuildC360Plan(config); missingMessage = ""; return true; }
        catch (Exception ex)
        {
            // The plan stops at the first unresolved name; list every wrong or missing endpoint so the operator can tell
            // a misspelt name from an unplugged device/hub.
            missingMessage = ex.Message;
            try
            {
                var endpoints = C360EndpointProvider is { } provide
                    ? provide().Select(e => new WindowsEndpointName(e.Id, e.Name, e.Capture)).ToArray()
                    : ModelEndpointCheck.ReadActive();
                var issues = ModelEndpointCheck.Check(config, endpoints);
                if (issues.Count > 0)
                    missingMessage = ex is InvalidOperationException ? ModelEndpointCheck.Describe(issues)
                        : ex.Message + "\n" + ModelEndpointCheck.Describe(issues);
            }
            catch (Exception detail) when (detail is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException) { }
            return false;
        }
    }

    /// <summary>
    /// A C360 capture at another rate than 44.1 kHz is not repeatable on the BT path (12/11/2026: 48 kHz capture, time base wandering
    /// 1–4 ms inside one sweep, no stable pair in 6 runs; every repeatable BT pair before was captured at 44.1 kHz). Windows gives each
    /// USB port its own endpoint instance with its own Default Format, so moving the C360 to another hub can silently bring back 48 kHz.
    /// Sync the bench endpoints first; return the capture stream rate that the measurement will really use.
    /// </summary>
    private async Task<int> SyncC360EndpointRatesAsync(C360ModelPlan plan, CancellationToken token)
    {
        var ids = plan.CaptureIds.Values.Concat(plan.PlaybackIds.Values).Append(plan.Setup.LineOutCaptureId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "unused").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool changed = false;
        using (var enumerator = new MMDeviceEnumerator())
            foreach (string id in ids)
            {
                try
                {
                    using var device = enumerator.GetDevice(id);
                    if (ReadEndpointSampleRate(device) == StandardSampleRate) continue;
                    bool ok = AudioSampleRateSetup.EnsureStandard(device, out string message);
                    changed |= ok && message.Length > 0;
                    if (message.Length > 0) AppendLog(ok ? "Đồng bộ 44.1 kHz" : "Cảnh báo 44.1 kHz", message);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { AppendLog("Cảnh báo 44.1 kHz", ex.Message); }
            }
        if (changed) _audioEngine.CaptureSession.Reset();
        // Windows re-initialises an endpoint after a format change; read the stream rate again once it is back.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var capture = enumerator.GetDevice(plan.Setup.LineOutCaptureId);
                using var client = capture.AudioClient;
                int rate = client.MixFormat.SampleRate;
                if (!changed || rate == StandardSampleRate || attempt >= 4) return rate;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { }
            await Task.Delay(250, token);
        }
    }

    internal static string? C360CaptureRateProblem(int captureRate, int? defaultRate, string captureName, bool findReference)
        => captureRate == StandardSampleRate ? null
            : $"CẢNH BÁO: ngõ thu {captureName} đang chạy {captureRate} Hz, chuẩn đo C360 là {StandardSampleRate} Hz. "
              + (defaultRate == StandardSampleRate
                  // 12/11/2026: Default Format 44.1 kHz but a 48 kHz float stream, VocaEffectPack APO attached, silence forced to 0.
                  ? "Default Format đã là 44,1 kHz nhưng Windows Audio enhancements đang xử lý tín hiệu thu (chống ồn/tự chỉnh mức, đổi sang 48 kHz). "
                    + $"Tắt: Settings → System → Sound → {captureName} → Audio enhancements: Off, rồi đo lại."
                  : "Nên mở Sound → Recording → thiết bị này → Properties → Advanced → chọn 2 channel, 16 bit, 44100 Hz và tắt Audio enhancements rồi đo lại.")
              + (findReference ? $" Vẫn lưu LINE CHUẨN nếu hai lượt lặp lại trong 3 dB; chuẩn này chỉ khớp khi đo ở {captureRate} Hz (12/11/2026: BT thu 48 kHz không lặp lại được)." : "");

    public async Task PrepareModelBleAsync(InOutConfig config)
    {
        if (!C360ModelDefinition.Matches(config)) return;
        if (config.BleSelectStrongestByModel || config.EffectiveBleNamePrefix != null) return; // Scan the full RSSI window only on the explicit Auto Test action.
        try { if (await SoncaBleManager.Instance.EnsureConnectedAsync(PreferredModelController(config), expectedModelId: ExpectedFirmwareModel(config)))
            {Acoustic3ModelDefinition.RequireFirmware(config,SoncaBleManager.Instance.FirmwareInfoPayload);RememberC360Controller();} }
        catch (Exception ex) { AppendLog("BLE", "Chuẩn bị kết nối: " + ex.Message); }
    }
    private ulong? PreferredModelController(InOutConfig config)
    {
        var ble=SoncaBleManager.Instance;
        if(ble.IsConnected && (config.FirmwareModelId==null || SoncaBleMixerProfile.TryReadModelId(ble.FirmwareInfoPayload,out ushort model) && model==config.FirmwareModelId))
            return ble.ConnectedBluetoothAddress;
        if(config.FirmwareModelId is ushort expected)
            return AudioBenchPreferences.Load().KnownControllers?.TryGetValue(expected,out ulong address)==true?address:null;
        return C360MachineDefaults().BleControllerAddress;
    }

    private static ushort? ExpectedFirmwareModel(InOutConfig config) => config.FirmwareModelId
        ?? (config.AutomationProfile == C360ModelDefinition.Profile ? (ushort)1075 : null);

    private void RememberC360Controller()
    {
        if (SoncaBleManager.Instance.ConnectedBluetoothAddress is not ulong address) return;
        var saved = C360MachineDefaults();
        if (saved.BleControllerAddress != address)
            ResultPersistencePolicy.WriteAllText(Path.Combine(GetUserDataDirectory(), "c360-electronic.json"),
                JsonSerializer.Serialize(saved with { BleControllerAddress = address }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Task RunC360ModelAutoTestAsync(bool findReference = false, TestConfig? singleTest = null)
        => RunC360ModelAutoTestCoreAsync(findReference, singleTest, retest: null);

    private async Task RunC360ModelAutoTestCoreAsync(bool findReference, TestConfig? singleTest, C360SingleRetestContext? retest)
    {
        if (ResultPersistencePolicy.IsTrial && !EnsureSerialReady()) return;
        using var persistence = ResultPersistencePolicy.BeginMeasurement();
        using var workflow = TryBeginRoutingWorkflow();
        if (workflow == null || _activeInOutConfig == null) return;
        using var cancellation = new CancellationTokenSource();
        _c360ModelCancellation = cancellation;
        var finished = _c360ModelFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = cancellation.Token;
        _discardCurrentMeasurementRequested = false; _isExecutingAutoSuite = true;
        (Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(IsSetupVisible);
        _leaveAudioReleasedAfterAutoTest = true;
        StopRoutingHeadroomMonitor(); _audioEngine.StopAllAudio();
        string model = (Application.Current.MainWindow as MainWindow)?.ComboModels.SelectedItem?.ToString() ?? "C360";
        bool isSingleRetest = retest != null;
        bool productSuite = !findReference && (singleTest == null || isSingleRetest);
        var resume = isSingleRetest ? retest!.Session : productSuite ? GetC360AutoTestResumeSession(model) : null;
        if (resume == null) { _c360AutoTestResumeSession = null; _reviewedAutoTestCase = null; }
        _allowInlineEffectImpulse = singleTest == null || isSingleRetest;
        ShowInlineImpulse(_reviewedAutoTestCase);
        string serial = isSingleRetest ? resume!.Serial : ResolveC360RunSerial(model, productSuite, findReference, resume?.Serial);
        if (isSingleRetest && resume!.Identity != null) _measurementIdentity = resume.Identity;
        string folder = resume?.Folder ?? Path.Combine(LocalQaStorage.Root, DateTime.Now.ToString("yyyyMMdd"), ProductMeasurementContext.FileToken(serial) + "_" + DateTime.Now.ToString("HHmmssfff"));
        string captureFolder = resume == null ? folder : Path.Combine(folder, (isSingleRetest ? $"retest-{retest!.Target.id}-" : "continue-") + ++resume.Continuations);
        _autoTestSessionFolder = folder;
        if (resume == null) _autoTestGraphPaths.Clear();
        if (productSuite)
        {
            if (!isSingleRetest) SetCurrentProduct(null);
            if (Application.Current.MainWindow is MainWindow main) main.SetMeasurementSerialText(serial);
            TxtProductSerial.Text = serial;
        }
        var buttons = new[] { BtnStart, BtnStartAutoTest, BtnSaveStandardReference, BtnNoiseTest, BtnSaveStandard };
        var enabled = buttons.Select(b => b.IsEnabled).ToArray();
        foreach (var b in buttons) b.IsEnabled = false;
        var applied = new HashSet<C360ElectronicStage>();
        var timer = Stopwatch.StartNew();
        AudioBenchPreferences? policy = null; bool completedSuite = false, removeBluetooth = false; ulong? measuredAddress = null;
        IC360ElectronicBench? activeBench = null; C360BleRestoreState? volumeRestore = null; string rateNotice = "";
        bool hasBluetoothStage = false; _endpointLossReason = null;
        var autoReference = new AutoReferenceDecision(this);
        try
        {
            (Application.Current.MainWindow as MainWindow)?.CancelBleAudioPreparation();
            await WindowsBluetoothPairing.WaitForIdleAsync(token);
            policy = new AudioBenchPreferences();
            var fullConfiguration = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(_activeInOutConfig))!;
            foreach (var t in fullConfiguration.Tests) t.ModelVolume = ModelTestVolume.Merge(fullConfiguration.TestVolume, t.TestVolume);
            var configuration = JsonSerializer.Deserialize<InOutConfig>(JsonSerializer.Serialize(_activeInOutConfig))!;
            removeBluetooth = configuration.BluetoothAutoRemove;
            if (isSingleRetest)
            {
                var target = configuration.Tests.Single(t => t.id == retest!.Target.id);
                retest!.VolumeOverride.ApplyToClonedTest(configuration, target);
                foreach (var test in configuration.Tests) test.Enable = test.IsEnabled && test.id == target.id;
            }
            else if (singleTest != null)
            {
                foreach (var test in configuration.Tests) test.Enable = test.IsEnabled && test.id == singleTest.id;
            }
            _soundCardReport = CheckAnalogPortsQuietly(configuration);
            var plan = BuildC360Plan(configuration);
            _runningC360Plan = plan;
            hasBluetoothStage = plan.Stages.Contains(C360ElectronicStage.Bluetooth);
            if (isSingleRetest)
            {
                AppendLog("Auto Test", $"Đo lại {retest!.Target.id} · {serial} · {model} · {retest.VolumeOverride.AuditNote}");
                AppendLog("Đo lại", retest.VolumeOverride.AuditNote);
            }
            else
            {
                AppendLog("Auto Test", $"{(resume != null ? "Tiếp tục" : findReference ? "Tìm LINE CHUẨN" : "Bắt đầu")} {serial} · {model} · "
                    + string.Join(", ", configuration.Tests.Where(t => t.IsEnabled).Select(t => t.id + " " + t.SpeakerStage)));
            }
            foreach (var test in plan.Configuration.Tests.Where(t => t.IsEnabled)) AppendLog("Âm lượng", test.id + ": " + test.VolumeMapping);
            string metaFolder = isSingleRetest ? captureFolder : folder;
            ResultPersistencePolicy.CreateDirectory(metaFolder);
            ResultPersistencePolicy.WriteAllText(Path.Combine(metaFolder, "model.json"), JsonSerializer.Serialize(plan.Configuration, new JsonSerializerOptions { WriteIndented = true }));
            ResultPersistencePolicy.WriteAllText(Path.Combine(metaFolder, "acquisition.json"), JsonSerializer.Serialize(plan.Setup, new JsonSerializerOptions { WriteIndented = true }));
            ResultPersistencePolicy.WriteAllText(Path.Combine(metaFolder, "run-policy.json"), JsonSerializer.Serialize(new { configuration.BluetoothAutoRemove,
                Tests = configuration.Tests.Where(t => t.IsEnabled).Select(t => new { t.id, t.MaximumRuns, t.RequiredPasses }) }));
            if (resume == null || isSingleRetest) ClearC360ModelResultsFor(plan.Stages);
            if (productSuite && resume == null)
                _c360AutoTestResumeSession = new(model, AutoTestConfigurationKey(), serial, folder, _autoTestCases.ToArray()) { Identity = _measurementIdentity };
            var session = isSingleRetest ? retest!.Session : productSuite ? _c360AutoTestResumeSession : null;
            if (session != null) session.Identity ??= _measurementIdentity;
            if (_reviewedAutoTestCase == null) SetC360Verdict("ĐANG KẾT NỐI BLE…", Brushes.Gold);
            // Load the Fourier backend while Windows opens BLE, rather than on the first sweep.
            _ = Task.Run(() => SweepFourier.BackendName);
            bool connected = EnsureC360Connection != null ? await EnsureC360Connection(token)
                : await SoncaBleManager.Instance.EnsureConnectedAsync(plan.Setup.BleControllerAddress, token, userInitiated: true,
                    expectedModelId: ExpectedFirmwareModel(configuration), selectStrongestByModel: configuration.BleSelectStrongestByModel && configuration.IsBleCheckAtStartEnabled,
                    namePrefix: configuration.EffectiveBleNamePrefix);
            token.ThrowIfCancellationRequested();
            if (!connected) throw new InvalidOperationException(configuration.EffectiveBleNamePrefix is string blePrefix
                ? $"Chưa kết nối được loa BLE có tên bắt đầu bằng \"{blePrefix}\"{(ExpectedFirmwareModel(configuration) is ushort id ? $" và đúng model {id}" : "")}. Bật loa, đặt gần máy đo rồi bấm đo lại."
                : "Chưa kết nối được điều khiển BLE của loa. Mở Ctrl+Shift+H để kiểm tra đúng loa trước khi đo.");
            if (EnsureC360Connection == null)
            {
                Acoustic3ModelDefinition.RequireFirmware(configuration,SoncaBleManager.Instance.FirmwareInfoPayload);
                if (!configuration.BleSelectStrongestByModel && configuration.EffectiveBleNamePrefix == null) RememberC360Controller();
                measuredAddress = SoncaBleManager.Instance.ConnectedBluetoothAddress;
                if (configuration.IsBleCheckAtStartEnabled && !isSingleRetest)
                {
                    var ble = SoncaBleManager.Instance;
                    string identity = ble.ConnectedDeviceAddress;
                    var requested = plan.Stages.Select(stage => stage == C360ElectronicStage.Bluetooth ? (byte)0 : stage == C360ElectronicStage.UsbA ? (byte)1 : (byte)4).Distinct().ToArray();
                    var report = await SoncaBleRouteVerification.VerifyAsync(ble, requested,
                        () => ble.IsConnected && ble.ConnectedDeviceAddress == identity, token);
                    string names = string.Join(", ", report.Confirmed.Select(ble.InputSourceName));
                    SoncaBleMixerProfile.TryReadModelId(ble.FirmwareInfoPayload, out ushort reportedModel);
                    string rssi = ble.SelectedModelRssi is short signal ? $" · RSSI {signal} dBm" : "";
                    var missingInputs = requested.Except(report.Confirmed).Select(ble.InputSourceName).ToArray();
                    string inputReport = InputReport($"{string.Join(" / ", SoncaFirmwareModels.Current.Find(reportedModel))} · {identity}", rssi.TrimStart(' ', '·'),
                        report.Available.Select(ble.InputSourceName), report.Confirmed.Select(ble.InputSourceName), missingInputs, _soundCardReport,
                        plan.Configuration.Tests.Where(t => t.IsEnabled).Select(t => t.id + (t.IsOptional ? " (tùy chọn)" : "")));
                    AppendLog("BLE", inputReport.Replace("\n", " | ") + " · đã chuyển: " + names);
                    // Info closes by itself and the run continues; a missing input stays on screen until read (owner 2026-11-19).
                    C360Popup(inputReport, missingInputs.Length > 0 ? "Thiếu ngõ đo qua BLE" : "Đã kiểm tra ngõ đo qua BLE",
                        missingInputs.Length > 0 ? ModernMessageBox.MessageBoxType.Warning : ModernMessageBox.MessageBoxType.Info, persistent: true);
                    if (configuration.BleSelectStrongestByModel && requested.Except(report.Confirmed).Any())
                        throw new InvalidOperationException("Chưa xác nhận đủ LINE IN và BT; chưa phát tín hiệu đo.");
                }
            }
            double connectSeconds = timer.Elapsed.TotalSeconds;
            var bench = C360BenchFactory?.Invoke() ?? new C360AudioBench(_audioEngine, SoncaBleManager.Instance, captureFolder);
            activeBench = bench;
            var baseline = await bench.ReadAsync(token);
            string volumeIdentity = bench.DeviceIdentity;
            if (session?.DeviceIdentity is string previousIdentity && previousIdentity != volumeIdentity)
                throw new InvalidOperationException("Loa BLE đã đổi so với phiên đang dở. Bấm Dừng / Xóa kết quả để đo loa mới từ đầu.");
            if (session != null) session.DeviceIdentity = volumeIdentity;
            volumeRestore = session ?? new C360BleRestoreState { DeviceIdentity = volumeIdentity };
            Task WriteVolume(ushort reg, ushort val, CancellationToken ct) => bench.DeviceIdentity != volumeIdentity
                ? throw new InvalidOperationException("Loa BLE đã đổi trong lúc chỉnh âm lượng; hủy lượt này và chọn lại đúng loa.")
                : bench.WriteAsync(reg, val, ct);
            async Task<IReadOnlyDictionary<ushort,ushort>> ReadVolume()
            {
                var state = await bench.ReadAsync(token);
                if (bench.DeviceIdentity != volumeIdentity) throw new InvalidOperationException("Loa BLE đã đổi trong lúc chỉnh âm lượng; hủy lượt này và chọn lại đúng loa.");
                return state;
            }
            if (session?.BleRestorePending == true)
            {
                await session.RestorePendingAsync(bench, token);
                baseline = await bench.ReadAsync(token);
            }
            baseline = await ConfirmModelVolumeAsync(plan.Configuration.TestVolume, ReadVolume, baseline, token,
                write: WriteVolume,
                recordOriginal: (before, regs) => volumeRestore.RecordOriginalVolume(before, regs),
                restoreOnCancel: _ => volumeRestore.RestoreOriginalVolumeAsync(bench, CancellationToken.None),
                settleDelayMs: C360BenchFactory != null ? 0 : 100);
            if (session != null) session.BaselineState = baseline.ToDictionary(p => p.Key, p => p.Value);
            string mixerKey = SoncaMixerControls.Fingerprint(baseline,SoncaBleManager.Instance.FirmwareInfoPayload,0);
            if (C360BenchFactory != null) mixerKey += ";CAPTURE_RATE:" + plan.Setup.SampleRate;
            else
            {
                int captureRate = await SyncC360EndpointRatesAsync(plan, token);
                string captureName; int? defaultRate;
                using (var devices = new MMDeviceEnumerator())
                using (var capture = devices.GetDevice(plan.Setup.LineOutCaptureId)) { captureName = capture.FriendlyName; defaultRate = ReadEndpointSampleRate(capture); }
                // Operator decision 12/11/2026: a non-44.1 kHz capture warns but never blocks, a reference included.
                if (C360CaptureRateProblem(captureRate, defaultRate, captureName, findReference) is string rateProblem)
                { rateNotice = rateProblem; AppendLog("Sample rate", rateProblem); }
                mixerKey += ";CAPTURE_RATE:" + captureRate;
            }
            if (session?.MixerKey is string previousMixer && previousMixer != mixerKey)
                throw new InvalidOperationException("Âm lượng, cấu hình loa hoặc sample rate đã đổi so với phiên đang dở. Khôi phục cấu hình cũ để tiếp tục, hoặc bấm Dừng / Xóa kết quả để đo lại từ đầu.");
            if (session != null) session.MixerKey = mixerKey;
            plan = await ProbeC360PortsAsync(bench, plan, session, baseline, token);
            Func<C360ElectronicStage, TestConfig, CancellationToken, Task<bool>>? askReference = productSuite
                ? (_, test, _) => autoReference.AskAsync([test]) : null;
            var referenceBench = new C360ReferenceBench(WithNoSignalRetry(bench, plan), plan, model, GetStandardsDirectory(), mixerKey, findReference,
                ParseDoubleSafe(TxtFreqTolerance.Text, 3), C360BenchFactory != null ? null
                    : run => WithNoSignalRetry(new C360AudioBench(_audioEngine, SoncaBleManager.Instance, Path.Combine(captureFolder, "reference-run-" + run)), plan),
                requireTrialReference: productSuite, autoReference: askReference);
            void Apply(C360ElectronicResult result)
            {
                if (!_isExecutingAutoSuite || token.IsCancellationRequested || _discardCurrentMeasurementRequested) return;
                if (result.Status != "RUNNING" && !applied.Add(result.Stage)) return;
                var effectiveResult = isSingleRetest && result.Status != "RUNNING" && !result.Summary.Contains(retest!.VolumeOverride.AuditNote, StringComparison.Ordinal)
                    ? result with { Summary = $"{result.Summary}\nĐo lại: {retest.VolumeOverride.AuditNote}" }
                    : result;
                var originalRowConfig = isSingleRetest ? fullConfiguration.Tests.Single(t => t.id == retest!.Target.id) : null;
                ApplyC360ProgressCore(effectiveResult, plan, originalRowConfig, isSingleRetest ? retest!.VolumeOverride.AuditNote : null);
                if (session != null && effectiveResult.Status != "RUNNING")
                {
                    session.AllResults[effectiveResult.Stage] = effectiveResult;
                    if (C360VerdictPolicy.IsPassStatus(effectiveResult.Status) || C360VerdictPolicy.IsConcludedFail(effectiveResult))
                        session.Completed[effectiveResult.Stage] = effectiveResult;
                    else
                        session.Completed.Remove(effectiveResult.Stage);
                }
            }
            var progress = new C360UiProgress(this, Apply);
            IC360ElectronicBench capturePipeline = referenceBench;
            if (productSuite)
                capturePipeline = new C360RepeatedBench(bench, policy, run => new C360ReferenceBench(
                    WithNoSignalRetry(C360BenchFactory != null ? bench : new C360AudioBench(_audioEngine, SoncaBleManager.Instance, Path.Combine(captureFolder, "attempt-" + run)), plan),
                    plan, model, GetStandardsDirectory(), mixerKey, false, ParseDoubleSafe(TxtFreqTolerance.Text, 3),
                    C360BenchFactory != null ? null
                        : refRun => WithNoSignalRetry(new C360AudioBench(_audioEngine, SoncaBleManager.Instance, Path.Combine(captureFolder, $"attempt-{run}-reference-run-{refRun}")), plan),
                    requireTrialReference: productSuite, autoReference: askReference), r =>
                    {
                        // The pink wrapper owns the final acoustic verdict; do not cache a sweep-only PASS first.
                        if (C360ModelDefinition.IsAcoustic(r.Stage) && plan.Configuration.Tests.Any(t => t.IsRubBuzzEnabled && t.UsesAcousticRubBuzz
                            && C360ModelDefinition.Stage(t) == r.Stage)) AppendLog("Lượt sweep âm học", r.Summary);
                        else Apply(r);
                    });
            // "Tìm line chuẩn" on a known-good unit also stores the pink rattle line standard (user requirement v1.3).
            capturePipeline = new C360PinkBench(capturePipeline, (r, s, t) => CaptureC360PinkRubBuzzAsync(r, _c360PortSync?.Apply(r.Stage, s, s.TestConfiguration) ?? s, t, saveAsStandard: findReference,
                saveIfMissing: productSuite && autoReference.Accepted));
            if (isSingleRetest && session != null)
                foreach (var stage in plan.Stages) session.Completed.Remove(stage);
            var remaining = isSingleRetest ? plan.Stages
                : plan.Stages.Where(stage => session?.Completed.ContainsKey(stage) != true).ToArray();
            if (resume != null && !isSingleRetest) AppendLog("Auto Test", $"Tiếp tục {serial}; giữ {session!.Completed.Count} bài hoàn tất, đo tiếp {remaining.Length} bài.");
            if (productSuite)
            {
                // Ask at the start (user request 2026-11-18) instead of ending with "CHƯA CÓ LINE CHUẨN". BT / USB A keys are known only
                // after BLE switches the source; those stages reuse this answer, or ask once when they are the first missing ones.
                var missing = C360ReferenceBench.MissingReferences(plan, model, GetStandardsDirectory(), mixerKey)
                    .Where(t => remaining.Contains(C360ModelDefinition.Stage(t))).ToArray();
                if (missing.Length > 0) await autoReference.AskAsync(missing);
                token.ThrowIfCancellationRequested();
            }
            IReadOnlyList<C360ElectronicResult> measuredResults = session != null
                ? await new C360ElectronicSuite().RunResumableAsync(plan.Bind(capturePipeline), plan.Setup, session, progress, token, remaining)
                : await new C360ElectronicSuite().RunAsync(plan.Bind(capturePipeline), plan.Setup, progress, token, remaining, baseline);
            if (session?.BleRestorePending == true) throw new InvalidOperationException("CHƯA HOÀN TẤT — chưa khôi phục BLE.");
            token.ThrowIfCancellationRequested();
            foreach (var result in measuredResults) Apply(result);
            var suiteConfig = isSingleRetest ? fullConfiguration : plan.Configuration;
            var suiteStages = suiteConfig.Tests.Where(t => t.IsEnabled).Select(C360ModelDefinition.Stage).ToArray();
            var combinedMap = new Dictionary<C360ElectronicStage, C360ElectronicResult>();
            if (session != null)
            {
                foreach (var kv in session.AllResults) combinedMap[kv.Key] = kv.Value;
                foreach (var kv in session.Completed) combinedMap.TryAdd(kv.Key, kv.Value);
            }
            foreach (var r in measuredResults)
            {
                combinedMap[r.Stage] = isSingleRetest && !r.Summary.Contains(retest!.VolumeOverride.AuditNote, StringComparison.Ordinal)
                    ? r with { Summary = $"{r.Summary}\nĐo lại: {retest.VolumeOverride.AuditNote}" }
                    : r;
            }
            var results = suiteStages.Where(combinedMap.ContainsKey).Select(stage => combinedMap[stage]).ToArray();
            // Owner 2026-11-19 (A1): >=1 required FAIL => overall FAIL & upload immediately; optional tests never decide; unfinished auto-reference => INCONCLUSIVE.
            var evaluatedItems = results.Select(r => (Config: suiteConfig.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == r.Stage), Result: r)).ToArray();
            var verdict = C360VerdictPolicy.Evaluate(evaluatedItems, (cfg, r) =>
                r.Stage == C360ElectronicStage.AcousticRubBuzz && _autoTestCases.FirstOrDefault(c => c.Id == cfg.id) is { } rubRow
                    ? C360VerdictPolicy.ShortUnconcludedReason(rubRow)
                    : C360VerdictPolicy.ShortUnconcludedReason(cfg, r));
            bool canUpload = verdict.CanUpload;
            bool passed = verdict.Passed;
            // Completion means every requested stage returned, independently of PASS/FAIL/INVALID.
            completedSuite = productSuite;
            if (productSuite && session != null) _lastC360ProductSession = session;
            if (productSuite && canUpload)
            {
                if (!verdict.HasUnconcludedRequired) _c360AutoTestResumeSession = null;
                else if (session != null) _c360AutoTestResumeSession = session;
                CompleteProductVerdict(passed);
            }
            else if (productSuite && session != null)
            {
                _c360AutoTestResumeSession = session;
            }
            // Ensure every stage has an image on disk before background QA snapshot/upload (A1/A2: unconcluded & optional steps get honest cards).
            if (productSuite && canUpload && ChkSendToServer.IsChecked == true)
            {
                foreach (var row in _autoTestCases)
                {
                    string expectedName = row.Config?.SpeakerStage == "MicFrequencyShift" ? row.Id + "-frequency-shift.png" : "FRA-" + row.Id + ".png";
                    string? existingPath = _autoTestGraphPaths.LastOrDefault(p => string.Equals(Path.GetFileName(p), expectedName, StringComparison.OrdinalIgnoreCase));
                    if (existingPath != null && File.Exists(existingPath)) continue;
                    string targetPath = Path.Combine(folder, expectedName);
                    if (row.Config?.SpeakerStage == "MicFeedbackGain") SaveC360FeedbackGainGraphPng(row, targetPath);
                    else SaveStatusPlaceholderGraphPng(row, targetPath);
                    if (File.Exists(targetPath)) UpsertAutoTestGraphPath(targetPath);
                }
            }
            string summaryJsonPath = Path.Combine(folder, "summary.json");
            var summaryPayload = new
            {
                model,
                serial,
                operatorNote = _measurementIdentity?.OperatorNote,
                overallStatus = verdict.OverallStatus,
                passed,
                canUpload,
                seconds = timer.Elapsed.TotalSeconds,
                connectSeconds,
                results = results.Select(r => new { r.StageName, r.Status, r.Summary, r.Attempts }).ToArray()
            };
            await Task.Run(() => ResultPersistencePolicy.WriteAllText(summaryJsonPath,
                JsonSerializer.Serialize(summaryPayload, new JsonSerializerOptions { WriteIndented = true })), token);
            _isExecutingAutoSuite = false;
            if (_reviewedAutoTestCase != null) DisplayAutoTestResult(_reviewedAutoTestCase);
            SetC360Verdict(verdict.DisplayText(findReference),
                canUpload ? (passed ? Brushes.MediumSpringGreen : Brushes.Salmon) : Brushes.Gold);
            string baseTiming = $"Hoàn tất {timer.Elapsed.TotalSeconds:F1}s · chuẩn bị BLE {connectSeconds:F1}s";
            TxtMeasurementTiming.Text = productSuite && canUpload && ChkSendToServer.IsChecked == true ? baseTiming + " · Đang gửi nền…" : baseTiming;
            AppendLog("Auto Test", $"{model}: {verdict.DisplayText(findReference)} · {timer.Elapsed.TotalSeconds:F1}s (chuẩn bị BLE {connectSeconds:F1}s); đã đọc lại và khôi phục trạng thái loa.");
            bool IsOptionalStage(C360ElectronicStage stage) => suiteConfig.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == stage).IsOptional;
            string Reason(C360ElectronicResult r) => r.Stage == C360ElectronicStage.AcousticRubBuzz
                && _autoTestCases.FirstOrDefault(c => c.Config.SpeakerStage == "AcousticRubBuzz") is { } rubRow ? RubBuzzVerdictLine(rubRow) : r.Summary;
            token.ThrowIfCancellationRequested();
            if (ChkSendToServer.IsChecked == true && (!productSuite || !canUpload))
                AppendLog("Server", !productSuite
                    ? "Không gửi server: chỉ gửi kết quả Auto Test sản phẩm."
                    : "Không gửi server: chưa có bài KHÔNG ĐẠT và còn bài chưa kết luận (" + string.Join("; ", verdict.UnconcludedRequiredSummaries) + ").");
            bool uploaded = false;
            if (productSuite && canUpload && ChkSendToServer.IsChecked == true)
            {
                // Owner 2026-11-19 (A5): let the UI paint the verdict + "Đang gửi nền…" before off-thread QA serialization & HTTP upload.
                await Dispatcher.Yield(DispatcherPriority.Render);
                await UploadCompletedAutoTestAsync(serial, model, folder, passed, true, replaceExisting: true);
                uploaded = _pendingQaCompletionSession == null && ServerEngine.LastAudioQaUploadReceipt != null;
                if (uploaded && session != null) session.HasUploadedOnce = true;
                TxtMeasurementTiming.Text = uploaded ? baseTiming + " · Đã gửi server" : baseTiming + " · Lưu chờ gửi";
            }
            string warnings = string.Join("\n\n", results.Select(r =>
                IsOptionalStage(r.Stage) ? null
                : !uploaded && C360VerdictPolicy.IsUnconcludedStatus(r.Status) ? r.StageName + ": " + Reason(r)
                : r.Stage is C360ElectronicStage.LineIn or C360ElectronicStage.UsbA && MeasurementSignalStatus.LowSignalWarning(r.Measurement) is string warning
                    ? r.StageName + ": " + warning : null).Prepend(rateNotice.Length > 0 ? rateNotice : null).Where(s => s != null));
            string? headlineDetails = canUpload && !passed ? AutoTestFaultNotes.BuildPopupHeadlineDetails(_autoTestCases) : null;
            var outcome = UploadOutcome(productSuite, findReference, singleTest != null && !isSingleRetest, canUpload, ChkSendToServer.IsChecked == true, uploaded,
                ServerEngine.LastAudioQaUploadReceipt, _pendingQaCompletionSession?.LastQaError ?? ServerEngine.LastError, ServerEngine.UserRole,
                verdict.UnconcludedRequiredSummaries, passed: canUpload ? passed : null, headlineDetails: headlineDetails);
            string? portNotes = _c360PortSync is { Notes.Count: > 0 } synced ? "Đã tự đồng bộ ngõ sound card theo tín hiệu thật:\n" + string.Join("\n", synced.Notes) : null;
            if (portNotes != null) AppendLog("Sound card", portNotes.Replace("\n", " | "));
            string closing = string.Join("\n\n", new[] { outcome?.Message, portNotes, warnings.Length > 0 ? "Kiểm tra tín hiệu đo:\n" + warnings : null }.Where(t => t != null));
            if (closing.Length > 0)
            {
                var closingType = outcome?.Type == ModernMessageBox.MessageBoxType.Error ? ModernMessageBox.MessageBoxType.Error
                    : warnings.Length > 0 || outcome?.Type == ModernMessageBox.MessageBoxType.Warning ? ModernMessageBox.MessageBoxType.Warning : ModernMessageBox.MessageBoxType.Info;
                C360Popup(closing, closingType == ModernMessageBox.MessageBoxType.Info ? "Đã gửi kết quả" : warnings.Length > 0 ? "Kiểm tra tín hiệu đo" : "Kết quả Auto Test", closingType,
                    persistent: closingType != ModernMessageBox.MessageBoxType.Info);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_discardCurrentMeasurementRequested && _reviewedAutoTestCase == null)
                SetC360Verdict(_endpointLossReason != null ? "ĐÃ NGẮT — " + _endpointLossReason + " — CÓ THỂ TIẾP TỤC" : "ĐÃ NGẮT — CÓ THỂ TIẾP TỤC", Brushes.Gold);
            AppendLog("Auto Test", "Đã dừng" + (_currentRunningTestCase is { } stopped ? $" ở {stopped.Id} {stopped.Name}" : "")
                + (_endpointLossReason != null ? " vì " + _endpointLossReason : "") + "; kiểm tra trạng thái khôi phục BLE trước khi tiếp tục.");
            if (_endpointLossReason != null && !_discardCurrentMeasurementRequested)
                C360Popup($"Auto Test đã dừng{(_currentRunningTestCase is { } row ? $" ở {row.Id} · {row.Name}" : "")}: {_endpointLossReason}.\n"
                    + "Kiểm tra cáp/kết nối thiết bị rồi bấm TIẾP TỤC ĐO TỰ ĐỘNG; các bài đã xong được giữ lại.", "Mất thiết bị đo", ModernMessageBox.MessageBoxType.Error);
        }
        catch (Exception ex)
        {
            SetC360Verdict("CHƯA HOÀN TẤT", Brushes.Gold);
            AppendLog("Auto Test", (_currentRunningTestCase is { } failedAt ? $"Dừng ở {failedAt.Id} {failedAt.Name}: " : "") + ex.Message + $" ({ex.GetType().Name})");
            C360Popup((_currentRunningTestCase is { } stoppedAt ? $"Dừng ở {stoppedAt.Id} · {stoppedAt.Name}:\n" : "") + ex.Message, "Auto Test đã dừng", ModernMessageBox.MessageBoxType.Error);
        }
        finally
        {
            _c360PortSync = null;
            _audioEngine.StopAllAudio();
            if (activeBench != null && volumeRestore is { OriginalVolumeState.Count: > 0 })
            {
                try
                {
                    await volumeRestore.RestoreOriginalVolumeAsync(activeBench, CancellationToken.None);
                    AppendLog("Âm lượng", "Đã khôi phục âm lượng loa về mức trước khi đo.");
                }
                catch (Exception restoreEx)
                {
                    volumeRestore.BleRestorePending = true;
                    SetC360Verdict("CHƯA HOÀN TẤT — CHƯA KHÔI PHỤC ÂM LƯỢNG BLE", Brushes.Gold);
                    AppendLog("Âm lượng", "Chưa khôi phục được âm lượng gốc của loa: " + restoreEx.Message);
                }
            }
            bool finishedCleanly = completedSuite && !token.IsCancellationRequested && !_discardCurrentMeasurementRequested && !_autoTestInterrupted;
            // Config BluetoothAutoRemove keeps removing silently; otherwise ask the operator once every required stage has concluded (2026-11-19).
            bool removeDevice = productSuite && finishedCleanly && measuredAddress != null
                && (removeBluetooth && !isSingleRetest || hasBluetoothStage && _c360AutoTestResumeSession == null && await AskRemoveDeviceAsync(serial));
            await CompleteAutoTestBluetoothAsync(completedSuite && !token.IsCancellationRequested, removeDevice, measuredAddress, folder);
            _isExecutingAutoSuite = false; _currentRunningTestCase = null;
            if (_discardCurrentMeasurementRequested) DeleteDiscardedAutoTestArtifacts();
            _c360ModelCancellation = null; _runningC360Plan = null; _autoTestSessionFolder = null;
            if (completedSuite && _c360AutoTestResumeSession == null) (Application.Current.MainWindow as MainWindow)?.CompleteMeasurementIdentity();
            foreach (var row in _autoTestCases.Where(r => r.Status == "RUNNING"))
            { row.Status = "WAITING"; row.FreqStatus = "ĐÃ DỪNG"; row.ThdStatus = "CHƯA ĐO"; row.StatusBrush = Brushes.Gray; }
            for (int i = 0; i < buttons.Length; i++) buttons[i].IsEnabled = enabled[i];
            BtnStartAutoTest.IsEnabled = _activeInOutConfig != null;
            UpdateAutoTestStartButton();
            _c360ModelFinished = null; finished.TrySetResult();
            _ = Dispatcher.BeginInvoke(new Action(() => (Application.Current.MainWindow as MainWindow)?.UpdateSettingsToggleIcon(IsSetupVisible)));
        }
    }

    private sealed class C360UiProgress(AudioRouting owner, Action<C360ElectronicResult> apply) : IProgress<C360ElectronicResult>
    {
        public void Report(C360ElectronicResult result)
        {
            if (owner.Dispatcher.CheckAccess()) apply(result);
            else owner.Dispatcher.Invoke(() => apply(result));
        }
    }

    private void ClearC360ModelResults()
    {
        _c360AutoTestResumeSession = null;
        _lastC360ProductSession = null;
        ClearC360ModelResultsFor(null);
        UpdateAutoTestStartButton();
    }

    private void ClearC360ModelResultsFor(IReadOnlyList<C360ElectronicStage>? stages)
    {
        foreach (var row in _autoTestCases)
        {
            if (stages != null && !stages.Contains(C360ModelDefinition.Stage(row.Config))) continue;
            row.Status = "WAITING"; row.FreqStatus = row.ThdStatus = "ĐANG CHỜ";
            row.StatusBrush = row.FreqBrush = row.ThdBrush = AutoTestCaseItem.DefaultWaitingBrush; row.NoiseStatus = "CHƯA ĐO";
            row.DistortionText = "CHƯA CÓ DỮ LIỆU DISTORTION";
            row.ImpulseResult = null; row.Acquisition = null; row.AdditionalResults = Array.Empty<ConfiguredTestResult>();
            row.FrequencyShift = null; row.FeedbackGain = null; row.OptionalMeasurementSummary = "";
            row.ResponseLimits = null; row.ResponseAssessment = null;
            row.FrequencyLimits = new Dictionary<double, FrequencyLimitPoint>(); row.CriticalZones = Array.Empty<CriticalFrequencyZone>();
            row.MeasurementValues = Array.Empty<MeasurementValue>(); row.ResponseFrequencies = row.ResponseLevels = Array.Empty<double>();
            row.UnsmoothedResponse = new Dictionary<double, double>(); row.StandardCurve = new Dictionary<double, double>();
            row.UnsmoothedStandardCurve = new Dictionary<double, double>(); row.DistortionMeasurements = new Dictionary<double, ToneQualityMetrics>();
        }
    }

    private void SetC360Verdict(string text, Brush color)
    {
        LblVerdict.Text = text; LblVerdict.Foreground = color;
        BorderVerdict.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)); BorderVerdict.BorderBrush = color;
        TxtFailureDiagnosis.Visibility = Visibility.Collapsed;
    }

    private void ShowC360Review(AutoTestCaseItem row, double frequency)
    {
        TxtThdStatus.Text = "";
        if (row.Config.SpeakerStage != "MicEffects" && row.ImpulseResult is { } m)
        {
            double limit = row.Config.ThdLimitAt(frequency);
            UpdateSweepDistortionMetricsWithLimit(_testRunner.BuildSweepDistortionSummary(m, frequency), limit, finalVerdict: true);
            TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(row, _activeInOutConfig) + $"\n{row.ThdStatus}\n\n" + TxtSweepDistortionMetrics.Text;
            if (row.Config.IsOptional)
            {
                TxtThdVerdictBadge.Text = row.StatusDisplay;
                TxtThdVerdictBadge.Foreground = AutoTestCaseItem.OptionalBrush;
                BorderThdVerdictBadge.BorderBrush = AutoTestCaseItem.OptionalBrush;
            }
            return;
        }
        TxtSweepDistortionMetrics.Text = AudioQaRouteDetails.Header(row, _activeInOutConfig) + $"\n{row.DistortionText}";
        TxtThdVerdictBadge.Text = row.Config.IsOptional ? row.StatusDisplay
            : row.Config.SpeakerStage == "MicEffects" ? "ECHO / REVERB"
            : row.Config.SpeakerStage == "MicFrequencyShift" ? "DI TẦN MIC"
            : row.Status == "INCONCLUSIVE" ? "CHƯA KẾT LUẬN" : "CHƯA CÓ TÍN HIỆU";
        TxtThdVerdictBadge.Foreground = row.StatusBrush;
        BorderThdVerdictBadge.BorderBrush = row.StatusBrush;
    }

    private void ApplyC360Progress(C360ElectronicResult result, C360ModelPlan plan)
        => ApplyC360ProgressCore(result, plan, originalConfig: null, retestAuditNote: null);

    private void ApplyC360ProgressCore(C360ElectronicResult result, C360ModelPlan plan, TestConfig? originalConfig, string? retestAuditNote)
    {
        var config = plan.Configuration.Tests.Single(t => t.IsEnabled && C360ModelDefinition.Stage(t) == result.Stage);
        var row = _autoTestCases.Single(t => t.Id == config.id);
        row.Config = originalConfig ?? config;
        bool isOptional = row.Config.IsOptional;
        row.Status = result.Status;
        row.StatusBrush = result.Status == "RUNNING" ? Brushes.Gold
            : isOptional ? AutoTestCaseItem.OptionalBrush
            : C360VerdictPolicy.IsPassStatus(result.Status) ? Brushes.MediumSpringGreen
            : C360VerdictPolicy.IsFailStatus(result.Status) ? Brushes.Salmon
            : Brushes.Gold;
        if (result.Status == "RUNNING")
        {
            _currentRunningTestCase = row; row.FreqStatus = row.ThdStatus = "ĐANG ĐO";
            if (_reviewedAutoTestCase == null) SetC360Verdict(row.Id + " — " + row.Name + " — " + result.Summary, Brushes.Gold);
            return;
        }
        row.Acquisition = result.Acquisition;
        row.AdditionalResults = result.AdditionalResults;
        row.ImpulseResult = result.Measurement;
        row.FeedbackGain = result.FeedbackGain;
        row.OptionalMeasurementSummary = isOptional
            ? (result.FeedbackGain != null
                ? FeedbackGainDiagnostics.OptionalMeasurementLabel(result.FeedbackGain, config.FeedbackGain)
                : C360VerdictPolicy.IsUnconcludedStatus(result.Status) ? "chưa có số đo" : C360VerdictPolicy.FirstMeaningfulLine(result.Summary))
            : "";
        row.StandardCurve = result.Reference?.Curve ?? new Dictionary<double, double>();
        row.ResponseLimits = ResponseLimitSettings.Capture(config, ParseDoubleSafe(TxtFreqTolerance.Text, 3));
        row.CriticalZones = row.ResponseLimits.CriticalZones.ToArray();
        row.FrequencyLimits = row.StandardCurve.ToDictionary(p => p.Key, p => new FrequencyLimitPoint(p.Key, p.Value,
            p.Value - row.ResponseLimits.ToleranceDb, p.Value + row.ResponseLimits.ToleranceDb, false));
        row.ResponseAssessment = result.Measurement == null || result.Stage is C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift or C360ElectronicStage.AcousticRubBuzz or C360ElectronicStage.MicFeedbackGain ? null
            : new ResponseLimitPolicy(row.ResponseLimits, row.StandardCurve, row.FrequencyLimits)
                .Assess(C360Reference.Response(result), config.EffectiveLineOutlierPercent, row.StandardCurve.Keys);
        row.UnsmoothedStandardCurve = new Dictionary<double, double>(row.StandardCurve);
        row.FrequencyShift = result.FrequencyShift;
        row.FreqStatus = result.Measurement == null || result.Status == "NO_SIGNAL" ? "CHƯA CÓ TÍN HIỆU" : "ĐÃ ĐO dBFS";
        if (string.Equals(result.Status, "INCONCLUSIVE", StringComparison.OrdinalIgnoreCase))
            row.FreqStatus = result.MissingReference ? "CHƯA KẾT LUẬN — TỰ LẤY LINE CHUẨN CHƯA XONG" : "CHƯA KẾT LUẬN";
        else if (result.Status == "INVALID" && result.Reference == null && result.Stage is not (C360ElectronicStage.MicEffects or C360ElectronicStage.MicFrequencyShift)
            && result.Summary.Contains("Chưa có LINE CHUẨN — không kết luận", StringComparison.Ordinal))
            row.FreqStatus = "CHƯA CÓ LINE CHUẨN — KHÔNG KẾT LUẬN";
        if (result.Summary.StartsWith("ĐÃ CÓ LINE CHUẨN", StringComparison.Ordinal)) row.FreqStatus = "ĐÃ CÓ LINE CHUẨN";
        if (result.Summary.StartsWith("ĐÃ LƯU LINE CHUẨN", StringComparison.Ordinal)) row.FreqStatus = "ĐÃ LƯU LINE CHUẨN";
        if (result.Summary.Contains("ĐÃ TỰ LẤY LINE CHUẨN", StringComparison.Ordinal)) row.FreqStatus = "ĐÃ TỰ LẤY LINE CHUẨN";
        if (result.Stage == C360ElectronicStage.AcousticRubBuzz) row.FreqStatus = "RÈ · KHÔNG XÉT ĐÁP TUYẾN";
        row.FreqBrush = row.ThdBrush = row.StatusBrush;
        row.ThdStatus = isOptional && !string.IsNullOrWhiteSpace(row.OptionalMeasurementSummary) ? row.OptionalMeasurementSummary
            : result.Status == "NO_SIGNAL" ? "KHÔNG CÓ TÍN HIỆU THỬ" : result.Summary.Split('\n')[0];
        // IO008: the Rè verdict, not the mic pre-check sweep line (v1.5.6).
        if (result.Stage == C360ElectronicStage.AcousticRubBuzz && result.Status != "NO_SIGNAL") row.ThdStatus = RubBuzzVerdictLine(row);
        row.DistortionText = result.Summary;
        row.NoiseStatus = result.Measurement is { } measured ? $"SNR {measured.EstimatedSnrDb:F1} dB" : "CHƯA ĐO";
        if (result.Stage == C360ElectronicStage.MicEffects)
            row.AdditionalResults = new[] { new ConfiguredTestResult("Echo / Reverb", result.Status, result.Summary) };
        if (result.Stage == C360ElectronicStage.MicFrequencyShift)
        {
            row.FreqStatus = "KHÔNG ÁP DỤNG — MULTITONE";
            row.ThdStatus = result.FrequencyShift is { IsValid: true } measuredShift
                ? $"FBX bật {measuredShift.OnShiftHz:+0.00;-0.00;0.00} Hz · tắt {measuredShift.OffShiftHz:+0.00;-0.00;0.00} Hz · đổi {measuredShift.ShiftHz:+0.00;-0.00;0.00} Hz"
                    + (config.FrequencyShift?.ExpectedFbxChange == null && config.FrequencyShift?.ExpectedDetected == null ? " · CHƯA ĐẶT KỲ VỌNG FBX" : "")
                : "CHƯA XÁC ĐỊNH — kiểm tra tone thử";
            var values = new List<MeasurementValue> { new("Bài đo", row.Name), new("Ngõ phát / thu", config.PlaybackOut + " → " + config.RecordingIn),
                new("Âm lượng BLE / Windows", config.VolumeMapping) };
            if (result.FrequencyShift is { } noted && FrequencyShiftDiagnostics.ExpectationNote(noted, config.FrequencyShift ?? new()) is string note)
                values.Add(new("Ghi chú", note));
            values.Add(new("Kết quả", result.Summary));
            if (result.FrequencyShift is { } shift)
            {
                if (shift.IsValid)
                {
                    values.Add(new("Dịch tần FBX BẬT − TẮT", $"{shift.ShiftHz:+0.000;-0.000;0.000} ±{shift.UncertaintyHz:F2} Hz"));
                    values.Add(new("Di tần MIC khi FBX TẮT / BẬT", $"{shift.OffShiftHz:+0.000;-0.000;0.000} ±{shift.OffUncertaintyHz:F2} Hz / {shift.OnShiftHz:+0.000;-0.000;0.000} ±{shift.OnUncertaintyHz:F2} Hz"));
                    values.Add(new("Clock tương đối / sai số fit", $"{shift.RelativeClockPpm:F2} ppm / {shift.FitResidualHz:F3} Hz"));
                }
                foreach (var p in shift.Off.Concat(shift.On)) values.Add(new((shift.Off.Contains(p) ? "FBX tắt" : "FBX bật") + $" · {p.NominalHz:F0} Hz", $"{p.MeasuredHz:F3} Hz · ổn định {p.StabilityHz:F3} Hz · {p.LevelDbFs:F1} dBFS · nổi trên đỉnh nền cục bộ {p.NarrowBandSnrDb:F1} dB"));
                foreach (var p in shift.ToneLevelChanges)
                    values.Add(new($"Mức tone BẬT − TẮT · {p.FrequencyHz:0} Hz", $"{p.ChangeDb:+0.0;-0.0;0.0} dB ({p.OffDbFs:F1} → {p.OnDbFs:F1} dBFS)"));
                foreach (var a in shift.CaptureAttempts)
                    values.Add(new($"Thu FBX {(a.FbxEnabled ? "bật" : "tắt")} · lượt {a.Attempt}", a.Analysis.IsValid ? "Đủ chất lượng" : string.Join(" ", a.Analysis.Problems)));
                if (shift.IsValid && shift.Off.Count == 3 && shift.On.Count == 3)
                {
                    row.ResponseFrequencies = shift.Off.Select(p => p.NominalHz).ToArray();
                    row.ResponseLevels = shift.On.Select(p => p.MeasuredHz - p.NominalHz * (1 + shift.OnClockPpm / 1e6)).ToArray();
                    row.ResponseLevelUnit = "Hz";
                }
                var setup = plan.SetupFor(config);
                row.Acquisition ??= new(setup.LevelDbFs, setup.SampleRate, shift.SampleRate, false, setup.AnalogPlaybackChannel,
                    setup.CaptureChannel, setup.AnalogPlaybackId, setup.LineOutCaptureId, setup.ThdLimitPercent, DateTimeOffset.UtcNow);
            }
            if (retestAuditNote != null) values.Add(new("Đo lại", retestAuditNote));
            row.MeasurementValues = values;
            DisplayAutoTestResult(row);
            if (row.ResponseFrequencies.Length > 0 && _autoTestSessionFolder != null)
            {
                string path = Path.Combine(_autoTestSessionFolder, row.Id + "-frequency-shift.png");
                SaveServerGraphPng(PlotFreqResponse.Plot, path); UpsertAutoTestGraphPath(path);
            }
        }
        if (result.Stage == C360ElectronicStage.MicFeedbackGain)
        {
            row.FreqStatus = isOptional ? "TÙY CHỌN · GAIN TRƯỚC HÚ" : "GAIN TRƯỚC HÚ";
            var values = new List<MeasurementValue> { new("Bài đo", row.Name), new("Ngõ phát / thu", config.PlaybackOut + " → " + config.RecordingIn),
                new("Âm lượng BLE / Windows", config.VolumeMapping), new("Kết quả", result.Summary) };
            if (result.FeedbackGain is { } loop)
            {
                var fbSettings = config.FeedbackGain ?? new();
                values.Add(new("FBX TẮT · hú ở", loop.Off.Howled ? $"{loop.Off.HowlGainDb:+0.0;-0.0} dB · {loop.Off.HowlFrequencyHz:0} Hz" : "chưa hú"));
                values.Add(new("FBX BẬT · hú ở", loop.On.Howled ? $"{loop.On.HowlGainDb:+0.0;-0.0} dB · {loop.On.HowlFrequencyHz:0} Hz" : $"chưa hú tới {loop.MaximumGainDb:+0} dB"));
                values.Add(new("FBX TẮT · độ mạnh tiếng hú", FeedbackGainDiagnostics.Strength(loop.Off, fbSettings) is { } offHowl ? FeedbackGainDiagnostics.Describe(offHowl) : "chưa hú"));
                values.Add(new("FBX BẬT · độ mạnh tiếng hú", FeedbackGainDiagnostics.Strength(loop.On, fbSettings) is { } onHowl ? FeedbackGainDiagnostics.Describe(onHowl)
                    : FeedbackGainDiagnostics.HighestPeakToMedianDb(loop.On) is double highest ? $"chưa hú · đỉnh cao nhất {highest:0.0} dB trên nền (ngưỡng hú {fbSettings.HowlMarginDb:0} dB)" : "chưa có dữ liệu"));
                values.Add(new("FBX bật giảm đỉnh hú (cùng gain FBX tắt bắt đầu hú)", FeedbackGainDiagnostics.SuppressionAtOffHowlDb(loop, fbSettings) is double reduced ? $"{reduced:+0.0;-0.0} dB" : "chưa xác định"));
                values.Add(new("Thang độ mạnh", $"nổi trên nền < {fbSettings.HowlMarginDb + 10:0} dB hú nhẹ · < {fbSettings.HowlMarginDb + 20:0} dB hú vừa · còn lại hú mạnh"));
                values.Add(new("FBX thêm được", loop.AddedDb is double added ? (loop.AddedIsLowerBound ? "≥ " : "") + $"{added:+0.0;-0.0} dB" : "chưa xác định"));
                values.Add(new("Phương pháp", "Vòng mic đo → MIC IN → loa → mic đo, tăng gain từng bước tới khi hú (đỉnh hẹp vượt nền). Không phải chuẩn IEC/AES; so cùng vòng khi FBX tắt/bật."));
            }
            if (retestAuditNote != null) values.Add(new("Đo lại", retestAuditNote));
            row.MeasurementValues = values;
            DisplayAutoTestResult(row);
            if (_autoTestSessionFolder != null)
            {
                string path = Path.Combine(_autoTestSessionFolder, "FRA-" + row.Id + ".png");
                try
                {
                    SaveC360FeedbackGainGraphPng(row, path);
                    UpsertAutoTestGraphPath(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { AppendLog("Error", "Chưa lưu được ảnh gain trước hú: " + ex.Message); }
            }
        }
        if (result.Measurement is { } m)
        {
            var setup = plan.SetupFor(config);
            var response = m.FrequencyResponse;
            row.ResponseFrequencies = response.Select(p => p.FrequencyHz).ToArray();
            row.ResponseLevels = response.Select(p => p.LevelDb + m.ExcitationRmsDbFs).ToArray();
            row.ResponseLevelUnit = "dBFS";
            row.UnsmoothedResponse = row.ResponseFrequencies.Zip(row.ResponseLevels).ToDictionary(p => p.First, p => p.Second);
            row.Acquisition ??= new(setup.PlaybackLevel(result.Stage), setup.SampleRate, m.SampleRate, false,
                setup.AnalogPlaybackChannel, setup.CaptureChannel, setup.PlaybackId(result.Stage), setup.LineOutCaptureId, setup.ThdLimitPercent, DateTimeOffset.UtcNow);
            var values = new List<MeasurementValue> { new("Bài đo", row.Name), new("Ngõ phát", config.PlaybackOut), new("Ngõ thu", config.RecordingIn),
                new("Âm lượng BLE / Windows", config.VolumeMapping),
                new("Mức phát", $"{setup.PlaybackLevel(result.Stage):F1} dBFS peak"), new("Sample rate phát / thu", $"{setup.SampleRate} / {m.SampleRate} Hz"),
                new("RMS / noise floor", $"{m.SignalLevelDbFs:F2} / {m.NoiseFloorDbFs:F2} dBFS"), new("SNR", $"{m.EstimatedSnrDb:F2} dB"),
                new("Kết quả", result.Summary), new("Hợp lệ", m.Validity), new("Clipping", m.IsClipped ? "CÓ" : "Không"),
                new("Điểm LINE ngoài biên chuẩn", row.ResponseAssessment?.Describe() ?? "Không áp dụng cho kỳ vọng effect/FBX."),
                new("Khoảng xét hõm", row.ResponseLimits.DescribeWindows()) };
            if (retestAuditNote != null) values.Add(new("Đo lại", retestAuditNote));
            row.MeasurementValues = values;
            DisplayAutoTestResult(row); UpdateSampleRateWarning(); SaveFailureScreenshots(row.Id, forceAlways: true);
        }
        if (result.Measurement == null && result.Stage is not (C360ElectronicStage.MicFrequencyShift or C360ElectronicStage.MicFeedbackGain))
        {
            if (retestAuditNote != null)
                row.MeasurementValues = row.MeasurementValues.Where(v => v.ThongSo != "Đo lại").Append(new MeasurementValue("Đo lại", retestAuditNote)).ToArray();
            DisplayAutoTestResult(row);
        }
        if (result.Stage == C360ElectronicStage.AcousticRubBuzz) SaveRubBuzzChirpGraph(row, result);
        AppendLog("Auto Test", row.Name + ": " + result.Summary);
    }

    /// <summary>IO008's QA image is one chirp graph (owner 2026-11-18), replacing the sweep pre-check screenshot of the same step.</summary>
    private void SaveRubBuzzChirpGraph(AutoTestCaseItem row, C360ElectronicResult result)
    {
        if (_autoTestSessionFolder == null) return;
        var chirp = result.AdditionalResults.Select(r => r.ChirpRubBuzz).FirstOrDefault(c => c != null);
        string name = "FRA-" + row.Id + ".png";
        string path = _autoTestGraphPaths.LastOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(_autoTestSessionFolder, name);
        try
        {
            SaveServerGraphPng(RubBuzzQaGraph.Chirp(chirp, $"{row.Id} · {row.Name} · Chirp rè",
                AudioQaRouteDetails.Route(row, _activeInOutConfig)), path);
            UpsertAutoTestGraphPath(path);
            AppendLog("Export", "Đã lưu ảnh chirp rè cho " + row.Id + ": " + name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { AppendLog("Error", "Chưa lưu được ảnh chirp rè: " + ex.Message); }
    }
}
