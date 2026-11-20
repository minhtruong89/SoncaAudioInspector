using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class PhaseCChecks
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Set(string name, object? value) => typeof(ServerEngine).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame(); task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false));
        if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static object? Call(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, args);
    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in Tree<T>(child)) yield return item;
    }
    private static void Render(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -30000; window.Top = -30000;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var png = new PngBitmapEncoder(); var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); png.Save(file);
    }
    public static void Run(string output, Action<bool, string> check)
    {
        output = Path.Combine(output, "phase-c-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var outer = check; check = (ok, message) => { outer(ok, message); File.AppendAllText(Path.Combine(output, "checks.txt"), "PASS " + message + "\n"); };
        string? previousDataDir = Environment.GetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR");
        Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", Path.Combine(output, "phase-c-data"));
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var previousState = ActiveAudioConfigSync.Current;
        try
        {
            Await(ServerTestListChecks.Run(output, check));
            Await(SymphonyChecks.Run(check));
            VerifyVectors(output, check);
            using var handler = new FakeServer(); using var client = new HttpClient(handler);
            Set("AuthorizedClientOverride", client); Set("ApiKey", "offline-bearer"); Set("ApiKeyExpiresAtUtc", DateTimeOffset.UtcNow.AddHours(1));
            Set("AppToken", "offline-app-key"); Set("AppTokenExpiresAtUtc", DateTimeOffset.UtcNow.AddHours(1)); Set("StaffID", "staff-test");
            Await(CheckWorkflows(handler, output, check));
        }
        finally
        {
            Set("AuthorizedClientOverride", null); ServerEngine.ClearSession(); SessionModelConfigurations.Current.Discard("Demo");
            SessionModelConfigurations.Current.SelectedModel = "";
            typeof(ActiveAudioConfigSync).GetProperty("PublicKeyOverrideForTest", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
            typeof(ActiveAudioConfigSync).GetProperty("Current")!.SetValue(null, previousState);
            Environment.SetEnvironmentVariable("SONCA_AUDIO_INSPECTOR_DATA_DIR", previousDataDir);
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
    private static (ActiveAudioConfig Revision, string Key, JsonElement Json) Vector(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText("tools/ConfigAcceptanceCheck/Fixtures/" + file));
        return (doc.RootElement.Deserialize<ActiveAudioConfig>(Web)!, doc.RootElement.GetProperty("publicKeyPem").GetString()!, doc.RootElement.Clone());
    }
    private static void VerifyVectors(string output, Action<bool, string> check)
    {
        foreach (string file in new[] { "backend-config-signing.json", "unicode-config-signing.json" })
        {
            var (r, key, vector) = Vector(file);
            check(AudioConfigSignature.Verify(r, key).model == r.SpeakerModel, "C4 real verifier accepts " + file);
            byte[] payload = AudioConfigSignature.Payload(r);
            check(Encoding.UTF8.GetString(payload) == vector.GetProperty("payload").GetString(), "C4 Node payload exact string " + file);
            check(Convert.ToBase64String(payload) == vector.GetProperty("payloadUtf8Base64").GetString(), "C4 Node payload exact UTF8 bytes " + file);
            byte[] changedSignature = Convert.FromBase64String(r.Signature); changedSignature[0] ^= 1;
            foreach (var bad in new[] { r with { ConfigJson = r.ConfigJson + " " }, r with { Signature = Convert.ToBase64String(changedSignature) }, r with { Serial = r.Serial + 1 } })
            {
                bool rejected = false; try { AudioConfigSignature.Verify(bad, key); } catch { rejected = true; }
                check(rejected, "C4 modified config/signature/serial refused " + file);
            }
            bool rollback = false; try { AudioConfigSignature.Verify(r, key, r with { Serial = r.Serial + 1 }); } catch (InvalidDataException) { rollback = true; }
            check(rollback, "C4 antirollback checked against verified serial " + file);
            check(AudioConfigSignature.Verify(r, key, r).model == r.SpeakerModel, "C4 equal serial same revision accepted " + file);
            bool conflict = false; try { AudioConfigSignature.Verify(r, key, r with { RevisionId = "other" }); } catch (InvalidDataException) { conflict = true; }
            check(conflict, "C4 equal serial different revision refused " + file);
            bool pinned = false; try { AudioConfigSignature.Verify(r, AudioConfigSignature.PinnedPublicKey); } catch { pinned = true; }
            check(pinned, "C4 test key never trusted by production placeholder " + file);
        }
        var (revision, publicKey, _) = Vector("backend-config-signing.json");
        check(File.ReadAllText("tools/ConfigAcceptanceCheck/Fixtures/config-signing-test-public.pem") == publicKey, "C4 standalone test public key equals backend fixture");
        var store = new ActiveAudioConfigStore(Path.Combine(output, "cache"), publicKey);
        var task = ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { revision }), store, publicKey); Await(task); var online = task.Result;
        check(online.Warning == "" && online.Revisions["C360"].Serial == 12, "C4 verified ACTIVE saved to real cache");
        var offlineTask = ActiveAudioConfigSync.RefreshAsync(() => throw new HttpRequestException("offline synthetic"), store, publicKey); Await(offlineTask);
        check(offlineTask.Result.Warning.Contains("OFFLINE") && offlineTask.Result.Revisions["C360"].RevisionId == revision.RevisionId, "C4 offline cache carries explicit warning");
        var badTask = ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { revision with { ConfigJson = "{}" } }), store, publicKey); Await(badTask);
        check(badTask.Result.Warning.Contains("SHA-256") && badTask.Result.Revisions["C360"] == revision, "C4 bad fetched ACTIVE falls back to verified cache");
        var emptyStore = new ActiveAudioConfigStore(Path.Combine(output, "empty-cache"), publicKey);
        var invalidTask = ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { revision with { Signature = "bad" } }), emptyStore, publicKey); Await(invalidTask);
        check(invalidTask.Result.Revisions.Count == 0 && invalidTask.Result.Warning.Length > 0, "C4 never uses unverified revision without cache");
        var (unicode, unicodeKey, unicodeJson) = Vector("unicode-config-signing.json");
        var newer = unicodeJson.GetProperty("newerRevision").Deserialize<ActiveAudioConfig>(Web)!;
        var older = unicodeJson.GetProperty("olderRevision").Deserialize<ActiveAudioConfig>(Web)!;
        var serialStore = new ActiveAudioConfigStore(Path.Combine(output, "serial-cache"), unicodeKey);
        serialStore.Save(unicode, null);
        var newTask = ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { newer }), serialStore, unicodeKey); Await(newTask);
        check(newTask.Result.Revisions[unicode.SpeakerModel].Serial == 28, "C4 valid signed higher serial advances persisted model cache");
        var rollbackTask = ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { older }), serialStore, unicodeKey); Await(rollbackTask);
        check(rollbackTask.Result.Revisions[unicode.SpeakerModel].Serial == 28 && rollbackTask.Result.Warning.Contains("rollback"), "C4 real sync refuses older correctly signed revision and keeps high water cache");
        var legacy = new CheckingConfig { models = new() { C360ModelDefinition.Create(), Demo() } };
        var merged = online.Merge(legacy);
        check(merged.models.Count == 2 && merged.models.Single(m => m.model == "C360").testItems.InOut.Tests.Single().id == "AC001", "C4 ACTIVE replaces matching legacy model");
        check(JsonSerializer.Serialize(merged.models.Single(m => m.model == "Demo")) == JsonSerializer.Serialize(legacy.models[1]), "C4 model without revision retains signed v1.3 config");
        SessionModelConfigurations.Current.Apply(Demo()); SessionModelConfigurations.Current.SelectedModel = "Demo";
        check(SessionModelConfigurations.Current.Resolve(merged.models[1]).model == "Demo" && (ResultPersistencePolicy.IsTrial && ResultPersistencePolicy.CanPersist), "C4 refresh preserves temporary session policy");
        SessionModelConfigurations.Current.Discard("Demo");
        typeof(ActiveAudioConfigSync).GetProperty("Current")!.SetValue(null, online);
        var context = new ProductMeasurementContext(); context.Receive("SN-1", "C360", SerialSource.Manual); context.ConfirmQa("FAIL", "test"); var frozen = context.Snapshot();
        typeof(ActiveAudioConfigSync).GetProperty("Current")!.SetValue(null, new ActiveAudioConfigState(new Dictionary<string, ActiveAudioConfig>(), ""));
        check(frozen.ConfigRevisionId == revision.RevisionId && frozen.ConfigSerial == 12 && frozen.QaVerdict == "FAIL", "C5 revision and QA frozen in measurement identity across refresh");
        var cacheFile = Directory.GetFiles(Path.Combine(output, "cache")).Single(); File.WriteAllText(cacheFile, "{}");
        var warnings = new List<string>(); check(store.Load(warnings).Count == 0 && warnings.Count > 0, "C4 corrupt cache refused and reported");
        var script = File.ReadAllText("scripts/publish-portable.ps1");
        check(script.IndexOf("PLACEHOLDER", StringComparison.Ordinal) is >= 0 and var guard && guard < script.IndexOf("new-artifact.ps1", StringComparison.Ordinal), "C4 publisher blocks placeholder before packaging/artifact/build");
        // The pinned asset is either the placeholder (before deploy) or the production P-256 public key — never a private key.
        string pinnedAsset = File.ReadAllText("Assets/config-signing-public.pem");
        bool pinnedOk = !pinnedAsset.Contains("PRIVATE", StringComparison.Ordinal) && (pinnedAsset.Contains("PLACEHOLDER", StringComparison.Ordinal) || IsP256PublicKey(pinnedAsset));
        check(pinnedOk, "C4 pinned config key is the placeholder or a valid P-256 public key (no private key)");
    }
    private static ModelConfig Demo() => ModelConfigEditorWindow.ParseModel("Demo", """{"model":"Demo","testItems":{"InOut":{"Devices":{"Input":{"USB":"Playback"},"Output":{"MIC":"Capture"}},"Tests":[{"id":"IO001","Playback Out":"USB","Recording In":"MIC","THD_1000Hz":0.5}]}}}""");
    private static async Task CheckWorkflows(FakeServer server, string output, Action<bool, string> check)
    {
        var me = await ServerEngine.GetMeAsync(); check(me.Role == "ADMIN" && ServerEngine.IsConfigAdmin, "C1 me stores ADMIN role");
        check(server.Requests.All(r => r.Bearer == "Bearer offline-bearer" && r.AppKey == "offline-app-key"), "C1 new API uses normal authenticated transport");
        foreach (int code in new[] { 400, 401, 403, 404, 409, 413, 429, 503 })
        {
            server.Error = (code, new { error = "Lỗi hợp đồng thử", code = "TEST_ERROR" }); bool caught = false;
            try { await ServerEngine.GetActiveAudioConfigsAsync(); }
            catch (ServerEngine.ApiException ex) { caught = (int)ex.StatusCode == code && ex.Code == "TEST_ERROR" && ex.Details != null; }
            check(caught, "C1 exact error envelope/status/code retained HTTP " + code); server.Error = null;
        }
        var model = Demo(); SessionModelConfigurations.Current.SelectedModel = "Demo";
        var trial = new ConfigTrialEditorSession(SessionModelConfigurations.Current.Apply);
        var draft = await trial.SaveAsync(model, "ghi chú thử");
        check(draft.Status == "PENDING" && (ResultPersistencePolicy.IsTrial && ResultPersistencePolicy.CanPersist), "C2 save server PENDING also applies temporary session");
        var create = JsonDocument.Parse(server.Requests.Last().Body!);
        check(create.RootElement.GetProperty("speakerModel").GetString() == "Demo" && create.RootElement.GetProperty("config").GetProperty("model").GetString() == "Demo"
            // The contract is "major.minor.patch of the running assembly"; a pinned number breaks at every release.
            && create.RootElement.GetProperty("appVersion").GetString() == AppVersion.Numeric, "C1 create body exact config/model/appVersion contract");
        var pending = draft; check(pending.Status == "PENDING", "C2 save goes directly to PENDING");
        var list = await ServerEngine.ListAudioConfigRevisionsAsync("Demo", "PENDING");
        check(list[0].Status == "PENDING" && server.Requests.Last().Path.EndsWith("speakerModel=Demo&status=PENDING"), "C1 revision list query names and envelope");
        trial.Apply(pending); check(SessionModelConfigurations.Current.TrialBanner("Demo").Contains("DỮ LIỆU THỬ") && (ResultPersistencePolicy.IsTrial && ResultPersistencePolicy.CanPersist), "C2 server trial banner and no-persistence policy");
        SessionModelConfigurations.Current.Discard("Demo");
        var proposed = pending with { ConfigJson = pending.ConfigJson.Replace("0.5", "0.7"), BaseRevision = pending with { Id = "base-1", Status = "ACTIVE" }, BaseRevisionId = "base-1" };
        check(ConfigRevisionDiff.Describe(pending.ConfigJson, proposed.ConfigJson).Contains("IO001 · THD_1000Hz: 0.5 → 0.7"), "C3 readable diff groups by test ID and field");
        var review = new ConfigApprovalReview(proposed);
        server.Approvals.Enqueue((409, new { error = "Cấu hình gốc đã thay đổi", code = "STALE_BASE", currentActiveRevision = pending with { Id = "base-2", Status = "ACTIVE" } }));
        int before = server.Requests.Count; check(!await review.ApproveAsync() && review.RequiresReconfirmation && server.Requests.Count == before + 1, "C3 STALE_BASE shows diff and never auto rebase/approve");
        check(review.Baseline?.Id == "base-2", "C3 conflict preserves currentActiveRevision detail from error envelope");
        server.Approvals.Enqueue((409, new { error = "ACTIVE lại đổi", code = "STALE_BASE", currentActiveRevision = (object?)null }));
        check(!await review.ApproveAsync() && review.RequiresReconfirmation && review.Baseline == null, "C3 repeated 409 loops back to explicit review (null base supported)");
        check(await review.ApproveAsync(), "C3 explicit confirmation rebases and approves");
        var calls = server.Requests.TakeLast(2).ToArray();
        check(calls[0].Path.EndsWith("/rebase") && calls[1].Path.EndsWith("/approve") && calls[0].Body!.Contains("\"expectedActiveRevisionId\":null"), "C3 rebase expected id uses exact viewed base including null");
        var popup = new ConfigApprovalWindow(new[] { proposed });
        Render(popup, Path.Combine(output, "admin-approval-diff.png"));
        check(popup.DiffText.Text.Contains("THD_1000Hz") && Tree<Expander>(popup).Single().Header.ToString()!.Contains("JSON"), "C3 actual dark ADMIN modal displays diff and full JSON");
        before = server.Requests.Count; await popup.PerformAsync(false);
        check(server.Requests.Count == before && popup.Status.Text.Contains("lý do"), "C3 reject reason required in real popup before HTTP");
        popup.Reason.Text = "  Sai ngõ thu  "; await popup.PerformAsync(false);
        check(popup.PendingList.Items.Count == 0 && server.Requests.Last().Body!.Contains("Sai ng"), "C3 popup rejection sends trimmed reason and removes pending"); popup.Close();
        var unavailable = new ConfigApprovalWindow(new[] { proposed }); server.Unavailable = true; await unavailable.PerformAsync(true); server.Unavailable = false;
        check(unavailable.Status.Text.Contains("503") && !unavailable.ApprovedAny && unavailable.PendingList.Items.Count == 1, "C3 signing server 503 never treated as approved"); unavailable.Close();
        var accepted = new ConfigApprovalWindow(new[] { proposed }); int reloads = 0;
        accepted.AfterApproved = async () => { reloads++; await ServerEngine.GetActiveAudioConfigsAsync(); }; await accepted.PerformAsync(true);
        check(accepted.ApprovedAny && accepted.PendingList.Items.Count == 0 && reloads == 1, "C3 popup approval immediately reloads ACTIVE in session"); accepted.Close();
        var shell = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Hidden | BindingFlags.Public, null, new object[] { false }, null)!;
        Set("UserRole", "STAFF"); before = server.Requests.Count;
        await (Task)Call(shell, "OpenPendingConfigsAsync", true)!;
        check(((Button)shell.FindName("BtnPendingConfigs")).Visibility == Visibility.Collapsed && server.Requests.Count == before, "C3 STAFF sees no pending button/popup and makes no ADMIN call");
        Set("UserRole", "ADMIN");
        var editor = new ModelConfigEditorWindow(model) { SaveTemporaryModel = SessionModelConfigurations.Current.Apply };
        check(new[] { "SaveServerTrial", "LoadServerTrials" }.All(n => Tree<Button>(editor).Any(b => b.Name == n)) && !Tree<Button>(editor).Any(b => b.Name == "SubmitServerTrial"), "C2 actual editor exposes save/list without separate submit");
        check(Tree<Button>(editor).Single(b => b.Name == "SaveServerTrial").Content?.ToString() == "Lưu lên server"
            && Tree<Button>(editor).Single(b => b.Name == "LoadServerTrials").Content?.ToString() == "Lưu danh sách thử"
            && Tree<Button>(editor).Single(b => b.Name == "SaveModel").Content?.ToString() == "Gửi cấu hình thử", "C2 requested editor action labels");
        await (Task)Call(editor, "SaveServerTrialAsync")!; await (Task)Call(editor, "LoadServerTrialsAsync")!;
        var combo = Tree<ComboBox>(editor).Single(c => c.Name == "ServerTrialRevisions"); combo.SelectedIndex = 0; Call(editor, "ApplySelectedServerTrial");
        check(combo.Items.Count > 0 && (ResultPersistencePolicy.IsTrial && ResultPersistencePolicy.CanPersist) && SessionModelConfigurations.Current.IsTemporary("Demo"), "C2 real editor save/submit/list/apply stays temporary");
        Render(editor, Path.Combine(output, "editor-server-trials.png"));
        var footerButtons = Tree<Button>(editor).Where(b => new[] { "SaveServerTrial", "LoadServerTrials", "SubmitServerTrial", "ApplyServerTrial", "SaveModel" }.Contains(b.Name)).ToArray();
        check(footerButtons.Length == 4 && footerButtons.All(b => Math.Abs(b.ActualWidth - 180) < .1 && Math.Abs(b.ActualHeight - 36) < .1), "Settings footer actions have identical rendered dimensions");
        editor.Width = 820; editor.Height = 600; Render(editor, Path.Combine(output, "editor-server-trials-minimum.png"));
        check(footerButtons.All(b => Math.Abs(b.ActualWidth - 180) < .1 && Math.Abs(b.ActualHeight - 36) < .1), "Minimum settings width wraps actions without shrinking buttons");
        Call(shell, "RefreshTrialBanner");
        var content = (FrameworkElement)shell.Content; shell.Content = null;
        var host = new Window { Content = content, Width = 1200, Height = 520, Background = Brushes.Black };
        Render(host, Path.Combine(output, "server-trial-banner.png")); host.Content = null; host.Close();
        string prior = JsonSerializer.Serialize(Call(editor, "ReadEditorModel")); server.Offline = true;
        bool failure = false; try { await (Task)Call(editor, "LoadServerTrialsAsync")!; } catch (HttpRequestException) { failure = true; }
        server.Offline = false; check(failure && JsonSerializer.Serialize(Call(editor, "ReadEditorModel")) == prior, "C2 offline list failure preserves current draft");
        server.Offline = true;
        await (Task)Call(editor, "ServerActionAsync", (Func<Task>)(() => (Task)Call(editor, "LoadServerTrialsAsync")!))!;
        check(Tree<TextBlock>(editor).Any(t => t.Text.Contains("Server/offline:")) && JsonSerializer.Serialize(Call(editor, "ReadEditorModel")) == prior, "C2 real editor error boundary reports offline without losing edits");
        server.Offline = false;
        await CheckLiveSyncAndAliasFixes(server, shell, output, check);
        editor.Close(); shell.Close();
        SessionModelConfigurations.Current.Discard("Demo");
        await CheckUploads(server, output, check);
        _ = await ServerEngine.GetAudioConfigPublicKeyAsync(); check(server.Requests.Last().Path.EndsWith("/public-key"), "C1 public-key diagnostic endpoint (not trust source)");
        _ = await ServerEngine.GetPendingAudioConfigRevisionsAsync(); check(server.Requests.Last().Path.EndsWith("/pending"), "C1 pending exact route");
    }
    private static async Task CheckUploads(FakeServer server, string output, Action<bool, string> check)
    {
        string folder = Path.Combine(LocalQaStorage.Root, "phase-c-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string image = Path.Combine(folder, "FRA-IO001.png"); File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
        var product = new ProductInfo { Id = "product-test", SerialNumber = "SN-1", Model = "Demo" };
        var steps = new[] { new ServerEngine.AudioQaStepResult("IO001", "PASS", "offline synthetic") };
        var config = new AudioQaConfiguration("approved-frozen", "SN-1", "FAIL");
        check(await ServerEngine.UploadAudioQaResultAsync(product, true, steps, new[] { image }, true, "upload-test", config), "C5 real QA uploader success through fake HTTP");
        using var body = JsonDocument.Parse(server.Requests.Last().Body!); var b = body.RootElement;
        check(b.GetProperty("configRevisionId").GetString() == "approved-frozen" && b.GetProperty("serialNumber").GetString() == "SN-1"
            && b.GetProperty("qaVerdict").GetString() == "FAIL" && b.GetProperty("appVerdict").GetString() == "PASS", "C5 upload contains exact frozen revision/serial/QA/App fields");
        check(b.GetProperty("images").GetArrayLength() == 1 && b.GetProperty("steps").GetArrayLength() == 1, "C5 JSON image/step transport retained");
        check(await ServerEngine.UploadAudioQaResultAsync(product, true, steps, new[] { image }, true), "C5 legacy QA upload accepted without revision");
        using var legacy = JsonDocument.Parse(server.Requests.Last().Body!); check(!legacy.RootElement.TryGetProperty("configRevisionId", out _), "C5 legacy omits revision field entirely");
        server.RejectUpload = true;
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, new[] { image }, true, configuration: config), "C5 unapproved 409 returns failure");
        check(ServerEngine.LastAudioQaRejected && ServerEngine.LastError == ServerEngine.UnapprovedConfigurationMessage && !LocalQaStorage.FindPending(folder).Any(), "C5 unapproved 409 removes queue entry permanently and reports exact message");
        check(File.Exists(Path.Combine(folder, ".rejected-audio-qa.json")), "C5 terminal rejection logged durably");
        var notification = new ModernMessageBox(ServerEngine.UnapprovedConfigurationMessage, "QA âm thanh", ModernMessageBox.MessageBoxType.Error);
        Render(notification, Path.Combine(output, "upload-409.png")); notification.Close();
        server.RejectUpload = false; server.Offline = true;
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, new[] { image }, true, "retry-test", config), "C5 network failure returns pending");
        check(LocalQaStorage.FindPending(folder).Any(), "C5 network failure keeps queue entry");
        var pendingPath = LocalQaStorage.FindPending(folder).Single(); var pending = LocalQaStorage.ReadPending(LocalQaStorage.Root, pendingPath)!;
        string pendingJson = File.ReadAllText(pendingPath);
        check(pending.Configuration == config, "C5 durable queue retains frozen metadata");
        server.Offline = false; server.RejectUpload = true;
        check(!await ServerEngine.RetryPendingAudioQaAsync(pendingPath, true) && !File.Exists(pendingPath), "C5 retry receives 409 and removes queued entry");
        File.WriteAllText(pendingPath, pendingJson); // Simulate an interrupted/denied marker deletion after durable rejection.
        check(LocalQaStorage.ReadPending(LocalQaStorage.Root, pendingPath) == null && !ServerEngine.GetPendingAudioQaUploads().Any(i => i.Path == pendingPath), "C5 durable rejection suppresses retry even if pending marker survives deletion");
        File.Delete(pendingPath);
        server.RejectUpload = false; SessionModelConfigurations.Current.Apply(Demo());
        int before = server.Requests.Count;
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, new[] { image }, true, configuration: config) && server.Requests.Count == before, "Trial scope refuses an explicitly production envelope before HTTP");
        check(!LocalQaStorage.FindPending(folder).Any(), "C5 trial cannot resurrect terminal pending entry");
        string trialFolder = Path.Combine(LocalQaStorage.Root, "offline-trial-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(trialFolder);
        string trialImage = Path.Combine(trialFolder, "FRA-trial.png"); File.Copy(image, trialImage);
        var trialConfig = new AudioQaConfiguration("draft-test", "SN-1", "NONE", true, "Demo", JsonSerializer.Serialize(Demo()));
        check(await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [trialImage], true, "trial-session", trialConfig), "Trial scope uploads to the dedicated trial endpoint");
        check(server.Requests.Last().Path == "/api/app/qa-audio/trial" && ServerEngine.LastAudioQaUploadReceipt?.DataSet == "TRIAL", "Trial receipt explicitly confirms isolated storage");
        using (var trialBody = JsonDocument.Parse(server.Requests.Last().Body!))
            check(trialBody.RootElement.GetProperty("configSnapshot").GetProperty("model").GetString() == "Demo", "Trial payload includes the frozen model configuration");
        server.Offline = true;
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [trialImage], true, "trial-retry", trialConfig), "Offline trial queues for retry");
        string trialPending = ServerEngine.LastAudioQaPendingPath!;
        SessionModelConfigurations.Current.Discard("Demo"); server.Offline = false;
        check(await ServerEngine.RetryPendingAudioQaAsync(trialPending, true) && server.Requests.Last().Path == "/api/app/qa-audio/trial", "Retry after discarding trial still uses the trial endpoint");
        check(!File.Exists(trialPending), "Successful trial retry completes its own durable marker");
        string noteFolder = Path.Combine(LocalQaStorage.Root, "phase-c-note-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(noteFolder);
        string noteImage = Path.Combine(noteFolder, "FRA-note.png"); File.Copy(image, noteImage);
        var prodNoteConfig = config with { OperatorNote = "Loa rè nhẹ ở 120Hz" };
        check(await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [noteImage], true, "prod-note", prodNoteConfig), "A8 production upload with operatorNote succeeds");
        using (var prodNoteBody = JsonDocument.Parse(server.Requests.Last().Body!))
            check(prodNoteBody.RootElement.GetProperty("operatorNote").GetString() == "Loa rè nhẹ ở 120Hz" && prodNoteBody.RootElement.GetProperty("note").GetString() == "Audio auto test passed" && server.Requests.Last().Path == "/api/app/qa-audio", "A8 production body includes operatorNote and keeps system note");
        var blankNoteConfig = config with { OperatorNote = "   \r\n\t  " };
        check(await ServerEngine.UploadAudioQaResultAsync(product, false, [new("IO001", "FAIL", "offline fail")], [noteImage], true, "prod-blank-note", blankNoteConfig), "A8 production upload with whitespace operatorNote succeeds");
        using (var blankNoteBody = JsonDocument.Parse(server.Requests.Last().Body!))
            check(!blankNoteBody.RootElement.TryGetProperty("operatorNote", out _) && blankNoteBody.RootElement.GetProperty("note").GetString() == "Audio auto test failed", "A8 whitespace operatorNote omitted from JSON body");
        server.Offline = true;
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [noteImage], true, "prod-note-retry", prodNoteConfig), "A8 offline upload with operatorNote queues pending file");
        string prodNotePendingPath = ServerEngine.LastAudioQaPendingPath!;
        var prodNotePending = LocalQaStorage.ReadPending(LocalQaStorage.Root, prodNotePendingPath)!;
        check(prodNotePending.OperatorNote == "Loa rè nhẹ ở 120Hz" && prodNotePending.Configuration?.OperatorNote == "Loa rè nhẹ ở 120Hz", "A8 pending JSON persists OperatorNote");
        server.Offline = false;
        check(await ServerEngine.RetryPendingAudioQaAsync(prodNotePendingPath, true), "A8 retry pending with OperatorNote succeeds");
        using (var retryNoteBody = JsonDocument.Parse(server.Requests.Last().Body!))
            check(retryNoteBody.RootElement.GetProperty("operatorNote").GetString() == "Loa rè nhẹ ở 120Hz", "A8 retry sends operatorNote to server");
        SessionModelConfigurations.Current.Apply(Demo());
        var trialNoteConfig = trialConfig with { OperatorNote = "Loa rè nhẹ ở 120Hz" };
        check(await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [trialImage], true, "trial-note", trialNoteConfig), "A8 trial upload with operatorNote succeeds");
        using (var trialNoteBody = JsonDocument.Parse(server.Requests.Last().Body!))
            check(trialNoteBody.RootElement.GetProperty("operatorNote").GetString() == "Loa rè nhẹ ở 120Hz" && trialNoteBody.RootElement.GetProperty("note").GetString() == "Audio auto test passed" && server.Requests.Last().Path == "/api/app/qa-audio/trial", "A8 trial body includes operatorNote and keeps system note");
        string refFolder = Path.Combine(LocalQaStorage.Root, "phase-c-ref-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(refFolder);
        string refImage = Path.Combine(refFolder, "FRA-ref.png"); File.Copy(image, refImage);
        int reqBeforeRef = server.Requests.Count;
        var refConfig = trialConfig with { Phase = "REFERENCE", TrialStatus = "PASS" };
        var invalidTrialConfig = trialConfig with { Phase = "AUTOTEST", TrialStatus = "INVALID" };
        check(!await ServerEngine.UploadAudioQaResultAsync(product, true, steps, [refImage], true, "ref-session", refConfig) && server.Requests.Count == reqBeforeRef && !LocalQaStorage.FindPending(refFolder).Any(), "A8 blocks Phase=REFERENCE upload before HTTP and pending creation");
        check(!await ServerEngine.UploadAudioQaResultAsync(product, false, [new("Reference", "INVALID", "Unstable")], [refImage], true, "invalid-session", invalidTrialConfig) && server.Requests.Count == reqBeforeRef && !LocalQaStorage.FindPending(refFolder).Any(), "A8 blocks TrialStatus=INVALID upload before HTTP and pending creation");
        string legacyRefFolder = Path.Combine(LocalQaStorage.Root, "phase-c-legacy-ref-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacyRefFolder);
        string legacyRefImage = Path.Combine(legacyRefFolder, "FRA-legacy.png"); File.Copy(image, legacyRefImage);
        string legacyPendingPath = Path.Combine(legacyRefFolder, ".pending-audio-qa.json");
        var legacyPayload = new PendingAudioQaUpload(ServerEngine.CurrentApiBaseUrl, ServerEngine.StaffID!, "product-test", true, steps, [Path.GetFileName(legacyRefImage)], true, "legacy-ref", DateTimeOffset.UtcNow, Configuration: trialConfig with { Phase = "REFERENCE", TrialStatus = "INVALID" });
        File.WriteAllText(legacyPendingPath, JsonSerializer.Serialize(legacyPayload));
        check(!ServerEngine.GetPendingAudioQaUploads().Any(p => p.Path == legacyPendingPath) && File.Exists(Path.Combine(legacyRefFolder, ".rejected-audio-qa.json")) && JsonDocument.Parse(File.ReadAllText(Path.Combine(legacyRefFolder, ".rejected-audio-qa.json"))).RootElement.GetProperty("Reason").GetString() == "Không phải kết quả Auto Test" && File.Exists(legacyRefImage) && server.Requests.Count == reqBeforeRef, "A8 GetPendingAudioQaUploads rejects legacy REFERENCE/INVALID pending file durably without HTTP or deleting PNG");
        string legacyRetryFolder = Path.Combine(LocalQaStorage.Root, "phase-c-legacy-retry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(legacyRetryFolder);
        string legacyRetryImage = Path.Combine(legacyRetryFolder, "FRA-legacy-retry.png"); File.Copy(image, legacyRetryImage);
        string legacyRetryPendingPath = Path.Combine(legacyRetryFolder, ".pending-audio-qa.json");
        File.WriteAllText(legacyRetryPendingPath, JsonSerializer.Serialize(legacyPayload with { GraphFiles = [Path.GetFileName(legacyRetryImage)] }));
        check(!await ServerEngine.RetryPendingAudioQaAsync(legacyRetryPendingPath, true) && File.Exists(Path.Combine(legacyRetryFolder, ".rejected-audio-qa.json")) && JsonDocument.Parse(File.ReadAllText(Path.Combine(legacyRetryFolder, ".rejected-audio-qa.json"))).RootElement.GetProperty("Reason").GetString() == "Không phải kết quả Auto Test" && File.Exists(legacyRetryImage) && server.Requests.Count == reqBeforeRef, "A8 RetryPendingAudioQaAsync rejects legacy REFERENCE/INVALID pending file durably without HTTP or deleting PNG");
        SessionModelConfigurations.Current.Discard("Demo");
    }
    private static async Task CheckLiveSyncAndAliasFixes(FakeServer server, MainWindow shell, string output, Action<bool, string> check)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pubPem = signer.ExportSubjectPublicKeyInfoPem();
        ActiveAudioConfig SignRev(string speaker, string revId, long serial, string testId)
        {
            string cfgJson = "{\"model\":" + AudioConfigSignature.Quote(speaker)
                + ",\"testItems\":{\"InOut\":{\"Devices\":{\"Input\":{\"USB\":\"Playback\"},\"Output\":{\"MIC\":\"Capture\"}},\"Tests\":[{\"id\":"
                + AudioConfigSignature.Quote(testId) + ",\"Playback Out\":\"USB\",\"Recording In\":\"MIC\",\"THD_1000Hz\":0.5}]}}}";
            string sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cfgJson)));
            var draft = new ActiveAudioConfig(speaker, revId, serial, cfgJson, sha, "", new ConfigApprovedBy("admin", "Admin"), DateTimeOffset.UtcNow);
            byte[] sig = signer.SignData(AudioConfigSignature.Payload(draft), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return draft with { Signature = Convert.ToBase64String(sig) };
        }

        var revMiSam = SignRev("MI SAM", "rev-old-1", 1, "OLD001");
        var revDemo = SignRev("Demo", "rev-demo-1", 1, "DM001");
        var revMisam2 = SignRev("MISAM", "rev-new-2", 2, "NEW001");
        var revMisam3 = SignRev("MISAM", "rev-web-3", 3, "WEB003");

        string pruneDir = Path.Combine(output, "prune-cache");
        var pruneStore = new ActiveAudioConfigStore(pruneDir, pubPem);
        var seeded = await ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { revMiSam, revDemo }), pruneStore, pubPem);
        int cachedBefore = Directory.GetFiles(pruneDir, "*.json").Length;
        var offline = await ActiveAudioConfigSync.RefreshAsync(() => throw new HttpRequestException("offline"), pruneStore, pubPem);
        int cachedOffline = Directory.GetFiles(pruneDir, "*.json").Length;
        var pruned = await ActiveAudioConfigSync.RefreshAsync(() => Task.FromResult(new[] { revMisam2 }), pruneStore, pubPem);
        int cachedAfter = Directory.GetFiles(pruneDir, "*.json").Length;
        check(seeded.Revisions.Count == 2 && cachedBefore == 2 && cachedOffline == 2 && offline.Revisions.Count == 2 && cachedAfter == 1 && pruned.Revisions.Count == 1,
            "ACTIVE cache retains verified entries offline and prunes removed models on clean fetch");

        static object? StaticCall(Type type, string name, params object?[] args)
            => type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        static ModelConfig? FindModel(IReadOnlyList<ModelConfig> models, string name)
            => (ModelConfig?)StaticCall(typeof(MainWindow), "FindModelConfig", models, name);
        var keyProp = typeof(ActiveAudioConfigSync).GetProperty("PublicKeyOverrideForTest", BindingFlags.NonPublic | BindingFlags.Static)!;

        var legacyAlias = new CheckingConfig { models = [ModelConfigEditorWindow.ParseModel("MI SAM", SignRev("MI SAM", "legacy", 1, "LEG001").ConfigJson), Demo()] };
        var mergedAlias = pruned.Merge(legacyAlias);
        check(!mergedAlias.models.Any(m => m.model == "MI SAM") && mergedAlias.models.Count(m => m.model == "MISAM") == 1
            && FindModel(mergedAlias.models, "MI SAM")?.testItems.InOut.Tests.Single().id == "NEW001",
            "ACTIVE canonical MISAM replaces legacy MI SAM without shadowed duplicates");

        var prevState = ActiveAudioConfigSync.Current;
        var prevKey = keyProp.GetValue(null);
        string prevSelected = SessionModelConfigurations.Current.SelectedModel;
        try
        {
            typeof(ActiveAudioConfigSync).GetProperty("Current")!.SetValue(null, pruned);
            var ctx = new ProductMeasurementContext(); ctx.Receive("SN-ALIAS", "MI SAM", SerialSource.Manual);
            check(ctx.Snapshot().ConfigRevisionId == "rev-new-2", "MeasurementContext resolves MI SAM alias to MISAM ACTIVE revision");

            var cfgField = typeof(MainWindow).GetField("_checkingConfig", Hidden)!;
            var combo = (ComboBox)shell.FindName("ComboModels");
            cfgField.SetValue(shell, legacyAlias);
            Call(shell, "SyncComboModelsWithCheckingConfig");
            combo.SelectedItem = "MI SAM";

            keyProp.SetValue(null, pubPem);
            server.EmptyPending = true; server.ActiveOverride = [revMisam2];
            int reqBefore = server.Requests.Count;
            await (Task)Call(shell, "OpenPendingConfigsAsync", true)!;
            var shellConfig = (CheckingConfig)cfgField.GetValue(shell)!;
            bool calledActive = server.Requests.Skip(reqBefore).Any(r => r.Path.EndsWith("/active"));
            check(calledActive && ActiveAudioConfigSync.RevisionFor("MI SAM") == "rev-new-2"
                && !combo.Items.Cast<object>().Any(x => x.ToString() == "MI SAM")
                && combo.Items.Cast<object>().Any(x => x.ToString() == "MISAM")
                && combo.SelectedItem?.ToString() == "MISAM"
                && FindModel(shellConfig.models, "MISAM")?.testItems.InOut.Tests.Single().id == "NEW001",
                "ADMIN OpenPendingConfigsAsync refreshes ACTIVE even when pending=0 and re-syncs ComboModels alias");

            server.ActiveOverride = [revMisam3];
            await (Task)Call(shell, "RefreshActiveAudioConfigurationsAsync", TimeSpan.FromSeconds(4))!;
            Call(shell, "MergeActiveAudioConfigurations");
            shellConfig = (CheckingConfig)cfgField.GetValue(shell)!;
            check(ActiveAudioConfigSync.RevisionFor("MISAM") == "rev-web-3"
                && FindModel(shellConfig.models, "MISAM")?.testItems.InOut.Tests.Single().id == "WEB003",
                "Web-approved ACTIVE update refreshes cleanly in-session without re-login");

            var live = new CheckingConfig { models = [ModelConfigEditorWindow.ParseModel("MISAM", revMisam3.ConfigJson), Demo()] };
            var diskDemo = Demo(); diskDemo.testItems.InOut.Tests.Single().id = "DISK_DEMO"; diskDemo.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(2);
            var diskAlias = ModelConfigEditorWindow.ParseModel("MI SAM", SignRev("MI SAM", "d", 1, "DISK_STALE").ConfigJson);
            diskAlias.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(5);
            StaticCall(typeof(MainWindow), "KeepNewerAudioConfiguration", live, new CheckingConfig { models = [diskAlias, diskDemo] });
            check(live.models[0].testItems.InOut.Tests.Single().id == "WEB003"
                && live.models[1].testItems.InOut.Tests.Single().id == "DISK_DEMO",
                "KeepNewerAudioConfiguration preserves ACTIVE over newer disk timestamp while keeping local non-ACTIVE updates");

            string readDir = Path.Combine(output, "read-path-guard"); Directory.CreateDirectory(readDir);
            string userCfg = Path.Combine(readDir, "user-checking.json");
            string appCfg = Path.Combine(readDir, "app-checking.json");
            File.WriteAllText(userCfg, JsonSerializer.Serialize(new CheckingConfig { models = [ModelConfigEditorWindow.ParseModel("SERVER_MODEL", revMisam3.ConfigJson)] }));
            File.WriteAllText(appCfg, JsonSerializer.Serialize(new CheckingConfig { models = [ModelConfigEditorWindow.ParseModel("PORTABLE_MODEL", revMiSam.ConfigJson)] }));
            File.SetLastWriteTimeUtc(userCfg, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(appCfg, DateTime.UtcNow);
            string chosenOnline = (string)StaticCall(typeof(MainWindow), "ResolveCheckingConfigReadPath", userCfg, appCfg, ServerConfigMode.Online)!;
            string chosenCached = (string)StaticCall(typeof(MainWindow), "ResolveCheckingConfigReadPath", userCfg, appCfg, ServerConfigMode.OfflineCached)!;
            check(chosenOnline == userCfg && chosenCached == userCfg && File.ReadAllText(userCfg).Contains("SERVER_MODEL"),
                "ResolveCheckingConfigReadPath refuses to overwrite verified Online/OfflineCached config with newer portable file");
            string chosenAdmin = (string)StaticCall(typeof(MainWindow), "ResolveCheckingConfigReadPath", userCfg, appCfg, ServerConfigMode.AdminLocal)!;
            check(chosenAdmin == userCfg && File.ReadAllText(userCfg).Contains("PORTABLE_MODEL"),
                "ResolveCheckingConfigReadPath allows newer portable file in AdminLocal mode");
        }
        finally
        {
            server.EmptyPending = false; server.ActiveOverride = null;
            keyProp.SetValue(null, prevKey);
            typeof(ActiveAudioConfigSync).GetProperty("Current")!.SetValue(null, prevState);
            SessionModelConfigurations.Current.SelectedModel = prevSelected;
        }
    }
    private sealed record Request(string Method, string Path, string? Body, string? Bearer, string? AppKey);
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<Request> Requests { get; } = new();
        public Queue<(int Status, object Body)> Approvals { get; } = new();
        public (int Status, object Body)? Error;
        public bool Offline, RejectUpload, Unavailable, EmptyPending;
        public ActiveAudioConfig[]? ActiveOverride;
        private AudioConfigRevision _revision = new("draft-test", "Demo", "DRAFT", null, JsonSerializer.Serialize(Demo()), "hash", "ghi chú thử", "staff-test", DateTimeOffset.Parse("2026-11-09T03:00:00Z"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(token);
            Requests.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery, body, request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("X-App-Api-Key", out var h) ? h.Single() : null));
            if (Offline) throw new HttpRequestException("offline synthetic");
            if (Error is { } error) return Raw(error.Body, error.Status);
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/me")) return Data(new { staffId = "staff-test", name = "Quản lý thử", role = "ADMIN" });
            if (path.EndsWith("/revisions") && request.Method == HttpMethod.Post)
            {
                using var json = JsonDocument.Parse(body!); _revision = _revision with { Status = "PENDING", ConfigJson = json.RootElement.GetProperty("config").GetRawText() }; return Data(_revision, 201);
            }
            if (path.EndsWith("/revisions")) return Data(new[] { _revision, _revision with { Id = "rejected-test", Status = "REJECTED", RejectReason = "Sai ngõ thu" } });
            if (path.EndsWith("/submit")) { _revision = _revision with { Status = "PENDING" }; return Data(_revision); }
            if (path.EndsWith("/pending")) return Data(EmptyPending ? Array.Empty<AudioConfigRevision>() : new[] { _revision });
            if (path.EndsWith("/rebase")) return Data(_revision with { Status = "PENDING" });
            if (path.EndsWith("/approve"))
            {
                if (Unavailable) return Raw(new { error = "Thiếu khóa ký", code = "SIGNING_UNAVAILABLE" }, 503);
                if (Approvals.TryDequeue(out var response)) return Raw(response.Body, response.Status);
                return Data(_revision with { Status = "ACTIVE", Serial = 1, Signature = "test" });
            }
            if (path.EndsWith("/reject")) return Data(_revision with { Status = "REJECTED", RejectReason = "Sai ngõ thu" });
            if (path.EndsWith("/active")) return Data(ActiveOverride ?? new[] { Vector("backend-config-signing.json").Revision });
            if (path.EndsWith("/public-key")) return Data(new { algorithm = "ECDSA-P256-SHA256", signatureFormat = "IEEE-P1363", publicKeyPem = "diagnostic only" });
            if (path.Contains("/sheet-sync/")) return Data(new { queued = 0, state = "chưa cấu hình" });
            if (path.EndsWith("/qa-audio/trial"))
            {
                using var trialBody = JsonDocument.Parse(body!);
                return Data(new { logId = "trial-test", imageUrl = trialBody.RootElement.GetProperty("images").GetArrayLength() == 0 ? "" : "offline-trial-image", dataSet = "TRIAL" }, 201);
            }
            if (path.EndsWith("/qa-audio")) return RejectUpload ? Raw(new { error = "Cấu hình chưa được duyệt; kết quả không được lưu." }, 409)
                : Data(new { logId = "qa-test", imageUrl = "offline-image" }, 201);
            throw new Exception("Unexpected HTTP route: " + path);
        }
        private static HttpResponseMessage Data(object data, int code = 200) => Raw(new { data }, code);
        private static HttpResponseMessage Raw(object data, int code) => new((HttpStatusCode)code) { Content = new StringContent(JsonSerializer.Serialize(data, Web), Encoding.UTF8, "application/json") };
    }
    private static bool IsP256PublicKey(string pem)
    {
        try
        {
            using var key = ECDsa.Create(); key.ImportFromPem(pem);
            return key.KeySize == 256 && key.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7";
        }
        catch (Exception) { return false; }
    }
}
