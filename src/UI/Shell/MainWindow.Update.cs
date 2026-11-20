using System.Diagnostics;
using System.Threading;
using System.Windows;
using SoncaAudioInspector.Update;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _updateChecked;
    private bool _updateDeferred;
    private Task? _updateTask;
    private Window? _updatePromptOwner;
    public UpdateCheckState UpdateState { get; } = new();
    private void InitializeUpdateUi()
    {
        UpdateState.CheckRequested = owner => CheckForUpdatesAsync(true, owner);
        UpdateState.CatalogRequested = async token => { using var client = new UpdateClient(); return await client.GetCatalogAsync(token); };
        UpdateState.InstallVersionRequested = InstallChosenVersionAsync;
    }
    // A held publish gate also protects background model synchronization from restart.
    private bool UpdateBusy() => _modelEditorOpen || IsStandardMeasurementBusy || _audioConfigPublishGate?.CurrentCount == 0
        || _audioRoutingView?.IsUpdateMeasurementBusy == true || _audioEngine?.IsAnyPlaybackActive == true;

    private async Task WaitForUpdateIdleAsync(CancellationToken token)
    {
        while (UpdateBusy() || !(_updatePromptOwner ?? this).IsActive)
        {
            if (_isLoggingOut || !(_updatePromptOwner ?? this).IsVisible) throw new OperationCanceledException();
            await Task.Delay(750, token);
        }
        token.ThrowIfCancellationRequested();
        if (_isLoggingOut || !IsVisible) throw new OperationCanceledException();
    }
    private Task CheckForUpdatesAsync(bool manual = false, Window? owner = null)
    {
        if (!manual && Environment.GetEnvironmentVariable("SONCA_UPDATE_DISABLE_AUTO_CHECK") == "1") return Task.CompletedTask;
        if (manual) _updatePromptOwner = owner ?? this;
        if (_updateTask is { IsCompleted: false }) return _updateTask;
        if (_updateChecked && !manual) return Task.CompletedTask;
        _updateChecked = true;
        return _updateTask = RunUpdateCheckAsync();
    }
    // Single-parameter signature kept for the offline acceptance harness (invoked by reflection).
    internal static ModernMessageBox CreateUpdatePrompt(UpdatePlan plan) => BuildUpdatePrompt(plan, chosenVersion: false);
    internal static ModernMessageBox CreateChosenVersionPrompt(UpdatePlan plan) => BuildUpdatePrompt(plan, chosenVersion: true);
    private static ModernMessageBox BuildUpdatePrompt(UpdatePlan plan, bool chosenVersion)
    {
        var target = UpdateManifest.ParseVersion(plan.Manifest.Version);
        // Only a station's own choice can go down: the automatic check offers strictly higher versions.
        bool downgrade = chosenVersion && target < AppVersion.Current;
        string message = (downgrade ? "HẠ CẤP xuống: " : chosenVersion ? "Cài phiên bản: " : "Bản mới: ") + plan.Manifest.DisplayVersion
            + $"\nBản hiện tại: {AppVersion.DisplayWithChannel}\n{plan.Files.Count} file · {plan.TotalBytes / 1048576d:0.00} MB"
            + (plan.Manifest.IsTemporary ? "\n\nĐây là BẢN TẠM: gửi lên từ máy khác và đã được ADMIN duyệt trên server; chỉ máy kênh Beta nhận để chạy thử trước."
                + " Nếu không ổn, vào Phiên bản → Cài phiên bản khác để hạ về bản ổn định."
                : plan.Manifest.IsBeta ? "\n\nĐây là bản BETA, chưa kiểm tra kỹ. Nếu không ổn, vào Phiên bản → Cài phiên bản khác để hạ về bản ổn định." : "")
            + (downgrade ? "\n\nDữ liệu trạm (cấu hình, hiệu chuẩn, line chuẩn, fail data) giữ nguyên. Bản cũ có thể chưa có chức năng chọn kênh/hạ cấp; "
                + "muốn lên lại thì dùng Kiểm tra cập nhật hoặc gói portable trên trang lịch sử." : "");
        var dialog = ModernMessageBox.CreateConfirmation(message, downgrade ? "Hạ cấp Sonca Audio Inspector" : "Cập nhật Sonca Audio Inspector",
            downgrade ? "Hạ cấp ngay" : "Cập nhật ngay", "Để sau");
        var notes = new ReleaseNotesView(); notes.SetNotes(plan.Manifest.FormatFeatures(), plan.Manifest.FeatureDetails());
        dialog.Width = 680; dialog.SetAdditionalContent(notes); return dialog;
    }
    private async Task RunUpdateCheckAsync()
    {
        var token = _updateCancellation.Token;
        string directory = AppContext.BaseDirectory;
        UpdateClient client;
        UpdatePlan plan;
        try
        {
            UpdateState.SetStatus("Đang kiểm" + (UpdateState.Channel == UpdateChannelKind.Beta ? " (kênh beta)" : ""));
            await Task.Run(() => UpdateInstaller.Cleanup(directory), token);
            client = new UpdateClient();
            try
            {
                var manifest = await client.CheckAsync(AppVersion.Current, UpdateState.Channel, token);
                if (manifest == null) { UpdateState.SetStatus("Đang dùng bản mới nhất của kênh " + (UpdateState.Channel == UpdateChannelKind.Beta ? "beta" : "ổn định")); client.Dispose(); return; }
                UpdateState.SetStatus("Có bản " + manifest.DisplayVersion + (manifest.IsTemporary ? " (bản tạm đã duyệt)" : ""));
                plan = await Task.Run(() => UpdateClient.CalculateChanges(manifest, directory), token);
            }
            catch { client.Dispose(); throw; }
        }
        catch (Exception ex) { UpdateState.SetStatus("Lỗi: " + ex.Message); Trace.WriteLine("Kiểm tra cập nhật: " + ex); return; }
        using (client)
        {
            if (_updateDeferred) return;
            await PromptAndInstallAsync(client, plan, chosenVersion: false, token);
        }
    }
    /// <summary>"Cài phiên bản khác": any archived signed version, including a downgrade from a beta that is not OK.</summary>
    private async Task InstallChosenVersionAsync(Window owner, string version)
    {
        if (_updateTask is { IsCompleted: false }) throw new InvalidOperationException("Đang kiểm/cài cập nhật; chờ xong rồi thử lại.");
        var token = _updateCancellation.Token;
        string directory = AppContext.BaseDirectory;
        _updatePromptOwner = owner;
        var task = Run();
        _updateTask = task;
        await task;
        async Task Run()
        {
            UpdateClient client = new();
            UpdatePlan plan;
            try
            {
                UpdateState.SetStatus("Đang đọc phiên bản " + version);
                await Task.Run(() => UpdateInstaller.Cleanup(directory), token);
                var manifest = await client.GetVersionAsync(version, token);
                plan = await Task.Run(() => UpdateClient.CalculateChanges(manifest, directory), token);
                if (plan.Files.Count == 0) { UpdateState.SetStatus("Đang chạy đúng các file của " + manifest.DisplayVersion); client.Dispose(); return; }
            }
            catch (Exception ex) { client.Dispose(); UpdateState.SetStatus("Lỗi: " + ex.Message); Trace.WriteLine("Cài phiên bản: " + ex); throw; }
            using (client) await PromptAndInstallAsync(client, plan, chosenVersion: true, token);
        }
    }
    private async Task PromptAndInstallAsync(UpdateClient client, UpdatePlan plan, bool chosenVersion, CancellationToken token)
    {
        string directory = AppContext.BaseDirectory;
        try
        {
            await WaitForUpdateIdleAsync(token);
            var prompt = chosenVersion ? CreateChosenVersionPrompt(plan) : CreateUpdatePrompt(plan);
            if ((_updatePromptOwner ?? this).IsVisible) prompt.Owner = _updatePromptOwner ?? this;
            prompt.ShowDialog();
            if (!prompt.Accepted)
            { if (!chosenVersion) _updateDeferred = true; return; }
            await WaitForUpdateIdleAsync(token);
            var progress = new Progress<string>(status => TxtUpdateStatus.Text = status);
            await client.DownloadAsync(plan, directory, progress, token);
            TxtUpdateStatus.Text = "Đã tải xong. Chờ trạm rảnh để cập nhật…";
            await WaitForUpdateIdleAsync(token);
            // No await between the last busy check, replacement and restart. UI actions cannot start a measurement here.
            IsEnabled = false;
            if (_updatePromptOwner != null) _updatePromptOwner.IsEnabled = false;
            try
            {
                UpdateInstaller.Apply(plan, directory, AppVersion.Current, UpdateBusy);
                UpdateRestart.Start();
                _isLoggingOut = true;
                Application.Current.Shutdown();
            }
            finally { IsEnabled = true; if (_updatePromptOwner != null) _updatePromptOwner.IsEnabled = true; }
        }
        catch (OperationCanceledException) { Trace.WriteLine("Đã hủy cập nhật trong phiên này."); }
        catch (Exception ex)
        {
            UpdateState.SetStatus("Lỗi: " + ex.Message);
            Trace.WriteLine("Cập nhật thất bại: " + ex);
            if (!_isLoggingOut && IsVisible)
                ModernMessageBox.Show(this, "Chưa cập nhật được: " + ex.Message, "Cập nhật", ModernMessageBox.MessageBoxType.Error);
        }
        finally { TxtUpdateStatus.Text = ""; }
    }
}
