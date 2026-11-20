using System.ComponentModel;
using System.Windows;

namespace SoncaAudioInspector.Update;

public sealed class UpdateCheckState : INotifyPropertyChanged
{
    private string _status = "Chưa kiểm";
    private UpdateChannelKind _channel = UpdateChannelPreference.Load();
    public string Feed => Environment.GetEnvironmentVariable("SONCA_UPDATE_FEED") ?? UpdateClient.DefaultFeed;
    public string Status => _status;
    /// <summary>Stable stations read latest.json only; beta stations also read beta.json. Saved per station.</summary>
    public UpdateChannelKind Channel => _channel;
    public Func<Window, Task>? CheckRequested { get; set; }
    /// <summary>Signed catalog of archived versions (versions.json) for "install another version / downgrade".</summary>
    public Func<CancellationToken, Task<UpdateCatalog>>? CatalogRequested { get; set; }
    /// <summary>Installs one archived version (older or newer) through the normal verified, transactional updater.</summary>
    public Func<Window, string, Task>? InstallVersionRequested { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetStatus(string status)
    {
        _status = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
    }
    public void SetChannel(UpdateChannelKind channel, bool persist = true)
    {
        if (persist) UpdateChannelPreference.Save(channel);
        _channel = channel;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Channel)));
    }
}
