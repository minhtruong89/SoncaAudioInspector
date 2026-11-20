using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector;

/// <summary>Completed measurements waiting for durable QA storage, independent of measurement resume.</summary>
public sealed class CompletedMeasurementPendingQaSession
{
    public string Model { get; init; } = "";
    public string ConfigurationKey { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Folder { get; init; } = "";
    public bool Passed { get; init; }
    public bool DevicesReadyForUpload { get; init; }
    public AutoTestCaseItem[] Cases { get; init; } = [];
    public string[] GraphPaths { get; set; } = [];
    public string? LastQaError { get; set; }
    public MeasurementIdentity? Identity { get; set; }
    public const string FileName = "qa-completion-pending.json";
    public string StateDescription => "Đo xong, QA chưa xong" + (LastQaError == null ? "" : ": " + LastQaError);
    private sealed record SavedCase(AutoTestCaseItem Case, StandardAcousticResult? Impulse,
        FrequencyShiftResult? Shift, RubBuzzMetrics? RubBuzz, FrequencySpectrum? Spectrum, IReadOnlyList<MeasurementValue> Values,
        PinkRubBuzzRun? Pink = null, ChirpRubBuzzRun? Chirp = null, FeedbackGainResult? FeedbackGain = null);
    private sealed record SavedSession(string Model, string ConfigurationKey, string Serial, string Folder, bool Passed,
        bool DevicesReadyForUpload, SavedCase[] Cases, string[] GraphPaths, string? LastQaError, MeasurementIdentity? Identity = null);
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (typeof(Brush).IsAssignableFrom(info.Properties[i].PropertyType)) info.Properties.RemoveAt(i);
        });
        return new() { TypeInfoResolver = resolver, IgnoreReadOnlyProperties = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    }
    /// <summary>Owner 2026-11-19 (A5): strips multi-hundred-MB complex transfer arrays that QA upload/graph repair never reads.
    /// Eliminates the ~43 s synchronous JSON serialization/disk freeze at end of a 9-stage C360 Auto Test.</summary>
    public static StandardAcousticResult? TrimForQaSnapshot(StandardAcousticResult? impulse, bool keepImpulsePlot)
    {
        if (impulse == null) return null;
        return impulse with
        {
            AlignedTransferFunction = Array.Empty<System.Numerics.Complex>(),
            HarmonicTransferFunctions = Array.Empty<System.Numerics.Complex[]>(),
            Waterfall = Array.Empty<WaterfallPoint>(),
            UngatedFrequencyResponse = Array.Empty<FrequencyResponsePoint>(),
            ImpulsePlot = keepImpulsePlot ? impulse.ImpulsePlot : null,
            ImpulseResponse = keepImpulsePlot ? impulse.ImpulseResponse : Array.Empty<TimeDomainPoint>(),
            StepResponse = Array.Empty<TimeDomainPoint>(),
            EnergyTimeCurve = keepImpulsePlot ? impulse.EnergyTimeCurve : Array.Empty<TimeDomainPoint>(),
            EnergyDecayCurve = keepImpulsePlot ? impulse.EnergyDecayCurve : Array.Empty<DecayCurvePoint>()
        };
    }
    // A serialization round trip freezes mutable config/arrays and retains diagnostic snapshots for graph repair after restart.
    public static CompletedMeasurementPendingQaSession Create(string model, string configurationKey, string serial, string folder,
        bool passed, bool devicesReady, IEnumerable<AutoTestCaseItem> cases, IEnumerable<string> graphs)
    {
        var rows = cases.ToArray();
        ServerEngine.ValidateAndMapQaSteps(rows.Select(r => new ServerEngine.AudioQaStepResult(r.Name, r.Status, "", r.Config?.IsOptional == true,
            r.Config?.IsOptional == true ? r.OptionalMeasurementSummary : C360VerdictPolicy.ShortUnconcludedReason(r))).ToArray(), passed);
        var snapshot = JsonSerializer.Deserialize<SavedCase[]>(JsonSerializer.Serialize(rows.Select(SaveCase).ToArray(), Json), Json)!;
        var paths = graphs.ToArray();
        return new() { Model = model, ConfigurationKey = configurationKey, Serial = serial, Folder = folder,
            Passed = passed, DevicesReadyForUpload = devicesReady, Cases = snapshot.Select(RestoreCase).ToArray(),
            GraphPaths = rows.Select(r => paths.LastOrDefault(p => string.Equals(Path.GetFileName(p), "FRA-" + r.Id + ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(p), r.Id + "-frequency-shift.png", StringComparison.OrdinalIgnoreCase))
                ?? Path.Combine(folder, r.Config?.SpeakerStage == "MicFrequencyShift" ? r.Id + "-frequency-shift.png" : "FRA-" + r.Id + ".png")).ToArray() };
    }
    public static Task<CompletedMeasurementPendingQaSession> CreateAsync(string model, string configurationKey, string serial, string folder,
        bool passed, bool devicesReady, IEnumerable<AutoTestCaseItem> cases, IEnumerable<string> graphs)
    {
        var rows = cases.ToArray();
        var paths = graphs.ToArray();
        return Task.Run(() => Create(model, configurationKey, serial, folder, passed, devicesReady, rows, paths));
    }
    private static SavedCase SaveCase(AutoTestCaseItem row) => new(row,
        TrimForQaSnapshot(row.ImpulseResult, keepImpulsePlot: row.Config?.SpeakerStage == "MicEffects"),
        row.FrequencyShift, row.RubBuzzMetrics, row.RubBuzzSpectrum, row.MeasurementValues,
        AudioRouting.RubBuzzOf(row).Pink, AudioRouting.RubBuzzOf(row).Chirp, row.FeedbackGain);
    private static AutoTestCaseItem RestoreCase(SavedCase saved)
    {
        var row = saved.Case; row.ImpulseResult = saved.Impulse; row.FrequencyShift = saved.Shift;
        row.RubBuzzMetrics = saved.RubBuzz; row.RubBuzzSpectrum = saved.Spectrum; row.MeasurementValues = saved.Values;
        row.FeedbackGain = saved.FeedbackGain;
        // IO008 Pink/Chirp runs are [JsonIgnore] on the result; restore them so the chirp QA image can be redrawn after a restart.
        if (saved.Pink != null || saved.Chirp != null)
            row.AdditionalResults = row.AdditionalResults.Count == 0
                ? [new ConfiguredTestResult("Rè/rung · Pink + Chirp", row.Status, "") { PinkRubBuzz = saved.Pink, ChirpRubBuzz = saved.Chirp }]
                : row.AdditionalResults.Select((r, i) => i == 0 ? r with { PinkRubBuzz = saved.Pink, ChirpRubBuzz = saved.Chirp } : r).ToArray();
        row.StatusBrush = row.Config?.IsOptional == true ? AutoTestCaseItem.OptionalBrush
            : C360VerdictPolicy.IsPassStatus(row.Status) ? Brushes.MediumSpringGreen
            : C360VerdictPolicy.IsFailStatus(row.Status) ? Brushes.Salmon
            : Brushes.Gold;
        return row;
    }
    public void Persist()
    {
        ResultPersistencePolicy.Execute(() =>
        {
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteAllText(Path.Combine(Folder, FileName), JsonSerializer.Serialize(new SavedSession(Model, ConfigurationKey, Serial,
            Folder, Passed, DevicesReadyForUpload, Cases.Select(SaveCase).ToArray(), GraphPaths, LastQaError, Identity), Json), keepBackup: false);

        });
    }
    public Task PersistAsync() => Task.Run(Persist);
    public static CompletedMeasurementPendingQaSession Load(string path)
    {
        var saved = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(path), Json) ?? throw new IOException("Thiếu trạng thái QA.");
        if (!string.Equals(Path.GetFullPath(saved.Folder), Path.GetDirectoryName(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Thư mục phiên QA không khớp file trạng thái.");
        var session = new CompletedMeasurementPendingQaSession { Model = saved.Model, ConfigurationKey = saved.ConfigurationKey,
            Serial = saved.Serial, Folder = saved.Folder, Passed = saved.Passed, DevicesReadyForUpload = saved.DevicesReadyForUpload,
            Cases = saved.Cases.Select(RestoreCase).ToArray(), GraphPaths = saved.GraphPaths, LastQaError = saved.LastQaError, Identity = saved.Identity };
        ServerEngine.ValidateAndMapQaSteps(session.Cases.Select(r => new ServerEngine.AudioQaStepResult(r.Name, r.Status, "", r.Config?.IsOptional == true,
            r.Config?.IsOptional == true ? r.OptionalMeasurementSummary : C360VerdictPolicy.ShortUnconcludedReason(r))).ToArray(), session.Passed);
        return session;
    }
    public void Discard() { string path = Path.Combine(Folder, FileName); if (File.Exists(path)) File.Delete(path); }
    /// <summary>Completed sessions live in &lt;root&gt;/&lt;yyyyMMdd&gt;/&lt;serial&gt;_&lt;time&gt;/; only those folders are checked, never capture files.</summary>
    public static string[] FindPendingFiles(string root, string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || !Directory.Exists(root)) return [];
        string token = ProductMeasurementContext.FileToken(serial);
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        return Directory.EnumerateDirectories(root, "*", options)
            .SelectMany(day => Directory.EnumerateDirectories(day, token + "_*", options))
            .Select(folder => Path.Combine(folder, FileName)).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }
    public async Task<bool> TryCompleteAsync(Action<AutoTestCaseItem, string> renderMissingGraph, Func<Task<bool>> saveOrUpload)
    {
        return await ResultPersistencePolicy.SubmitAsync(async () =>
        {
        try
        {
            await PersistAsync();
            if (GraphPaths.Length != Cases.Length) throw new IOException("Số ảnh QA không khớp số bài đã đo.");
            for (int i = 0; i < Cases.Length; i++)
            {
                if (File.Exists(GraphPaths[i])) continue;
                var row = Cases[i];
                bool allowPlaceholder = row.Config?.IsOptional == true || !C360VerdictPolicy.IsPassStatus(row.Status) && !C360VerdictPolicy.IsFailStatus(row.Status);
                if (!allowPlaceholder && row.ResponseFrequencies.Length == 0 && row.FrequencyShift == null && row.RubBuzzSpectrum == null
                    && row.ImpulseResult == null && AudioRouting.RubBuzzOf(row).Chirp == null && row.FeedbackGain == null)
                    throw new IOException($"{row.Id}: chưa có dữ liệu để dựng lại ảnh QA.");
                renderMissingGraph(row, GraphPaths[i]);
                if (!File.Exists(GraphPaths[i])) throw new IOException($"{row.Id}: chưa tạo được ảnh QA.");
            }
            if (!await saveOrUpload()) throw new IOException(ServerEngine.LastError ?? "Chưa lưu được phiếu QA chờ gửi.");
            await Task.Run(Discard);
            return true;
        }
        catch (Exception ex)
        {
            LastQaError = ex.Message;
            try { await PersistAsync(); } catch (Exception disk) { LastQaError += "; chưa ghi được trạng thái trên đĩa: " + disk.Message; }
            return false;
        }

        });
    }
}

public partial class AudioRouting
{
    private CompletedMeasurementPendingQaSession? _pendingQaCompletionSession;
    private bool _qaCompletionInProgress;
    internal Func<CompletedMeasurementPendingQaSession, Task<bool>>? QaCompletionSubmitOverride { get; set; }
    private void ClearPendingQaCompletion()
    {
        var saved = _pendingQaCompletionSession; _pendingQaCompletionSession = null;
        try { saved?.Discard(); } catch (Exception ex) { AppendLog("QA", "Chưa xóa được trạng thái QA: " + ex.Message); }
    }
    private void RememberCompletedMeasurement(string serial, string model, string? folder, bool passed, bool ready, bool replaceExisting = false)
    {
        ResultPersistencePolicy.Execute(() =>
        {
        if (!replaceExisting && _pendingQaCompletionSession is { } previous && previous.Serial == serial && previous.Model == model
            && previous.ConfigurationKey == AutoTestConfigurationKey()) return;
        if (folder == null) throw new IOException("Chưa có thư mục lưu phiên đo đã hoàn tất.");
        _pendingQaCompletionSession = CompletedMeasurementPendingQaSession.Create(model, AutoTestConfigurationKey(), serial,
            folder, passed, ready, _autoTestCases, _autoTestGraphPaths);
        ApplyPendingQaIdentity(_pendingQaCompletionSession, serial, model);
        try { _pendingQaCompletionSession.Persist(); }
        catch (Exception ex) { _pendingQaCompletionSession.LastQaError = ex.Message; AppendLog("QA", _pendingQaCompletionSession.StateDescription); }

        });
    }
    /// <summary>Owner 2026-11-19 (A5): builds and persists the QA snapshot off the UI thread so the window never freezes.</summary>
    private async Task RememberCompletedMeasurementAsync(string serial, string model, string? folder, bool passed, bool ready, bool replaceExisting = false)
    {
        if (!ResultPersistencePolicy.CanPersist) return;
        if (!replaceExisting && _pendingQaCompletionSession is { } previous && previous.Serial == serial && previous.Model == model
            && previous.ConfigurationKey == AutoTestConfigurationKey()) return;
        if (folder == null) throw new IOException("Chưa có thư mục lưu phiên đo đã hoàn tất.");
        string configKey = AutoTestConfigurationKey();
        var identity = _measurementIdentity ?? (Application.Current.MainWindow as MainWindow)?.MeasurementContext.Snapshot();
        var created = await CompletedMeasurementPendingQaSession.CreateAsync(model, configKey, serial, folder, passed, ready, _autoTestCases, _autoTestGraphPaths);
        ApplyPendingQaIdentity(created, serial, model, identity);
        _pendingQaCompletionSession = created;
        try { await created.PersistAsync(); }
        catch (Exception ex) { created.LastQaError = ex.Message; AppendLog("QA", created.StateDescription); }
    }
    private void ApplyPendingQaIdentity(CompletedMeasurementPendingQaSession session, string serial, string model, MeasurementIdentity? snapshot = null)
    {
        session.Identity = snapshot ?? _measurementIdentity ?? (Application.Current.MainWindow as MainWindow)?.MeasurementContext.Snapshot();
        if (ResultPersistencePolicy.IsTrial && session.Identity?.IsTrial != true)
        {
            var trial = SessionModelConfigurations.Current.Find(model) ?? throw new InvalidOperationException("Không tìm thấy cấu hình thử của phiên đo.");
            var existing = session.Identity;
            session.Identity = new(serial, model, existing?.Source ?? SerialSource.Auto, existing?.QaVerdict ?? "NONE",
                existing?.QaConfirmedAt, existing?.QaConfirmedBy,
                SessionModelConfigurations.Current.ServerTrialRevision(model), null, true, JsonSerializer.Serialize(trial, ModelConfigEditorWindow.JsonOptions),
                existing?.OperatorNote);
        }
    }
    private async Task UploadCompletedAutoTestAsync(string serial, string model, string? folder, bool passed, bool ready, bool replaceExisting = false)
    {
        await RememberCompletedMeasurementAsync(serial, model, folder, passed, ready, replaceExisting);
        await RetryPendingQaCompletionAsync();
    }
    public async Task<bool> RetryPendingQaCompletionAsync()
    {
        return await ResultPersistencePolicy.SubmitAsync(async () =>
        {
        var saved = _pendingQaCompletionSession;
        if (saved == null || _qaCompletionInProgress) return false;
        if (saved.ConfigurationKey != AutoTestConfigurationKey()) return false;
        using var destination = ResultPersistencePolicy.BeginTrial(saved.Identity?.IsTrial == true);
        _qaCompletionInProgress = true;
        try
        {
            bool complete = await saved.TryCompleteAsync((row, path) =>
            {
                if (row.Config.SpeakerStage == "MicFeedbackGain") SaveC360FeedbackGainGraphPng(row, path);
                else if (row.Config.RubBuzzTestFreq is double frequency && row.RubBuzzSpectrum != null)
                { DisplayAutoTestResult(row); SaveServerGraphPng((frequency < 500 ? PlotThdFft : frequency < 2500 ? PlotThdFft1k : PlotThdFft4k).Plot, path); }
                else if (RubBuzzOf(row).Chirp is { } chirp) SaveServerGraphPng(RubBuzzQaGraph.Chirp(chirp, $"{row.Id} · {row.Name} · Chirp rè"), path);
                else if (row.FrequencyShift != null && row.ResponseFrequencies.Length > 0) { DisplayAutoTestResult(row); SaveServerGraphPng(PlotFreqResponse.Plot, path); }
                else if (row.ImpulseResult != null && row.Config.SpeakerStage == "MicEffects") new ImpulseAnalysisView(row.ImpulseResult).SaveGraph(path);
                else if (row.ResponseFrequencies.Length > 0) { DisplayAutoTestResult(row); SaveFullResponseGraphPng(path); }
                else SaveStatusPlaceholderGraphPng(row, path);
            }, () => ReferenceEquals(saved, _pendingQaCompletionSession)
                ? QaCompletionSubmitOverride?.Invoke(saved) ?? UploadCompletedAutoTestCoreAsync(saved) : Task.FromResult(false));
            if (!ReferenceEquals(saved, _pendingQaCompletionSession))
            {
                // A stop/model change may discard the session while submission is awaited.
                // The retry's error handler must not resurrect its completion marker afterwards.
                saved.Discard();
                return complete;
            }
            if (complete) _pendingQaCompletionSession = null;
            else { LblVerdict.Text = saved.StateDescription; LblVerdict.Foreground = Brushes.Gold; AppendLog("QA", saved.StateDescription); }
            UpdateAutoTestStartButton();
            return complete;
        }
        finally { _qaCompletionInProgress = false; }

        });
    }
    private async Task<bool> TryRetryQaForCurrentSelectionAsync()
    {
        return await ResultPersistencePolicy.SubmitAsync(async () =>
        {
        if (_qaCompletionInProgress) return true;
        string model = (Application.Current.MainWindow as MainWindow)?.ComboModels.SelectedItem?.ToString() ?? "";
        string serial = (Application.Current.MainWindow as MainWindow)?.TxtSerialNumber?.Text?.Trim() ?? TxtProductSerial.Text.Trim();
        if (_pendingQaCompletionSession == null && Directory.Exists(LocalQaStorage.Root))
        {
            string[] candidates = [];
            try { candidates = CompletedMeasurementPendingQaSession.FindPendingFiles(LocalQaStorage.Root, serial); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog("QA", "Không tìm được phiên QA: " + ex.Message); }
            foreach (string path in candidates)
            {
                try
                {
                    var saved = CompletedMeasurementPendingQaSession.Load(path);
                    if (saved.Model == model && saved.ConfigurationKey == AutoTestConfigurationKey() && saved.Serial == serial)
                    { _pendingQaCompletionSession = saved; break; }
                }
                catch (Exception ex) { AppendLog("QA", "Không đọc được phiên QA: " + ex.Message); }
            }
        }
        if (_pendingQaCompletionSession is not { } pending) return false;
        if (pending.Model != model || pending.ConfigurationKey != AutoTestConfigurationKey() || serial.Length > 0 && pending.Serial != serial) return false;
        await RetryPendingQaCompletionAsync();
        return true;

        });
    }
}
