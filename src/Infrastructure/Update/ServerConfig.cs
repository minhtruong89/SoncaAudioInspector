using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoncaAudioInspector.Update;

public enum ServerConfigMode { NotChecked, Online, OfflineCached, Unavailable, AdminLocal }

public sealed record ServerConfigState(ServerConfigMode Mode, ServerConfigManifest? Manifest, string Message)
{
    /// <summary>Measurements are blocked only when no server-signed configuration was ever verified on this station.</summary>
    public bool AllowsMeasurement => Mode is ServerConfigMode.Online or ServerConfigMode.OfflineCached or ServerConfigMode.AdminLocal;
    public string Banner => Mode switch
    {
        ServerConfigMode.Online => "Config server " + Manifest?.Describe(),
        ServerConfigMode.OfflineCached => "⚠ CONFIG OFFLINE — bản server " + Manifest?.Describe(),
        ServerConfigMode.AdminLocal => "⚠ ADMIN: config cục bộ khác server",
        ServerConfigMode.Unavailable => "⛔ CHƯA CÓ CONFIG SERVER",
        _ => ""
    };
}

public static class ServerConfigStatus
{
    public const string ServerManagedMessage = "Cấu hình bài đo do file config trên server quản lý: mọi máy trạm phải dùng đúng file này mỗi lần mở app, "
        + "nên không lưu sửa đổi tại máy. Muốn đổi cấu hình, cập nhật file config trên server (scripts/publish-config.ps1 + deploy-config.ps1), "
        + "các máy sẽ nhận ở lần mở app tiếp theo.";
    public static ServerConfigState Current { get; set; } = new(ServerConfigMode.NotChecked, null, "Chưa kiểm tra config server.");
    /// <summary>Admin station only: edits stay local until published. Never set on production stations.</summary>
    public static bool AdminMode => Environment.GetEnvironmentVariable("SONCA_CONFIG_ADMIN") == "1";
    public static bool IsServerManaged => !AdminMode;
}

public sealed class ServerConfigClient : IDisposable
{
    public const string DefaultFeed = UpdateClient.DefaultFeed + "config/";
    private readonly HttpClient _http;
    private readonly Uri _feed;
    private readonly string _publicKey;
    public ServerConfigClient(Uri? feed = null, HttpMessageHandler? handler = null, string publicKey = UpdateSignature.PublicKey, TimeSpan? timeout = null)
    {
        string address = (feed?.AbsoluteUri ?? Environment.GetEnvironmentVariable("SONCA_CONFIG_FEED") ?? DefaultFeed).TrimEnd('/') + "/";
        _feed = new Uri(address, UriKind.Absolute);
        if (_feed.Scheme is not ("https" or "http") || _feed.UserInfo.Length > 0 || _feed.Query.Length > 0 || _feed.Fragment.Length > 0)
            throw new InvalidDataException("Địa chỉ config server không hợp lệ.");
        _publicKey = publicKey;
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false })
            { Timeout = timeout ?? TimeSpan.FromSeconds(8) };
    }
    public sealed record Download(ServerConfigManifest Manifest, byte[] ManifestBytes, string Signature, byte[] Content);
    public async Task<Download> FetchAsync(CancellationToken token = default)
    {
        // Only the mutable pointer gets a cache-busting query; content is immutable and addressed by hash.
        string bust = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] manifestBytes = await _http.GetByteArrayAsync(new Uri(_feed, "latest.json" + bust), token).ConfigureAwait(false);
        string signature = (await _http.GetStringAsync(new Uri(_feed, "latest.json.sig" + bust), token).ConfigureAwait(false)).Trim();
        var manifest = VerifyManifest(manifestBytes, signature, _publicKey);
        using var response = await _http.GetAsync(new Uri(_feed, "files/" + manifest.Sha256 + ".json"), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != manifest.Size)
            throw new InvalidDataException("Kích thước config server khác manifest.");
        byte[] content = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        VerifyContent(manifest, content);
        return new(manifest, manifestBytes, signature, content);
    }
    public static ServerConfigManifest VerifyManifest(byte[] manifestBytes, string signature, string publicKey = UpdateSignature.PublicKey)
    {
        UpdateSignature.Verify(manifestBytes, signature.Trim(), publicKey);
        return ServerConfigManifest.Read(manifestBytes).Validate();
    }
    public static void VerifyContent(ServerConfigManifest manifest, byte[] content)
    {
        if (content.LongLength != manifest.Size || Convert.ToHexStringLower(SHA256.HashData(content)) != manifest.Sha256)
            throw new InvalidDataException("Config server sai size/SHA-256 so với manifest đã ký.");
        ServerConfigManifest.ValidateContentShape(content);
        _ = JsonSerializer.Deserialize<CheckingConfig>(content) ?? throw new InvalidDataException("Config server không đọc được.");
    }
    public void Dispose() => _http.Dispose();
}

/// <summary>Last server-signed configuration this station verified; used when the server cannot be reached.</summary>
public sealed class ServerConfigStore(string directory, string publicKey = UpdateSignature.PublicKey)
{
    private readonly string _publicKey = publicKey;
    public string Directory { get; } = Path.GetFullPath(directory);
    private string ManifestPath => Path.Combine(Directory, "latest.json");
    private string SignaturePath => Path.Combine(Directory, "latest.json.sig");
    private string ContentPath => Path.Combine(Directory, "checking_config.json");
    public void Save(ServerConfigClient.Download download)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Content first, pointer last: an interrupted save leaves the old verified pair or fails verification.
        AtomicFile.WriteAllBytes(ContentPath, download.Content, keepBackup: false);
        AtomicFile.WriteAllText(SignaturePath, download.Signature, keepBackup: false);
        AtomicFile.WriteAllBytes(ManifestPath, download.ManifestBytes, keepBackup: false);
    }
    public ServerConfigClient.Download? TryLoad()
    {
        try
        {
            if (!File.Exists(ManifestPath) || !File.Exists(SignaturePath) || !File.Exists(ContentPath)) return null;
            byte[] manifestBytes = File.ReadAllBytes(ManifestPath);
            string signature = File.ReadAllText(SignaturePath).Trim();
            var manifest = ServerConfigClient.VerifyManifest(manifestBytes, signature, _publicKey);
            byte[] content = File.ReadAllBytes(ContentPath);
            ServerConfigClient.VerifyContent(manifest, content);
            return new(manifest, manifestBytes, signature, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine("Bản config server đã lưu không hợp lệ: " + ex.Message);
            return null;
        }
    }
}

public static class ServerConfigSync
{
    public static string UserDataDirectory
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoncaAudioInspector")
                : Path.GetFullPath(overridePath);
        }
    }
    public static string UserCheckingConfigPath => Path.Combine(UserDataDirectory, "checking_config.json");
    public static string PortableCheckingConfigPath => Path.Combine(AppContext.BaseDirectory, "checking_config.json");

    public static async Task<ServerConfigState> RunDefaultAsync(CancellationToken token = default)
    {
        using var client = new ServerConfigClient();
        var store = new ServerConfigStore(Path.Combine(UserDataDirectory, "server-config"));
        return await RunAsync(client, store, [UserCheckingConfigPath, PortableCheckingConfigPath], ServerConfigStatus.AdminMode, token);
    }

    /// <summary>Fetches and verifies the signed configuration, refuses rollback below the cached serial, and writes the exact
    /// server bytes to every target. Offline: re-applies the last verified server copy. Never verified: Unavailable.</summary>
    public static async Task<ServerConfigState> RunAsync(ServerConfigClient client, ServerConfigStore store, IReadOnlyList<string> targets,
        bool admin, CancellationToken token = default)
    {
        var cached = store.TryLoad();
        string failure;
        try
        {
            var fetched = await client.FetchAsync(token).ConfigureAwait(false);
            if (cached != null && fetched.Manifest.Serial < cached.Manifest.Serial)
                throw new InvalidDataException($"Server trả config cũ hơn bản đã xác minh (#{fetched.Manifest.Serial} < #{cached.Manifest.Serial}); từ chối rollback.");
            store.Save(fetched);
            bool keptLocal = Apply(fetched.Content, targets, admin);
            return keptLocal
                ? new(ServerConfigMode.AdminLocal, fetched.Manifest, "ADMIN: giữ config cục bộ khác server " + fetched.Manifest.Describe() + "; nhớ publish trước khi giao máy.")
                : new(ServerConfigMode.Online, fetched.Manifest, "Đã áp dụng config server " + fetched.Manifest.Describe() + ".");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { failure = "hết thời gian chờ server"; }
        catch (Exception ex) { failure = ex.Message; }
        if (cached != null)
        {
            bool keptLocal = Apply(cached.Content, targets, admin);
            return new(keptLocal ? ServerConfigMode.AdminLocal : ServerConfigMode.OfflineCached, cached.Manifest,
                $"Không đọc được config server ({failure}). Đang dùng bản server đã xác minh lần trước {cached.Manifest.Describe()}.");
        }
        return new(ServerConfigMode.Unavailable, null,
            $"Chưa từng tải được config server có chữ ký trên máy này ({failure}). Kết nối mạng rồi bấm Kiểm tra lại; chưa cho phép đo.");
    }

    /// <summary>The first target (station data) must be written; later targets (portable copy) are best-effort.
    /// Returns true when admin mode kept a differing local file.</summary>
    private static bool Apply(byte[] content, IReadOnlyList<string> targets, bool admin)
    {
        bool keptLocal = false;
        for (int i = 0; i < targets.Count; i++)
        {
            string target = targets[i];
            byte[]? existing = null;
            try { if (File.Exists(target)) existing = File.ReadAllBytes(target); } catch (IOException) { }
            if (existing != null && existing.AsSpan().SequenceEqual(content)) continue;
            if (admin && existing != null) { keptLocal = true; continue; }
            try { AtomicFile.WriteAllBytes(target, content); }
            catch (Exception ex) when (i > 0 && ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine($"Không ghi được bản sao config {target}: {ex.Message}"); }
        }
        return keptLocal;
    }
}
