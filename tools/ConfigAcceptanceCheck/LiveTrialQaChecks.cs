using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoncaAudioInspector;

internal static class LiveTrialQaChecks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static Task Prepare(string modelFile, string output)
    {
        Directory.CreateDirectory(output);
        var model = JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(modelFile))!;
        model = ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model));
        return Authenticated(async client =>
        {
            var revision = await ServerEngine.CreateAudioConfigRevisionAsync(model, "Kiểm tra dữ liệu thử: LINE CHUẨN thử rồi Auto Test; "
                + string.Join(", ", model.testItems.InOut.Tests.Where(t => t.IsEnabled).Select(t => t.id + " " + t.name)) + "; không duyệt cấu hình.");
            File.WriteAllText(Path.Combine(output, "trial-revision.json"), JsonSerializer.Serialize(revision, Json));
            File.WriteAllText(Path.Combine(output, "trial-model.json"), JsonSerializer.Serialize(model, ModelConfigEditorWindow.JsonOptions));
            Console.WriteLine($"PASS created {revision.Status} trial revision {revision.Id} for {revision.SpeakerModel}; no approval requested.");
        });
    }
    public static Task Upload(string session, string prepared, string output, bool diagnostic = false)
    {
        Directory.CreateDirectory(output);
        using var measured = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "measured-results.json")));
        using var restored = JsonDocument.Parse(File.ReadAllText(Path.Combine(session, "restoration.json")));
        var root = measured.RootElement;
        var results = root.GetProperty("results").EnumerateArray().ToArray();
        if (diagnostic || root.GetProperty("findReference").GetBoolean())
            throw new InvalidOperationException("Chỉ upload kết quả Auto Test hoàn tất (PASS/FAIL); không upload phiên tìm line chuẩn (REFERENCE) hoặc phiên chẩn đoán/chưa hoàn tất.");
        if (!root.GetProperty("isTrial").GetBoolean()
            || results.Length == 0 || results.Any(r => r.GetProperty("Status").GetString() is not ("PASS" or "FAIL") || r.GetProperty("referenceKey").ValueKind != JsonValueKind.String)
            // All steps verified as completed Auto Test PASS/FAIL with valid referenceKey
            || restored.RootElement.GetProperty("changed").GetArrayLength() != 0)
            throw new InvalidOperationException("Chưa có Auto Test thử hợp lệ với LINE CHUẨN thử và xác nhận khôi phục DSP; không upload.");
        var model = JsonSerializer.Deserialize<ModelConfig>(root.GetProperty("config"))!;
        var revision = JsonSerializer.Deserialize<AudioConfigRevision>(File.ReadAllText(Path.Combine(prepared, "trial-revision.json")))!;
        // Compare effective models, including the display-only recording alias, before attaching revision provenance.
        var preparedModel = ModelConfigEditorWindow.ParseModel(revision.SpeakerModel, revision.ConfigJson);
        var actualModel = ModelConfigEditorWindow.ParseModel(model.model, JsonSerializer.Serialize(model));
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(JsonSerializer.SerializeToNode(preparedModel), JsonSerializer.SerializeToNode(actualModel)))
            throw new InvalidOperationException("Cấu hình đo khác bản thử đã lưu trên server; không upload sai revision.");
        SessionModelConfigurations.Current.Apply(actualModel); SessionModelConfigurations.Current.SelectedModel = model.model;
        SessionModelConfigurations.Current.SetServerTrial(model.model, revision.Id);
        string serial = ProductMeasurementContext.ValidateSerial(root.TryGetProperty("measurementId", out var measuredId) ? measuredId.GetString() ?? ""
            : throw new InvalidDataException("Bản thu cũ chưa lưu ID phiên đo. Không tự tạo ID mới khi upload hoặc gán ID của phiên app khác."));
        string folder = Path.Combine(LocalQaStorage.Root, ProductMeasurementContext.FileToken(serial)); Directory.CreateDirectory(folder);
        string[] images = results.Select((r, i) => { string file = Path.Combine(folder, $"FRA-{i + 1}.png"); Graph(r, file); return file; }).ToArray();
        var steps = results.Select(r => new ServerEngine.AudioQaStepResult(r.GetProperty("StageName").GetString()!, r.GetProperty("Status").GetString()!,
            "Đo phần cứng C360; dùng LINE CHUẨN THỬ. " + r.GetProperty("Summary").GetString())).ToArray();
        bool passed = steps.All(s => s.Status == "PASS");
        string status = results.Any(r => r.GetProperty("Status").GetString() is "INVALID" or "NO_SIGNAL") ? "INVALID" : passed ? "PASS" : "FAIL";
        var configuration = new AudioQaConfiguration(revision.Id, serial, "NONE", true, model.model, JsonSerializer.Serialize(actualModel, ModelConfigEditorWindow.JsonOptions),
            "AUTOTEST", status);
        return Authenticated(async client =>
        {
            using var scope = ResultPersistencePolicy.BeginMeasurement();
            if (!await ServerEngine.UploadAudioQaResultAsync(new ProductInfo { Id = serial, SerialNumber = serial, Model = model.model },
                passed, steps, images, true, serial, configuration)) throw new Exception(ServerEngine.LastError);
            var receipt = ServerEngine.LastAudioQaUploadReceipt ?? throw new Exception("Missing trial upload receipt.");
            if (receipt.DataSet != "TRIAL") throw new Exception("Wrong storage dataset.");
            File.WriteAllText(Path.Combine(output, "upload-receipt.json"), JsonSerializer.Serialize(new { serial, model = model.model, revision = revision.Id, phase = configuration.Phase, status, passed, receipt }, Json));
            using var response = await client.GetAsync("api/report/qa-audio/" + receipt.LogId); response.EnsureSuccessStatusCode();
            string text = await response.Content.ReadAsStringAsync(); File.WriteAllText(Path.Combine(output, "independent-readback.json"), text);
            using var detail = JsonDocument.Parse(text); var data = detail.RootElement.GetProperty("data");
            if (data.GetProperty("serial").GetString() != serial || data.GetProperty("steps").GetArrayLength() != steps.Length
                || data.GetProperty("config").GetProperty("status").GetString() != "TRIAL") throw new Exception("Trial readback mismatch.");
            if (diagnostic && (data.GetProperty("appVerdict").ValueKind != JsonValueKind.Null || data.GetProperty("trialStatus").GetString() != status))
                throw new Exception("Diagnostic was incorrectly turned into a product verdict.");
            int index = 0;
            foreach (var image in data.GetProperty("images").EnumerateArray())
            {
                using var download = await client.GetAsync(image.GetProperty("url").GetString()); download.EnsureSuccessStatusCode();
                byte[] bytes = await download.Content.ReadAsByteArrayAsync();
                if (!SHA256.HashData(bytes).SequenceEqual(SHA256.HashData(File.ReadAllBytes(images[index])))) throw new Exception("Trial graph hash mismatch.");
                File.WriteAllBytes(Path.Combine(output, $"server-graph-{++index}.png"), bytes);
            }
            if (index != images.Length) throw new Exception("Missing stored trial graph.");
            using var official = await client.GetAsync("api/report/qa-audio?serial=" + serial);
            official.EnsureSuccessStatusCode();
            using var officialJson = JsonDocument.Parse(await official.Content.ReadAsStringAsync());
            if (officialJson.RootElement.GetProperty("data").GetProperty("total").GetInt32() != 0) throw new Exception("Trial leaked into production report.");
            Console.WriteLine($"PASS live trial HTTP {receipt.HttpStatus}: {serial}, {configuration.Phase}, {steps.Length} measured step(s), {index} graph(s), result {status}; independent readback/hash and production exclusion verified. Log {receipt.LogId}");
        });
    }
    private static async Task Authenticated(Func<HttpClient, Task> run)
    {
        var saved = ServerEngine.GetRememberedLogin() ?? throw new Exception("No saved login; server unchanged.");
        string server = ServerEngine.CurrentApiBaseUrl;
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("Origin", server);
        using var login = await client.PostAsJsonAsync("api/auth/login", new { email = saved.Account, password = saved.Password, deviceId = Guid.NewGuid().ToString(), deviceName = "Sonca trial workflow verification" });
        if (!login.IsSuccessStatusCode) throw new Exception("Saved-account login HTTP " + (int)login.StatusCode);
        using var credentials = JsonDocument.Parse(await login.Content.ReadAsStringAsync()); var data = credentials.RootElement.GetProperty("data");
        string access = data.GetProperty("accessToken").GetString()!, refresh = data.GetProperty("refreshToken").GetString()!;
        string payload = data.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        string sessionId = claims.RootElement.GetProperty("sessionId").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        void Set(string key, object value) => typeof(ServerEngine).GetProperty(key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
        Set("ApiKey", access); Set("ApiKeyExpiresAtUtc", DateTimeOffset.UtcNow.AddMinutes(10)); Set("StaffID", claims.RootElement.GetProperty("staffId").GetString()!);
        try { await run(client); }
        finally { using var logout = await client.PostAsJsonAsync("api/auth/logout", new { refreshToken = refresh, sessionId }); Console.WriteLine("Verification logout HTTP " + (int)logout.StatusCode); }
    }
    private static void Graph(JsonElement result, string path)
    {
        var points = result.GetProperty("response").EnumerateArray().Select(p => (Hz: p.GetProperty("hz").GetDouble(), Db: p.GetProperty("db").GetDouble()))
            .Where(p => p.Hz >= 20 && p.Hz <= 20000 && double.IsFinite(p.Db)).ToArray();
        if (points.Length < 3) throw new Exception("Missing measured response; no synthetic graph substituted.");
        var reference = result.GetProperty("reference").ValueKind == JsonValueKind.Object
            ? result.GetProperty("reference").EnumerateObject().Select(p => (Hz: double.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), Db: p.Value.GetDouble()))
                .Where(p => p.Hz >= 20 && p.Hz <= 20000 && double.IsFinite(p.Db)).ToArray() : Array.Empty<(double Hz, double Db)>();
        double minimum = Math.Floor(points.Concat(reference).Min(p => p.Db) / 10) * 10 - 5, maximum = Math.Ceiling(points.Concat(reference).Max(p => p.Db) / 10) * 10 + 5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 27)), null, new Rect(0, 0, 800, 450));
            void Text(string text, double x, double y, Brush brush, double size = 14) => dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
            Text("DỮ LIỆU THỬ · C360 · " + result.GetProperty("StageName").GetString() + " · " + result.GetProperty("Status").GetString(), 24, 14, Brushes.White, 18);
            Point Position(double hz, double db) => new(70 + 700 * Math.Log10(hz / 20) / 3, 380 - 300 * (db - minimum) / (maximum - minimum));
            foreach (double hz in new[] { 20d, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
            { var p = Position(hz, minimum); dc.DrawLine(new Pen(Brushes.DimGray, .5), p, new Point(p.X, 80)); Text(hz >= 1000 ? hz / 1000 + "k" : hz.ToString(), p.X - 10, 385, Brushes.LightGray, 11); }
            for (double db = minimum; db <= maximum; db += 10) { var p = Position(20, db); dc.DrawLine(new Pen(Brushes.DimGray, .5), p, new Point(770, p.Y)); Text(db.ToString("0"), 24, p.Y - 7, Brushes.LightGray, 11); }
            void Curve((double Hz, double Db)[] values, Brush brush)
            { for (int i = 1; i < values.Length; i++) dc.DrawLine(new Pen(brush, 1.5), Position(values[i - 1].Hz, values[i - 1].Db), Position(values[i].Hz, values[i].Db)); }
            Curve(reference, Brushes.Gray); Curve(points, Brushes.DeepSkyBlue);
            Text("Hz · log", 700, 415, Brushes.LightGray); Text("dBFS · đo", 24, 48, Brushes.DeepSkyBlue); Text(reference.Length > 0 ? "LINE CHUẨN THỬ" : "CHƯA CÓ CHUẨN ỔN ĐỊNH · CHƯA KẾT LUẬN", 160, 48, Brushes.Gray);
        }
        var bitmap = new RenderTargetBitmap(800, 450, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); png.Save(file);
    }
}
