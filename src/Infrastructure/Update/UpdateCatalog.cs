using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector.Update;

/// <summary>One archived release in the signed catalog (versions.json). Each entry still has its own signed
/// versions/&lt;version&gt;.json; the catalog only lists them so a station can pick one (including an older one).</summary>
public sealed record UpdateCatalogEntry(string Version, DateTimeOffset ReleasedUtc, string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Channel = null)
{
    [JsonIgnore] public bool IsBeta => Channel == AppVersion.BetaChannel;
    [JsonIgnore] public string DisplayVersion => AppVersion.Format(UpdateManifest.ParseVersion(Version)) + (IsBeta ? " beta" : "");
}

/// <summary>Signed list of every archived release plus the two pointers: StableVersion (= latest.json, what every
/// station is offered) and BetaVersion (= beta.json, only for stations that chose the beta channel).</summary>
public sealed record UpdateCatalog(int Schema, string App, string Architecture, DateTimeOffset GeneratedUtc,
    string StableVersion, UpdateCatalogEntry[] Versions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BetaVersion = null)
{
    public const string FileName = "versions.json";
    public UpdateCatalog Validate()
    {
        if (Schema != 1 || App != "SoncaAudioInspector" || Architecture != "win-x64" || GeneratedUtc == default || Versions == null || Versions.Length == 0)
            throw new InvalidDataException("Danh mục phiên bản không đúng ứng dụng, schema hoặc kiến trúc.");
        var seen = new HashSet<Version>();
        foreach (var entry in Versions)
        {
            if (entry == null || entry.ReleasedUtc == default || entry.Summary == null) throw new InvalidDataException("Danh mục có mục rỗng.");
            if (entry.Channel != null && entry.Channel != AppVersion.BetaChannel) throw new InvalidDataException("Kênh phát hành không hợp lệ trong danh mục.");
            if (!seen.Add(UpdateManifest.ParseVersion(entry.Version))) throw new InvalidDataException("Danh mục trùng phiên bản.");
        }
        var stable = Versions.FirstOrDefault(v => v.Version == StableVersion);
        if (stable == null || stable.IsBeta) throw new InvalidDataException("Bản ổn định của danh mục không có hoặc là beta.");
        if (BetaVersion != null && Versions.FirstOrDefault(v => v.Version == BetaVersion) is not { IsBeta: true })
            throw new InvalidDataException("Bản beta của danh mục không có hoặc không đánh dấu beta.");
        return this;
    }
    public static UpdateCatalog Read(byte[] bytes) => JsonSerializer.Deserialize<UpdateCatalog>(bytes, UpdateManifest.JsonOptions)
        ?? throw new InvalidDataException("Danh mục phiên bản rỗng.");
    public IEnumerable<UpdateCatalogEntry> NewestFirst() => Versions.OrderByDescending(v => UpdateManifest.ParseVersion(v.Version));
}
