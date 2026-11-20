using System.Windows;
using System.Windows.Media;

namespace SoncaAudioInspector;

public partial class MainWindow
{
    // SYS002 supplies the signed base configuration. After login, verified ACTIVE revisions from the web
    // replace each model's complete test list, including added/deleted tests and enable flags.
    private bool _modelEditorOpen;
    private bool _syncingComboModels;
    // Kept as an update/restart gate for any future background configuration work (see UpdateBusy).
    private readonly SemaphoreSlim _audioConfigPublishGate = new(1, 1);
    internal void SyncComboModelsWithCheckingConfig()
    {
        string? selected = ComboModels.SelectedItem?.ToString();
        var desired = _checkingConfig?.models.Select(m => m.model).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
        string? matched = selected != null ? FindModelConfig(_checkingConfig?.models, selected)?.model : null;
        bool sameOrder = ComboModels.Items.Count == desired.Count
            && Enumerable.Range(0, desired.Count).All(i => Equals(ComboModels.Items[i]?.ToString(), desired[i]));
        if (sameOrder && string.Equals(selected, matched, StringComparison.Ordinal)) return;

        _syncingComboModels = true;
        try
        {
            ComboModels.Items.Clear();
            foreach (string name in desired) ComboModels.Items.Add(name);
            if (matched != null) ComboModels.SelectedItem = matched;
            else ComboModels.SelectedIndex = -1;
        }
        finally { _syncingComboModels = false; }
    }
    internal async Task RefreshActiveAudioConfigurationsAsync(TimeSpan? timeout = null)
    {
        try
        {
            var refreshTask = ActiveAudioConfigSync.RefreshDefaultAsync();
            if (timeout is { } limit)
            {
                var completed = await Task.WhenAny(refreshTask, Task.Delay(limit));
                if (completed != refreshTask) return;
            }
            await refreshTask;
        }
        catch { }
    }
    private void MergeActiveAudioConfigurations()
    {
        _checkingConfig = ActiveAudioConfigSync.Current.Merge(_checkingConfig);
        SyncComboModelsWithCheckingConfig();
        if (ActiveAudioConfigSync.Current.Warning.Length > 0)
        {
            TxtConfigStatus.Text = "⚠ ACTIVE: cache / feed v1.3";
            TxtConfigStatus.ToolTip = ActiveAudioConfigSync.Current.Warning; TxtConfigStatus.Foreground = Brushes.Salmon;
        }
        else ApplyServerConfigStatus();
    }
    private async Task OpenPendingConfigsAsync(bool automatic)
    {
        BtnPendingConfigs.Visibility = ServerEngine.IsConfigAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (!ServerEngine.IsConfigAdmin) return;
        if (IsAudioRoutingBusy || IsStandardMeasurementBusy || _modelEditorOpen) return;
        if (!await _audioConfigPublishGate.WaitAsync(0)) return;
        try
        {
            var pending = await ServerEngine.GetPendingAudioConfigRevisionsAsync();
            BtnPendingConfigs.Content = $"Chờ duyệt ({pending.Length})";
            await RefreshActiveAudioConfigurationsAsync();
            MergeActiveAudioConfigurations();
            if (pending.Length == 0 && automatic) return;
            // Recheck after HTTP: an acquisition could have started while the request was pending.
            if (IsAudioRoutingBusy || IsStandardMeasurementBusy || _modelEditorOpen) return;
            var popup = new ConfigApprovalWindow(pending) { Owner = this, AfterApproved = async () =>
            {
                await ActiveAudioConfigSync.RefreshDefaultAsync();
                MergeActiveAudioConfigurations();
                var model = FindModelConfig(_checkingConfig.models, ComboModels.SelectedItem?.ToString());
                if (model != null && !SessionModelConfigurations.Current.IsTemporary(model.model))
                { _audioRoutingView.ClearForConfigurationChange(); ApplySessionModel(model.testItems.InOut); }
                RefreshTrialBanner();
            } };
            popup.ShowDialog();
            BtnPendingConfigs.Content = $"Chờ duyệt ({popup.PendingList.Items.Count})";
        }
        catch (Exception ex)
        {
            BtnPendingConfigs.ToolTip = ex.Message;
            if (!automatic) ModernMessageBox.Show(this, "Không tải được danh sách chờ duyệt: " + ex.Message, "Duyệt cấu hình", ModernMessageBox.MessageBoxType.Error);
        }
        finally { _audioConfigPublishGate.Release(); }
    }
    private async void BtnPendingConfigs_Click(object sender, RoutedEventArgs e) => await OpenPendingConfigsAsync(false);
}
