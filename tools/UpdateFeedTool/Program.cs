using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector.Update;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    if (args.Length == 0 || args[0] is not ("build" or "verify" or "history" or "config" or "verify-config" or "prune" or "submit-temp" or "verify-temp"))
        throw new ArgumentException("Dùng build, verify, history, prune, config, verify-config, submit-temp hoặc verify-temp cùng các tham số --name value.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--") || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Tham số không hợp lệ.");
    string Required(string key) => options.TryGetValue("--" + key, out var value) ? value : throw new ArgumentException("Thiếu --" + key);
    if (args[0] == "submit-temp")
    {
        // Temporary build from another machine: no signing key here; the server signs after ADMIN approval (Beta channel only).
        string tokenVariable = options.TryGetValue("--token-env", out var variable) ? variable : "SONCA_UPDATE_SUBMIT_TOKEN";
        string token = Environment.GetEnvironmentVariable(tokenVariable) ?? throw new ArgumentException($"Đặt token gửi bản tạm vào biến môi trường {tokenVariable}.");
        string[] tempFeatures = File.ReadAllLines(Required("features-file"), Encoding.UTF8).Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
        foreach (string feature in tempFeatures) ReleaseNotesText.RequireUtf8(feature, "Dòng tính năng trong --features-file");
        TempSubmission.SubmitAsync(options.TryGetValue("--server", out var server) ? server : "https://inventory.acnos.store", token,
            options.TryGetValue("--submitter", out var who) ? who : Environment.MachineName, Path.GetFullPath(Required("portable")), Required("version"),
            tempFeatures, Console.Out).GetAwaiter().GetResult();
        return;
    }
    if (args[0] == "verify-temp")
    {
        TempSubmission.VerifyAsync(options.TryGetValue("--feed", out var tempFeed) ? tempFeed : TempSubmission.DefaultFeed,
            options.TryGetValue("--version", out var expected) ? expected : null, Console.Out).GetAwaiter().GetResult();
        return;
    }
    if (args[0] == "config")
    {
        // Mandatory station config: config/files/<sha>.json (immutable) + config/latest.json(.sig) pointer.
        byte[] content = File.ReadAllBytes(Path.GetFullPath(Required("config")));
        ServerConfigManifest.ValidateContentShape(content);
        if (content.LongLength > ServerConfigManifest.MaxSize) throw new InvalidDataException("Config quá lớn.");
        string configOutput = Path.GetFullPath(Required("out"));
        if (Directory.Exists(configOutput) && Directory.EnumerateFileSystemEntries(configOutput).Any()) throw new IOException("Thư mục config feed phải mới.");
        long serial = long.Parse(Required("serial"), CultureInfo.InvariantCulture);
        string sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var configManifest = new ServerConfigManifest(1, ServerConfigManifest.ExpectedApp, ServerConfigManifest.ExpectedKind, serial,
            DateTimeOffset.UtcNow, sha, content.LongLength,
            options.TryGetValue("--notes", out var configNotes) ? ReleaseNotesText.RequireUtf8(configNotes, "Ghi chú config (--notes)") : null).Validate();
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(configManifest, new JsonSerializerOptions(ServerConfigManifest.JsonOptions) { WriteIndented = true });
        string configSignature = UpdateSignature.Sign(manifestBytes, File.ReadAllText(Required("key")));
        Directory.CreateDirectory(Path.Combine(configOutput, "files"));
        File.WriteAllBytes(Path.Combine(configOutput, "files", sha + ".json"), content);
        File.WriteAllBytes(Path.Combine(configOutput, "latest.json"), manifestBytes);
        File.WriteAllText(Path.Combine(configOutput, "latest.json.sig"), configSignature, new UTF8Encoding(false));
        Console.WriteLine($"PASS config #{serial}: {content.LongLength} byte, sha256 {sha}; {configOutput}");
        return;
    }
    if (args[0] == "verify-config")
    {
        string feed = Path.GetFullPath(Required("feed"));
        string publicKey = File.ReadAllText(Required("public-key"));
        byte[] manifestBytes = File.ReadAllBytes(Path.Combine(feed, "latest.json"));
        UpdateSignature.Verify(manifestBytes, File.ReadAllText(Path.Combine(feed, "latest.json.sig")).Trim(), publicKey);
        var configManifest = ServerConfigManifest.Read(manifestBytes).Validate();
        byte[] content = File.ReadAllBytes(Path.Combine(feed, "files", configManifest.Sha256 + ".json"));
        if (content.LongLength != configManifest.Size || Convert.ToHexStringLower(SHA256.HashData(content)) != configManifest.Sha256)
            throw new InvalidDataException("Config sai size/SHA-256 so với manifest.");
        ServerConfigManifest.ValidateContentShape(content);
        if (options.TryGetValue("--expect-config", out var expected) && !File.ReadAllBytes(expected).SequenceEqual(content))
            throw new InvalidDataException("Config trong feed khác file nguồn.");
        Console.WriteLine($"PASS verify-config #{configManifest.Serial}: {content.LongLength} byte, sha256 {configManifest.Sha256}; chữ ký P-256/P1363 hợp lệ.");
        return;
    }
    if (args[0] == "history")
    {
        string feed = Path.GetFullPath(Required("feed"));
        string publicKey = File.ReadAllText(Required("public-key"));
        var manifest = ReadVerified(feed, "latest.json", publicKey);
        string target = Path.GetFullPath(Required("out"));
        if (File.Exists(target)) throw new IOException("HTML output đã tồn tại; dùng đường dẫn artifact mới.");
        if (Nested(target, feed)) throw new IOException("HTML output phải nằm ngoài feed nguồn để giữ artifact cũ.");
        FeedHistoryPage.Write(feed, manifest.Version, publicKey, target);
        Console.WriteLine($"PASS history {manifest.Version}: {target}; manifest/chữ ký/blob/ZIP nguồn không thay đổi.");
        return;
    }
    if (args[0] == "prune")
    {
        // Owner-requested removal of old releases. The new feed keeps the listed versions (always incl. the stable
        // latest.json and beta.json targets), re-signs versions.json, re-renders index.html and writes prune-plan.txt:
        // the exact server paths that only removed releases use. Pointers are copied byte-for-byte, never re-signed.
        string feed = Path.GetFullPath(Required("feed"));
        string pruned = Path.GetFullPath(Required("out"));
        if (Directory.Exists(pruned) && Directory.EnumerateFileSystemEntries(pruned).Any()) throw new IOException("Thư mục feed sau khi tỉa phải mới.");
        string pruneKey = File.ReadAllText(Required("key"));
        using var signer = ECDsa.Create(); signer.ImportFromPem(pruneKey);
        string prunePublic = signer.ExportSubjectPublicKeyInfoPem();
        var keep = Required("keep").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        foreach (string v in keep) UpdateManifest.ParseVersion(v);
        var stablePointer = ReadVerified(feed, "latest.json", prunePublic);
        var betaPointer = File.Exists(Path.Combine(feed, "beta.json")) ? ReadVerified(feed, "beta.json", prunePublic) : null;
        if (!keep.Contains(stablePointer.Version) || betaPointer != null && !keep.Contains(betaPointer.Version))
            throw new InvalidDataException("Phải giữ bản ổn định (latest.json) và bản beta (beta.json).");
        var all = FeedHistoryPage.Versions(feed, prunePublic).ToArray();
        if (keep.Any(v => all.All(a => a.Manifest.Version != v))) throw new InvalidDataException("Có phiên bản cần giữ không tồn tại trong feed.");
        var kept = all.Where(a => keep.Contains(a.Manifest.Version)).ToArray();
        var removed = all.Where(a => !keep.Contains(a.Manifest.Version)).ToArray();
        var keptBlobs = kept.SelectMany(a => a.Manifest.Files.Select(f => f.Sha256)).ToHashSet(StringComparer.Ordinal);
        Directory.CreateDirectory(Path.Combine(pruned, "files")); Directory.CreateDirectory(Path.Combine(pruned, "versions")); Directory.CreateDirectory(Path.Combine(pruned, "packages"));
        foreach (var (source, manifest) in kept)
        {
            foreach (var file in manifest.Files)
            {
                string blob = Path.Combine(feed, "files", file.Sha256), target = Path.Combine(pruned, "files", file.Sha256);
                UpdatePaths.RejectLinks(blob); if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Feed thiếu/sai blob: " + file.Path);
                if (!File.Exists(target)) File.Copy(blob, target);
            }
            string package = Path.Combine(feed, "packages", manifest.Version, "SoncaAudioInspector-win-x64.zip");
            Directory.CreateDirectory(Path.Combine(pruned, "packages", manifest.Version));
            File.Copy(package, Path.Combine(pruned, "packages", manifest.Version, "SoncaAudioInspector-win-x64.zip"));
            File.Copy(source, Path.Combine(pruned, "versions", Path.GetFileName(source)));
            File.Copy(source + ".sig", Path.Combine(pruned, "versions", Path.GetFileName(source) + ".sig"));
        }
        CopyPointer(feed, pruned, "latest.json");
        if (betaPointer != null) CopyPointer(feed, pruned, "beta.json");
        var prunedCatalog = WriteCatalog(pruned, pruneKey, prunePublic);
        FeedHistoryPage.Write(pruned, prunedCatalog.StableVersion, prunePublic);
        var plan = removed.SelectMany(r => new[] { "versions/" + r.Manifest.Version + ".json", "versions/" + r.Manifest.Version + ".json.sig", "packages/" + r.Manifest.Version })
            .Concat(removed.SelectMany(r => r.Manifest.Files.Select(f => f.Sha256)).Distinct().Where(sha => !keptBlobs.Contains(sha)).Order(StringComparer.Ordinal).Select(sha => "files/" + sha))
            .ToArray();
        File.WriteAllLines(Path.Combine(pruned, "prune-plan.txt"), plan, new UTF8Encoding(false));
        long blobBytes = removed.SelectMany(r => r.Manifest.Files).Where(f => !keptBlobs.Contains(f.Sha256)).DistinctBy(f => f.Sha256).Sum(f => f.Size);
        long packageBytes = removed.Sum(r => new FileInfo(Path.Combine(feed, "packages", r.Manifest.Version, "SoncaAudioInspector-win-x64.zip")).Length);
        Console.WriteLine($"PASS prune: giữ {string.Join(", ", kept.Select(k => k.Manifest.Version).OrderBy(UpdateManifest.ParseVersion))}; "
            + $"xóa {string.Join(", ", removed.Select(r => r.Manifest.Version).OrderBy(UpdateManifest.ParseVersion))}: {plan.Count(p => p.StartsWith("files/"))} blob "
            + $"({blobBytes} byte) + {removed.Length} ZIP ({packageBytes} byte); danh mục mới {prunedCatalog.Versions.Length} phiên bản; kế hoạch: {Path.Combine(pruned, "prune-plan.txt")}");
        return;
    }
    if (args[0] == "verify")
    {
        string feed = Path.GetFullPath(Required("feed"));
        string publicKey = File.ReadAllText(Required("public-key"));
        string expectedChannel = options.TryGetValue("--channel", out var verifyChannel) ? verifyChannel.Trim().ToLowerInvariant() : "stable";
        if (expectedChannel is not ("stable" or "beta")) throw new ArgumentException("--channel chỉ nhận stable hoặc beta.");
        UpdateManifest VerifyPointer(string name)
        {
            var pointed = ReadVerified(feed, name, publicKey);
            foreach (var file in pointed.Files)
            {
                string blob = Path.Combine(feed, "files", file.Sha256);
                UpdatePaths.RejectLinks(blob);
                if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Thiếu hoặc sai blob: " + file.Path);
            }
            string archived = "versions/" + pointed.Version + ".json";
            if (!File.ReadAllBytes(Path.Combine(feed, archived)).SequenceEqual(File.ReadAllBytes(Path.Combine(feed, name))))
                throw new InvalidDataException("Manifest lưu trữ khác " + name + ".");
            ReadVerified(feed, archived, publicKey);
            return pointed;
        }
        var manifest = VerifyPointer("latest.json");
        if (manifest.IsBeta) throw new InvalidDataException("latest.json không được trỏ tới bản beta.");
        UpdateManifest? betaManifest = File.Exists(Path.Combine(feed, "beta.json")) ? VerifyPointer("beta.json") : null;
        if (betaManifest is { IsBeta: false }) throw new InvalidDataException("beta.json phải trỏ tới bản đánh dấu beta.");
        if (expectedChannel == "beta" && (betaManifest == null || UpdateManifest.ParseVersion(betaManifest.Version) <= UpdateManifest.ParseVersion(manifest.Version)))
            throw new InvalidDataException("Feed beta phải có beta.json mới hơn latest.json.");
        if (expectedChannel == "beta" && options.TryGetValue("--previous", out var previousFeed)
            && (!File.ReadAllBytes(Path.Combine(feed, "latest.json")).SequenceEqual(File.ReadAllBytes(Path.Combine(previousFeed, "latest.json")))
                || !File.ReadAllBytes(Path.Combine(feed, "latest.json.sig")).SequenceEqual(File.ReadAllBytes(Path.Combine(previousFeed, "latest.json.sig")))))
            throw new InvalidDataException("Bản beta đã làm thay đổi latest.json so với feed trước.");
        byte[] catalogBytes = File.ReadAllBytes(Path.Combine(feed, UpdateCatalog.FileName));
        UpdateSignature.Verify(catalogBytes, File.ReadAllText(Path.Combine(feed, UpdateCatalog.FileName + ".sig")).Trim(), publicKey);
        var verifiedCatalog = UpdateCatalog.Read(catalogBytes).Validate();
        var archivedVersions = FeedHistoryPage.Versions(feed, publicKey).Select(v => v.Manifest.Version).Order().ToArray();
        if (verifiedCatalog.StableVersion != manifest.Version || verifiedCatalog.BetaVersion != betaManifest?.Version
            || !verifiedCatalog.Versions.Select(v => v.Version).Order().SequenceEqual(archivedVersions))
            throw new InvalidDataException("Danh mục versions.json khác con trỏ hoặc archive.");
        Console.WriteLine($"PASS verify {manifest.Version}{(betaManifest == null ? "" : " · beta " + betaManifest.Version)}: {manifest.Files.Count} file, "
            + $"{manifest.Files.Sum(f => f.Size)} byte; danh mục {verifiedCatalog.Versions.Length} phiên bản; chữ ký P-256/P1363 và mọi SHA-256 hợp lệ.");
        return;
    }
    string portable = Path.GetFullPath(Required("portable"));
    string output = Path.GetFullPath(Required("out"));
    string version = Required("version");
    UpdateManifest.ParseVersion(version);
    DateTimeOffset released = options.TryGetValue("--released", out var releasedText)
        ? DateTimeOffset.Parse(releasedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime() : DateTimeOffset.UtcNow;
    string[] features = options.TryGetValue("--features-file", out var featuresFile)
        ? File.ReadAllLines(featuresFile, Encoding.UTF8).Select(f => f.Trim()).Where(f => f.Length > 0).ToArray() : [];
    string notes = ReleaseNotesText.RequireUtf8(Required("notes"), "Ghi chú phát hành (--notes)");
    foreach (string feature in features) ReleaseNotesText.RequireUtf8(feature, "Dòng tính năng trong --features-file");
    if (Nested(output, portable) || Nested(portable, output)) throw new ArgumentException("Feed và portable phải ở các thư mục riêng.");
    UpdatePaths.RejectLinks(output);
    string privateKey = File.ReadAllText(Required("key"));
    using var key = ECDsa.Create(); key.ImportFromPem(privateKey);
    string publicPem = key.ExportSubjectPublicKeyInfoPem();
    // Channel: stable writes latest.json (every station is offered it). beta writes only beta.json and copies the
    // previous latest.json byte-for-byte, so stations on the stable channel never see a beta (skill sonca-beta-release).
    string channel = options.TryGetValue("--channel", out var channelText) ? channelText.Trim().ToLowerInvariant() : "stable";
    if (channel is not ("stable" or "beta")) throw new ArgumentException("--channel chỉ nhận stable hoặc beta.");
    bool beta = channel == "beta";
    string? previous = options.TryGetValue("--previous", out var prev) ? Path.GetFullPath(prev) : null;
    if (beta && previous == null) throw new ArgumentException("Bản beta cần --previous là feed ổn định đầy đủ để giữ nguyên latest.json.");
    if (previous != null && ReadVerified(previous, "latest.json", publicPem).IsBeta) throw new InvalidDataException("latest.json của feed trước trỏ tới bản beta.");
    string archive = Path.Combine(output, "versions", version + ".json");
    foreach (string path in new[] { archive, archive + ".sig", Path.Combine(output, "latest.json"), Path.Combine(output, "latest.json.sig"),
        Path.Combine(output, "beta.json"), Path.Combine(output, "beta.json.sig"), Path.Combine(output, UpdateCatalog.FileName), Path.Combine(output, "packages", version) })
        UpdatePaths.RejectLinks(path);
    if (File.Exists(archive)) throw new IOException("Phiên bản đã tồn tại; dùng số phiên bản cao hơn.");
    if (previous != null && File.Exists(Path.Combine(previous, "versions", version + ".json"))) throw new IOException("Phiên bản đã có trong feed trước; dùng số phiên bản cao hơn.");
    Directory.CreateDirectory(Path.Combine(output, "files"));
    Directory.CreateDirectory(Path.Combine(output, "versions"));
    Directory.CreateDirectory(Path.Combine(output, "packages", version));
    if (previous != null) FeedHistoryPage.CopyHistory(previous, output, publicPem);
    if (beta) CopyPointer(previous!, output, "latest.json");
    else if (previous != null && File.Exists(Path.Combine(previous, "beta.json"))
        && UpdateManifest.ParseVersion(ReadVerified(previous, "beta.json", publicPem).Version) > UpdateManifest.ParseVersion(version))
        CopyPointer(previous, output, "beta.json"); // a newer beta stays offered to beta stations; an older one is superseded.
    string versionInfo = UpdatePaths.Resolve(portable, "version-info.json");
    File.WriteAllBytes(versionInfo, JsonSerializer.SerializeToUtf8Bytes(new InstalledVersionInfo(version, released, features, beta ? "beta" : null), UpdateManifest.JsonOptions));
    var files = new List<UpdateFile>(); int reused = 0;
    foreach (string source in UpdatePaths.ApplicationFiles(portable).Order(StringComparer.Ordinal))
    {
        string relative = Path.GetRelativePath(portable, source).Replace('\\', '/');
        if (UpdatePaths.IsExcluded(relative)) continue;
        UpdatePaths.Validate(relative);
        var entry = new UpdateFile(relative, new FileInfo(source).Length, UpdateSignature.Hash(source));
        string blob = Path.Combine(output, "files", entry.Sha256);
        UpdatePaths.RejectLinks(blob);
        string? old = previous == null ? null : Path.Combine(previous, "files", entry.Sha256);
        if (old != null) UpdatePaths.RejectLinks(old);
        bool reuse = old != null && UpdateSignature.Matches(old, entry);
        if (!File.Exists(blob)) File.Copy(reuse ? old! : source, blob);
        if (!UpdateSignature.Matches(blob, entry)) throw new InvalidDataException("Blob không khớp; portable thay đổi trong khi build: " + relative);
        if (reuse) reused++;
        files.Add(entry);
    }
    var built = new UpdateManifest(1, "SoncaAudioInspector", version, "win-x64", released, notes, files, features, beta ? "beta" : null);
    built.Validate();
    string zipPath = Path.Combine(output, "packages", version, "SoncaAudioInspector-win-x64.zip");
    UpdatePaths.RejectLinks(zipPath);
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        foreach (var entry in files) zip.CreateEntryFromFile(Path.Combine(output, "files", entry.Sha256), entry.Path, CompressionLevel.Optimal);
    // Publish archive first; latest pointers are written only after every immutable blob/package exists.
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(built, UpdateManifest.JsonOptions);
    string signature = UpdateSignature.Sign(bytes, privateKey);
    File.WriteAllBytes(archive, bytes);
    File.WriteAllText(archive + ".sig", signature, new UTF8Encoding(false));
    string pointer = beta ? "beta.json" : "latest.json";
    File.WriteAllText(Path.Combine(output, pointer + ".sig"), signature, new UTF8Encoding(false));
    File.WriteAllBytes(Path.Combine(output, pointer), bytes);
    var catalog = WriteCatalog(output, privateKey, publicPem);
    FeedHistoryPage.Write(output, catalog.StableVersion, publicPem);
    Console.WriteLine($"PASS build {version} ({(beta ? "BETA: chỉ beta.json, latest.json giữ " + catalog.StableVersion : "ổn định: latest.json")}): {files.Count} file, "
        + $"{files.Sum(f => f.Size)} byte, {reused} file dùng lại từ feed cũ; danh mục {catalog.Versions.Length} phiên bản. MKL được giữ nếu có trong portable.");
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); Environment.ExitCode = 1; }

static bool Nested(string value, string root) => value.Equals(root, StringComparison.OrdinalIgnoreCase)
    || value.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
static void CopyPointer(string source, string output, string name)
{
    foreach (string file in new[] { name, name + ".sig" })
    {
        string from = Path.Combine(source, file), to = Path.Combine(output, file);
        UpdatePaths.RejectLinks(from); UpdatePaths.RejectLinks(to);
        if (File.Exists(to)) throw new IOException("Con trỏ đã tồn tại trong feed mới: " + file);
        File.Copy(from, to);
    }
}
// Signed list of every archived version plus the stable (latest.json) and beta (beta.json) pointers.
static UpdateCatalog WriteCatalog(string feed, string privateKey, string publicKey)
{
    var stable = ReadVerified(feed, "latest.json", publicKey);
    var betaPointer = File.Exists(Path.Combine(feed, "beta.json")) ? ReadVerified(feed, "beta.json", publicKey) : null;
    var entries = FeedHistoryPage.Versions(feed, publicKey)
        .Select(v => new UpdateCatalogEntry(v.Manifest.Version, v.Manifest.ReleasedUtc, v.Manifest.FormatFeatures(), v.Manifest.Channel))
        .OrderByDescending(e => UpdateManifest.ParseVersion(e.Version)).ToArray();
    var catalog = new UpdateCatalog(1, "SoncaAudioInspector", "win-x64", DateTimeOffset.UtcNow, stable.Version, entries, betaPointer?.Version).Validate();
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(catalog, UpdateManifest.JsonOptions);
    File.WriteAllText(Path.Combine(feed, UpdateCatalog.FileName + ".sig"), UpdateSignature.Sign(bytes, privateKey), new UTF8Encoding(false));
    File.WriteAllBytes(Path.Combine(feed, UpdateCatalog.FileName), bytes);
    return catalog;
}
static UpdateManifest ReadVerified(string feed, string relative, string key)
{
    string path = Path.Combine(feed, relative); UpdatePaths.RejectLinks(path); UpdatePaths.RejectLinks(path + ".sig");
    byte[] bytes = File.ReadAllBytes(path);
    UpdateSignature.Verify(bytes, File.ReadAllText(path + ".sig"), key);
    var manifest = UpdateManifest.Read(bytes); manifest.Validate(); return manifest;
}
