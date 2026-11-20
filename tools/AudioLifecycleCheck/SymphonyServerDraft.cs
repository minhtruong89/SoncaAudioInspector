using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using SoncaAudioInspector;

internal static class SymphonyServerDraft
{
    public static async Task Run(string configFile, string output, bool activate = false)
    {
        Directory.CreateDirectory(output);
        var model = ModelConfigEditorWindow.ParseModel("SYMPHONY", File.ReadAllText(configFile));
        if (model.model != "SYMPHONY" || model.testItems.InOut.FirmwareModelId != 1062 || model.testItems.InOut.Tests.Count != 2)
            throw new InvalidOperationException("Chỉ gửi bản nháp SYMPHONY hai bài đã chuẩn bị.");
        var account = ServerEngine.GetRememberedLogin() ?? throw new InvalidOperationException("Chưa có tài khoản app đã nhớ để gửi bản nháp; file JSON vẫn được giữ.");
        string origin = ServerEngine.CurrentApiBaseUrl.TrimEnd('/');
        using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(origin + "/"), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("Origin", origin);
        using var login = await client.PostAsJsonAsync("api/auth/login", new { email = account.Account, password = account.Password, deviceId = Guid.NewGuid().ToString(), deviceName = "SYMPHONY configuration draft" });
        if (!login.IsSuccessStatusCode) throw new InvalidOperationException("Không đăng nhập được để gửi nháp: HTTP " + (int)login.StatusCode);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var auth = loginJson.RootElement.GetProperty("data");
        string token = auth.GetProperty("accessToken").GetString()!, refresh = auth.GetProperty("refreshToken").GetString()!;
        string sessionPayload = auth.GetProperty("sessionToken").GetString()!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var sessionJson = JsonDocument.Parse(Convert.FromBase64String(sessionPayload.PadRight((sessionPayload.Length + 3) / 4 * 4, '=')));
        string sessionId = sessionJson.RootElement.GetProperty("sessionId").GetString()!;
        try
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var keyResponse = await client.GetAsync("api/app/audio-config/public-key");
            if (!keyResponse.IsSuccessStatusCode) throw new InvalidOperationException("Chưa đọc được khóa công khai ký config: HTTP " + (int)keyResponse.StatusCode);
            using var keyJson = JsonDocument.Parse(await keyResponse.Content.ReadAsStringAsync());
            using var serverKey = ECDsa.Create();
            serverKey.ImportFromPem(keyJson.RootElement.GetProperty("data").GetProperty("publicKeyPem").GetString()!);
            using var pinnedKey = ECDsa.Create(); pinnedKey.ImportFromPem(AudioConfigSignature.PinnedPublicKey);
            bool pinnedPublicKeyMatches = serverKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(pinnedKey.ExportSubjectPublicKeyInfo());
            if (!pinnedPublicKeyMatches) throw new InvalidOperationException("Khóa ký config trên server chưa khớp khóa pin trong bản app này.");
            string modelJson = JsonSerializer.Serialize(model, ModelConfigEditorWindow.JsonOptions);
            using var listed = await client.GetAsync("api/app/audio-config/revisions?speakerModel=SYMPHONY");
            if (!listed.IsSuccessStatusCode) throw new InvalidOperationException("Không đọc được revision SYMPHONY: HTTP " + (int)listed.StatusCode);
            using var listJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var existing = listJson.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(row =>
                row.GetProperty("status").GetString() is "DRAFT" or "PENDING" or "ACTIVE"
                && JsonNode.DeepEquals(JsonNode.Parse(row.GetProperty("configJson").GetString()!), JsonNode.Parse(modelJson)));
            string id; string status;
            if (existing.ValueKind != JsonValueKind.Undefined)
            { id = existing.GetProperty("id").GetString()!; status = existing.GetProperty("status").GetString()!; }
            else
            {
                using var created = await client.PostAsJsonAsync("api/app/audio-config/revisions", new {
                    speakerModel = "SYMPHONY", config = JsonSerializer.SerializeToElement(model, ModelConfigEditorWindow.JsonOptions), appVersion = "web",
                    note = activate
                        ? "Theo yêu cầu người dùng: SYMPHONY LINE IN + BT, thu tạm Microphone (USB Audio Device), BLE check ở đầu ON, mức thử -24 dBFS. Cấu hình đường đo; kết luận âm học được ghi riêng sau khi đo."
                        : "SYMPHONY: LINE IN + BT, BLE check ở đầu mặc định ON. Đã xác minh firmware 1062 và chuyển/đọc lại LINE IN + BT. Chưa đo âm học: người dùng sẽ kết nối UMIK-1 sau. Mức thử -24 dBFS; không có kết luận PASS/FAIL loa." });
                if (!created.IsSuccessStatusCode) throw new InvalidOperationException("Không tạo được bản nháp: HTTP " + (int)created.StatusCode);
                using var receipt = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                id = receipt.RootElement.GetProperty("data").GetProperty("id").GetString()!; status = "DRAFT";
            }
            int? submitHttp = null;
            string? submitError = null;
            if (status == "DRAFT")
            {
                using var submitted = await client.PostAsJsonAsync("api/app/audio-config/revisions/" + Uri.EscapeDataString(id) + "/submit", new { });
                submitHttp = (int)submitted.StatusCode;
                if (submitted.IsSuccessStatusCode) status = "PENDING";
                else
                {
                    using var errorJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
                    submitError = errorJson.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "Không gửi duyệt được.";
                    Console.WriteLine("Đã giữ bản nháp; gửi duyệt HTTP " + submitHttp + ": " + submitError);
                }
            }
            if (activate && status == "PENDING")
            {
                using var approved = await client.PostAsJsonAsync("api/app/audio-config/revisions/" + Uri.EscapeDataString(id) + "/approve", new { });
                if (!approved.IsSuccessStatusCode) throw new InvalidOperationException("Không kích hoạt được cấu hình theo yêu cầu: HTTP " + (int)approved.StatusCode);
                status = "ACTIVE";
            }
            bool? signedActiveVerified = null;
            if (activate && status == "ACTIVE")
            {
                using var activeResponse = await client.GetAsync("api/app/audio-config/active"); activeResponse.EnsureSuccessStatusCode();
                using var activeJson = JsonDocument.Parse(await activeResponse.Content.ReadAsStringAsync());
                var activeRow = activeJson.RootElement.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("speakerModel").GetString() == model.model);
                var active = activeRow.Deserialize<ActiveAudioConfig>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var accepted = AudioConfigSignature.Verify(active, AudioConfigSignature.PinnedPublicKey);
                if (active.RevisionId != id || !JsonNode.DeepEquals(JsonNode.Parse(active.ConfigJson), JsonNode.Parse(modelJson)))
                    throw new InvalidOperationException("ACTIVE đã ký chưa khớp cấu hình ngõ thu USB đã yêu cầu.");
                var store = new ActiveAudioConfigStore(Path.Combine(SoncaAudioInspector.Update.ServerConfigSync.UserDataDirectory, "active-audio-config"), AudioConfigSignature.PinnedPublicKey);
                var warnings = new List<string>(); var cached = store.Load(warnings);
                store.Save(active, cached.GetValueOrDefault(model.model));
                File.WriteAllText(Path.Combine(output, "SYMPHONY.active.json"), JsonSerializer.Serialize(active, new JsonSerializerOptions { WriteIndented = true }));
                signedActiveVerified = true;
                Console.WriteLine("Đã xác minh chữ ký ACTIVE, cập nhật cache app: " + accepted.model + " · " + accepted.testItems.InOut.Devices.Output.Values.Single());
            }
            if (activate && (status != "ACTIVE" || signedActiveVerified != true))
                throw new InvalidOperationException("Chưa kích hoạt và xác minh được SYMPHONY: " + status + ". " + submitError);
            using var readback = await client.GetAsync("api/app/audio-config/revisions?speakerModel=SYMPHONY");
            readback.EnsureSuccessStatusCode();
            using var verified = JsonDocument.Parse(await readback.Content.ReadAsStringAsync());
            var row = verified.RootElement.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);
            if (row.GetProperty("status").GetString() != status)
                throw new InvalidOperationException("Trạng thái revision đọc lại không khớp: " + row.GetProperty("status").GetString());
            if (!JsonNode.DeepEquals(JsonNode.Parse(row.GetProperty("configJson").GetString()!), JsonNode.Parse(modelJson)))
                throw new InvalidOperationException("Bản đọc lại SYMPHONY không khớp JSON đã gửi.");
            File.WriteAllText(Path.Combine(output, "server-draft-receipt.json"), JsonSerializer.Serialize(new { model = "SYMPHONY", revisionId = id, status,
                independentReadbackMatches = true, pinnedPublicKeyMatches, signedActiveVerified, submitHttp, submitError, configActivatedOnUserRequest = activate && status == "ACTIVE", url = origin + "/config?model=SYMPHONY" }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Đã lưu và đọc lại SYMPHONY trên server: " + id + " · " + status);
        }
        finally
        {
            using var logout = await client.PostAsJsonAsync("api/auth/logout", new { refreshToken = refresh, sessionId });
            Console.WriteLine("Đã kết thúc phiên gửi nháp: HTTP " + (int)logout.StatusCode);
        }
    }
}
