using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector.Update;

public enum UpdateChannelKind { Stable, Beta }

/// <summary>Per-station choice of update channel. Stable stations read only latest.json, so a beta is never
/// offered to them. A beta build with no saved choice follows the beta channel it was installed from.</summary>
public static class UpdateChannelPreference
{
    public const string FileName = "update-channel.json";
    private sealed record Stored(string Channel);
    public static UpdateChannelKind Default => AppVersion.IsBeta ? UpdateChannelKind.Beta : UpdateChannelKind.Stable;
    public static string DataDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : local, "SoncaAudioInspector");
    }
    public static string PathIn(string directory) => Path.Combine(directory, FileName);
    /// <summary>Missing or unreadable file → the build's default; never a crash.</summary>
    public static UpdateChannelKind Load(string? directory = null)
    {
        try
        {
            string path = PathIn(directory ?? DataDirectory());
            if (!File.Exists(path)) return Default;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllBytes(path), UpdateManifest.JsonOptions);
            return stored?.Channel?.Trim().ToLowerInvariant() switch { "beta" => UpdateChannelKind.Beta, "stable" => UpdateChannelKind.Stable, _ => Default };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return Default; }
    }
    public static void Save(UpdateChannelKind channel, string? directory = null)
    {
        string folder = directory ?? DataDirectory();
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(PathIn(folder), JsonSerializer.Serialize(new Stored(channel == UpdateChannelKind.Beta ? "beta" : "stable"), UpdateManifest.JsonOptions));
    }
    public static string Describe(UpdateChannelKind channel) => channel == UpdateChannelKind.Beta
        ? "Beta (thử nghiệm) — nhận cả bản beta chưa kiểm tra kỹ"
        : "Ổn định (khuyến nghị) — chỉ nhận bản đã phát hành chính thức";
}
