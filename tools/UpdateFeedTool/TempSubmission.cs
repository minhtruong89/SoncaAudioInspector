using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoncaAudioInspector.Update;

/// <summary>
/// v1.5.3: a Claude session on another machine sends a TEMPORARY build to the server instead of deploying a feed.
/// The file list is built exactly like `build` (same exclusions, path rules, version-info.json), only blobs the server lacks are
/// uploaded in small chunks, and the build waits for ADMIN approval on /temp-updates. No signing key is needed on this machine:
/// the server signs the beta manifest with its temporary-release key after approval, and only Beta-channel stations read it.
/// </summary>
static class TempSubmission
{
    public const string DefaultFeed = "https://inventory.acnos.store/api/app/updates/temp/";
    public static List<UpdateFile> Prepare(string portable, string version, string[] features)
    {
        UpdateManifest.ParseVersion(version);
        if (!File.Exists(Path.Combine(portable, "SoncaAudioInspector.exe")) || !File.Exists(Path.Combine(portable, "SoncaAudioInspector.dll")))
            throw new FileNotFoundException("Thư mục portable phải có SoncaAudioInspector.exe và SoncaAudioInspector.dll.");
        string versionInfo = UpdatePaths.Resolve(portable, "version-info.json");
        File.WriteAllBytes(versionInfo, JsonSerializer.SerializeToUtf8Bytes(new InstalledVersionInfo(version, DateTimeOffset.UtcNow, features, "beta"), UpdateManifest.JsonOptions));
        var files = new List<UpdateFile>();
        foreach (string source in UpdatePaths.ApplicationFiles(portable).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(portable, source).Replace('\\', '/');
            if (UpdatePaths.IsExcluded(relative)) continue;
            UpdatePaths.Validate(relative);
            files.Add(new UpdateFile(relative, new FileInfo(source).Length, UpdateSignature.Hash(source)));
        }
        // The manifest validator of the station runs here too, so a package it would refuse never reaches the server.
        new UpdateManifest(1, "SoncaAudioInspector", version, "win-x64", DateTimeOffset.UtcNow, features.FirstOrDefault() ?? version, files, features, "beta").Validate();
        return files;
    }

    public static async Task SubmitAsync(string server, string token, string submitter, string portable, string version, string[] features, TextWriter log)
    {
        if (features.Length == 0) throw new ArgumentException("--features-file cần ít nhất một dòng; dòng đầu là tóm tắt.");
        var files = Prepare(portable, version, features);
        using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var created = await Send(http.PostAsJsonAsync("api/app/update-submissions", new
        {
            version, summary = features[0], details = features.Skip(1).ToArray(), submitter,
            files = files.Select(f => new { path = f.Path, size = f.Size, sha256 = f.Sha256 }).ToArray()
        }));
        string id = created["id"]!.GetValue<string>();
        int chunk = created["chunkBytes"]!.GetValue<int>();
        var missing = created["missing"]!.AsArray().Select(m => m!["sha256"]!.GetValue<string>()).ToArray();
        log.WriteLine($"Đã tạo bản tạm {version} (id {id}): {files.Count} file, {files.Sum(f => f.Size):N0} byte; cần tải lên {missing.Length} file.");
        foreach (string sha in missing)
        {
            var file = files.First(f => f.Sha256 == sha);
            string source = UpdatePaths.Resolve(portable, file.Path);
            if (!UpdateSignature.Matches(source, file)) throw new InvalidDataException("Portable thay đổi trong lúc gửi: " + file.Path);
            long offset = 0;
            await using var stream = File.OpenRead(source);
            var buffer = new byte[chunk];
            while (offset < file.Size)
            {
                stream.Position = offset;
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunk, file.Size - offset)));
                using var content = new ByteArrayContent(buffer, 0, read);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                JsonNode answer = null!;
                for (int attempt = 1; ; attempt++)
                {
                    try { answer = await Send(http.PutAsync($"api/app/update-submissions/{id}/blobs/{sha}?offset={offset}", content)); break; }
                    catch (HttpRequestException ex) when (attempt < 4 && (ex.StatusCode == null || (int)ex.StatusCode >= 500))
                    { await Task.Delay(TimeSpan.FromSeconds(2 * attempt)); }
                }
                // The server answers with what it holds; a lost reply simply resumes from there.
                offset = answer["received"]!.GetValue<long>();
            }
            log.WriteLine($"  ✓ {file.Path} ({file.Size:N0} byte)");
        }
        await Send(http.PostAsync($"api/app/update-submissions/{id}/submit", null));
        log.WriteLine($"ĐÃ GỬI DUYỆT bản tạm {version}. ADMIN duyệt tại {server.TrimEnd('/')}/temp-updates; sau đó chỉ máy kênh Beta được mời cập nhật.");
    }

    /// <summary>Checks the live temporary feed the way a Beta station does: server temporary key, beta mark, every file SHA-256.</summary>
    public static async Task VerifyAsync(string feed, string? expectedVersion, TextWriter log)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(feed.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        byte[] bytes = await http.GetByteArrayAsync("latest.json");
        string signature = (await http.GetStringAsync("latest.json.sig")).Trim();
        UpdateSignature.Verify(bytes, signature, UpdateSignature.TempReleasePublicKey);
        var manifest = UpdateManifest.Read(bytes);
        manifest.Validate();
        if (!manifest.IsBeta) throw new InvalidDataException("Bản tạm phải đánh dấu beta.");
        if (expectedVersion != null && manifest.Version != expectedVersion) throw new InvalidDataException($"Feed bản tạm đang là {manifest.Version}, cần {expectedVersion}.");
        foreach (var file in manifest.Files)
        {
            byte[] blob = await http.GetByteArrayAsync("files/" + file.Sha256);
            if (blob.LongLength != file.Size || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob)) != file.Sha256)
                throw new InvalidDataException("File bản tạm sai size/SHA-256: " + file.Path);
        }
        log.WriteLine($"PASS verify-temp {manifest.Version} beta: chữ ký khóa bản tạm hợp lệ, {manifest.Files.Count} file / {manifest.Files.Sum(f => f.Size):N0} byte đúng SHA-256.");
    }

    static async Task<JsonNode> Send(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        try { body = JsonNode.Parse(text); } catch (JsonException) { }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Server {(int)response.StatusCode}: {body?["error"]?.GetValue<string>() ?? text[..Math.Min(text.Length, 300)]}", null, response.StatusCode);
        return body?["data"] ?? throw new InvalidDataException("Server trả về không có data.");
    }
}
