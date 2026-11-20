using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace SoncaAudioInspector.Update;

public sealed record UpdatePlan(UpdateManifest Manifest, IReadOnlyList<UpdateFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed class UpdateClient : IDisposable
{
    public const string DefaultFeed = "https://inventory.acnos.store/updates/audio-inspector/";
    private readonly HttpClient _http;
    private readonly Uri _feed;
    private readonly string _publicKey;
    public UpdateClient(Uri? feed = null, HttpMessageHandler? handler = null, string publicKey = UpdateSignature.PublicKey)
    {
        string address = (feed?.AbsoluteUri ?? Environment.GetEnvironmentVariable("SONCA_UPDATE_FEED") ?? DefaultFeed).TrimEnd('/') + "/";
        _feed = new Uri(address, UriKind.Absolute);
        if (_feed.Scheme is not ("https" or "http") || _feed.UserInfo.Length > 0 || _feed.Query.Length > 0 || _feed.Fragment.Length > 0)
            throw new InvalidDataException("Địa chỉ feed không hợp lệ.");
        _publicKey = publicKey;
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(15) };
    }
    public const string BetaPointer = "beta.json";
    private static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "win-x64" : "unsupported";
    /// <summary>Stable channel: latest.json only (unchanged since 1.0, so a beta can never reach a stable station).</summary>
    public Task<UpdateManifest?> CheckAsync(Version current, CancellationToken token = default)
        => CheckAsync(current, UpdateChannelKind.Stable, token);
    /// <summary>Beta channel also reads beta.json and offers the highest signed version above the current one.
    /// A missing beta pointer (404) means "no beta", never an error.</summary>
    public async Task<UpdateManifest?> CheckAsync(Version current, UpdateChannelKind channel, CancellationToken token = default)
    {
        var stable = await ReadSignedAsync("latest.json", token).ConfigureAwait(false);
        if (stable.IsBeta) throw new InvalidDataException("latest.json không được trỏ tới bản beta.");
        var best = stable;
        if (channel == UpdateChannelKind.Beta && await TryReadSignedAsync(BetaPointer, token).ConfigureAwait(false) is { } beta)
        {
            if (!beta.IsBeta) throw new InvalidDataException("beta.json phải trỏ tới bản đánh dấu beta.");
            if (UpdateManifest.ParseVersion(beta.Version) > UpdateManifest.ParseVersion(stable.Version)) best = beta;
        }
        if (channel == UpdateChannelKind.Beta)
        {
            // A temporary build is optional: an unreachable or empty temporary feed must never hide the signed stable/beta result.
            UpdateManifest? temporary = null;
            try { temporary = await CheckTemporaryAsync(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Security.Cryptography.CryptographicException
                or TaskCanceledException && !token.IsCancellationRequested)
            { System.Diagnostics.Trace.WriteLine("Feed bản tạm: " + ex.Message); }
            if (temporary != null && UpdateManifest.ParseVersion(temporary.Version) > UpdateManifest.ParseVersion(best.Version)) best = temporary;
        }
        return UpdateManifest.ParseVersion(best.Version) > AppVersion.Normalize(current) ? best : null;
    }
    public const string DefaultTemporaryFeed = "https://inventory.acnos.store/api/app/updates/temp/";
    /// <summary>Feed of the server-approved temporary build (Beta channel only).</summary>
    public static Uri TemporaryFeed(string? overrideAddress = null)
    {
        var feed = new Uri((overrideAddress ?? Environment.GetEnvironmentVariable("SONCA_TEMP_UPDATE_FEED") ?? DefaultTemporaryFeed).TrimEnd('/') + "/", UriKind.Absolute);
        if (feed.Scheme is not ("https" or "http") || feed.UserInfo.Length > 0 || feed.Query.Length > 0 || feed.Fragment.Length > 0)
            throw new InvalidDataException("Địa chỉ feed bản tạm không hợp lệ.");
        return feed;
    }
    /// <summary>Reads the approved temporary build: signed by the server temporary-release key, must be marked beta.</summary>
    public async Task<UpdateManifest?> CheckTemporaryAsync(CancellationToken token = default, Uri? feed = null, string publicKey = UpdateSignature.TempReleasePublicKey)
    {
        var root = feed ?? TemporaryFeed();
        using var probe = await _http.GetAsync(new Uri(root, "latest.json"), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (probe.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        probe.EnsureSuccessStatusCode();
        byte[] bytes = await probe.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        string sig = await _http.GetStringAsync(new Uri(root, "latest.json.sig"), token).ConfigureAwait(false);
        UpdateSignature.Verify(bytes, sig.Trim(), publicKey);
        var manifest = UpdateManifest.Read(bytes);
        manifest.Validate(Architecture);
        if (!manifest.IsBeta) throw new InvalidDataException("Bản tạm phải đánh dấu beta.");
        return manifest with { FeedRoot = root };
    }
    /// <summary>Signed list of archived versions for "install another version / downgrade".</summary>
    public async Task<UpdateCatalog> GetCatalogAsync(CancellationToken token = default)
    {
        byte[] bytes = await _http.GetByteArrayAsync(new Uri(_feed, UpdateCatalog.FileName), token).ConfigureAwait(false);
        string sig = await _http.GetStringAsync(new Uri(_feed, UpdateCatalog.FileName + ".sig"), token).ConfigureAwait(false);
        UpdateSignature.Verify(bytes, sig, _publicKey);
        return UpdateCatalog.Read(bytes).Validate();
    }
    /// <summary>The signed archive of one released version (may be older than the running build).</summary>
    public async Task<UpdateManifest> GetVersionAsync(string version, CancellationToken token = default)
    {
        UpdateManifest.ParseVersion(version);
        var manifest = await ReadSignedAsync("versions/" + version + ".json", token).ConfigureAwait(false);
        if (manifest.Version != version) throw new InvalidDataException("Archive trả về khác phiên bản yêu cầu.");
        return manifest;
    }
    private async Task<UpdateManifest> ReadSignedAsync(string relative, CancellationToken token)
    {
        byte[] bytes = await _http.GetByteArrayAsync(new Uri(_feed, relative), token).ConfigureAwait(false);
        string sig = await _http.GetStringAsync(new Uri(_feed, relative + ".sig"), token).ConfigureAwait(false);
        UpdateSignature.Verify(bytes, sig, _publicKey);
        var manifest = UpdateManifest.Read(bytes);
        manifest.Validate(Architecture);
        return manifest;
    }
    private async Task<UpdateManifest?> TryReadSignedAsync(string relative, CancellationToken token)
    {
        using var probe = await _http.GetAsync(new Uri(_feed, relative), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (probe.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        probe.EnsureSuccessStatusCode();
        return await ReadSignedAsync(relative, token).ConfigureAwait(false);
    }
    public static UpdatePlan CalculateChanges(UpdateManifest manifest, string appDirectory)
    {
        manifest.Validate();
        return new(manifest, manifest.Files.Where(f => !UpdateSignature.Matches(UpdatePaths.Resolve(appDirectory, f.Path), f)).ToArray());
    }
    public static TimeSpan DefaultFileTimeout(long size) => TimeSpan.FromSeconds(Math.Max(120, size / (32d * 1024)));
    public async Task<string> DownloadAsync(UpdatePlan plan, string appDirectory, IProgress<string>? progress = null, CancellationToken token = default,
        TimeSpan? idleTimeout = null, Func<long, TimeSpan>? fileTimeout = null)
    {
        plan.Manifest.Validate();
        TimeSpan idleWait = idleTimeout ?? TimeSpan.FromSeconds(30);
        UpdateInstaller.CheckWritable(appDirectory);
        string stage = UpdateInstaller.StageDirectory(appDirectory, plan.Manifest.Version);
        try
        {
            UpdateInstaller.DeleteStage(stage);
            Directory.CreateDirectory(stage);
            int count = 0;
            foreach (var file in plan.Files)
            {
                if (!plan.Manifest.Files.Contains(file)) throw new InvalidDataException("File không có trong manifest.");
                string target = UpdatePaths.Resolve(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                progress?.Report($"Đang tải cập nhật {++count}/{plan.Files.Count}: {file.Path}");
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                lifetime.CancelAfter((fileTimeout ?? DefaultFileTimeout)(file.Size));
                // HttpClient still limits the wait for headers to 15 seconds; the body has separate budgets.
                using var response = await _http.GetAsync(new Uri(plan.Manifest.FeedRoot ?? _feed, "files/" + file.Sha256), HttpCompletionOption.ResponseHeadersRead, lifetime.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    idle.CancelAfter(idleWait);
                    await using (var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920];
                        long size = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) != 0)
                        {
                            idle.CancelAfter(idleWait); // Progress renews only the silence timer, never the total limit.
                            size = checked(size + read);
                            if (size > file.Size) throw new InvalidDataException("File tải về vượt kích thước: " + file.Path);
                            await output.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                        }
                    }
                }
                if (!UpdateSignature.Matches(target, file)) throw new InvalidDataException("File tải về sai size/SHA-256: " + file.Path);
                lifetime.Token.ThrowIfCancellationRequested();
            }
            return stage;
        }
        catch { UpdateInstaller.DeleteStage(stage); throw; }
    }
    public void Dispose() => _http.Dispose();
}
