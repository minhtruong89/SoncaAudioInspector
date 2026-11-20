using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector.Update;

public sealed record UpdateFile(string Path, long Size, string Sha256);
public sealed record InstalledVersionInfo(string Version, DateTimeOffset ReleasedUtc, string[] Features,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Channel = null);
// Channel is optional and omitted for stable releases, so stable manifest bytes keep the schema-1 shape; clients
// that predate it ignore the unknown property (System.Text.Json default) and only ever read latest.json anyway.
public sealed record UpdateManifest(int Schema, string App, string Version, string Architecture,
    DateTimeOffset ReleasedUtc, string Notes, List<UpdateFile> Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Features = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Channel = null)
{
    [JsonIgnore] public bool IsBeta => Channel == AppVersion.BetaChannel;
    /// <summary>Set by the client for a server-approved temporary build: its files are served from the temporary feed.</summary>
    [JsonIgnore] public Uri? FeedRoot { get; init; }
    [JsonIgnore] public bool IsTemporary => FeedRoot != null;
    [JsonIgnore] public string DisplayVersion => AppVersion.Format(ParseVersion(Version)) + (IsBeta ? " beta" : "");
    // Keep schema 1 and signed manifest bytes unchanged: the first feature is the summary.
    public string FormatFeatures() => Features?.FirstOrDefault() is string first ? SingleLine(first)
        : NoteLines().FirstOrDefault() ?? "Chưa có ghi chú phát hành.";
    public string[] FeatureDetails()
    {
        var details = (Features ?? []).Skip(1).Select(SingleLine).ToList();
        foreach (string note in Features?.Length > 0 ? NoteLines() : NoteLines().Skip(1))
            if (note != FormatFeatures() && !details.Contains(note)) details.Add(note);
        return details.ToArray();
    }
    public static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private IEnumerable<string> NoteLines() => (Notes ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static Version ParseVersion(string text)
    {
        if (!System.Version.TryParse(text, out var version) || text != version.ToString()) throw new InvalidDataException("Phiên bản không hợp lệ.");
        return AppVersion.Normalize(version);
    }
    public System.Version Validate(string architecture = "win-x64")
    {
        if (Schema != 1 || App != "SoncaAudioInspector" || Architecture != architecture || architecture != "win-x64")
            throw new InvalidDataException("Feed không đúng ứng dụng, schema hoặc kiến trúc win-x64.");
        var version = ParseVersion(Version);
        if (ReleasedUtc == default || Notes == null || Files == null || Files.Count == 0) throw new InvalidDataException("Manifest thiếu dữ liệu.");
        if (Features?.Any(string.IsNullOrWhiteSpace) == true) throw new InvalidDataException("Tính năng rỗng hoặc không hợp lệ.");
        if (Channel != null && Channel != AppVersion.BetaChannel) throw new InvalidDataException("Kênh phát hành không hợp lệ (chỉ bỏ trống hoặc beta).");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (file == null) throw new InvalidDataException("File rỗng.");
            UpdatePaths.Validate(file.Path);
            if (!paths.Add(file.Path) || file.Size < 0 || file.Sha256 == null || file.Sha256.Length != 64
                || file.Sha256.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new InvalidDataException("File trùng, size hoặc SHA-256 không hợp lệ.");
        }
        foreach (string path in paths)
        {
            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (paths.Contains(path[..slash])) throw new InvalidDataException("Đường dẫn file/thư mục xung đột.");
                slash = path.IndexOf('/', slash + 1);
            }
        }
        return version;
    }
    public static UpdateManifest Read(byte[] bytes) => JsonSerializer.Deserialize<UpdateManifest>(bytes, JsonOptions)
        ?? throw new InvalidDataException("Manifest rỗng.");
}
