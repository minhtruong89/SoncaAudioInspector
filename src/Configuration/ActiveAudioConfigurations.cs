using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector.Update;

namespace SoncaAudioInspector;

public static class AudioConfigSignature
{
    public static string PinnedPublicKey
    {
        get
        {
            using var stream = typeof(AudioConfigSignature).Assembly.GetManifestResourceStream("SoncaAudioInspector.ConfigSigningPublicKey")!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
    }
    // Node JSON.stringify string encoding: retain Unicode, HTML and slash verbatim.
    public static string Quote(string value)
    {
        var b = new StringBuilder("\"");
        foreach (char c in value)
            b.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n",
                '\r' => "\\r", '\t' => "\\t", < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture), _ => c.ToString() });
        return b.Append('"').ToString();
    }
    public static byte[] Payload(ActiveAudioConfig r) => Encoding.UTF8.GetBytes("{\"v\":1,\"speakerModel\":" + Quote(r.SpeakerModel)
        + ",\"revisionId\":" + Quote(r.RevisionId) + ",\"serial\":" + r.Serial.ToString(CultureInfo.InvariantCulture)
        + ",\"configSha256\":" + Quote(r.ConfigSha256) + "}");
    public static ModelConfig Verify(ActiveAudioConfig r, string key, ActiveAudioConfig? previous = null)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.ConfigJson))) != r.ConfigSha256)
            throw new InvalidDataException("SHA-256 cấu hình không khớp.");
        byte[] payload = Payload(r);
        byte[] signature = Convert.FromBase64String(r.Signature);
        using var verifier = ECDsa.Create(); verifier.ImportFromPem(key);
        if (verifier.KeySize != 256 || verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7"
            || signature.Length != 64 || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Chữ ký cấu hình P-256 không hợp lệ.");
        if (r.Serial < 1 || r.Serial > 9007199254740991L || string.IsNullOrWhiteSpace(r.RevisionId) || string.IsNullOrWhiteSpace(r.SpeakerModel))
            throw new InvalidDataException("Định danh/serial cấu hình không hợp lệ.");
        if (previous != null && (r.Serial < previous.Serial || r.Serial == previous.Serial
            && (r.RevisionId != previous.RevisionId || r.ConfigSha256 != previous.ConfigSha256)))
            throw new InvalidDataException("Từ chối rollback cấu hình theo model.");
        using var doc = JsonDocument.Parse(r.ConfigJson);
        if (doc.RootElement.GetProperty("model").GetString() != r.SpeakerModel)
            throw new InvalidDataException("Model trong cấu hình ký không khớp.");
        return ModelConfigEditorWindow.ParseModel(r.SpeakerModel, r.ConfigJson);
    }
}

/// <summary>Cache contains the original signed bytes/fields, always verified again before use.</summary>
public sealed class ActiveAudioConfigStore(string directory, string publicKey)
{
    private string FileFor(string model) => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(model))) + ".json");
    public Dictionary<string, ActiveAudioConfig> Load(ICollection<string> warnings)
    {
        var result = new Dictionary<string, ActiveAudioConfig>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        try
        {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            try
            {
                var revision = JsonSerializer.Deserialize<ActiveAudioConfig>(File.ReadAllText(path))!;
                AudioConfigSignature.Verify(revision, publicKey);
                if (Path.GetFullPath(path) != Path.GetFullPath(FileFor(revision.SpeakerModel))) throw new InvalidDataException("Sai tên cache model.");
                result.Add(revision.SpeakerModel, revision);
            }
            catch (Exception ex) { warnings.Add("Cache ACTIVE bị bỏ: " + ex.Message); }
        }
        catch (Exception ex) { warnings.Add("Không đọc được cache ACTIVE: " + ex.Message); }
        return result;
    }
    public void Save(ActiveAudioConfig revision, ActiveAudioConfig? previous)
    {
        AudioConfigSignature.Verify(revision, publicKey, previous);
        Directory.CreateDirectory(directory);
        AtomicFile.WriteAllText(FileFor(revision.SpeakerModel), JsonSerializer.Serialize(revision), keepBackup: false);
    }
    public void RetainOnly(IReadOnlyCollection<string> activeModels)
    {
        if (!Directory.Exists(directory)) return;
        var keep = activeModels.Select(m => Path.GetFullPath(FileFor(m))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            if (!keep.Contains(Path.GetFullPath(path))) File.Delete(path);
    }
}

public sealed record ActiveAudioConfigState(IReadOnlyDictionary<string, ActiveAudioConfig> Revisions, string Warning)
{
    public static int FindModelIndex(IReadOnlyList<ModelConfig> models, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return -1;
        for (int i = 0; i < models.Count; i++)
            if (string.Equals(models[i].model, modelName, StringComparison.Ordinal)) return i;
        for (int i = 0; i < models.Count; i++)
            if (string.Equals(models[i].model, modelName, StringComparison.OrdinalIgnoreCase)) return i;
        string norm = BomCsvParser.NormalizeModelKey(modelName);
        if (norm.Length == 0) return -1;
        for (int i = 0; i < models.Count; i++)
            if (string.Equals(BomCsvParser.NormalizeModelKey(models[i].model), norm, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public ActiveAudioConfig? FindRevision(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return null;
        if (Revisions.TryGetValue(modelName, out var exact)) return exact;
        foreach (var kv in Revisions)
            if (string.Equals(kv.Key, modelName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        string norm = BomCsvParser.NormalizeModelKey(modelName);
        if (norm.Length == 0) return null;
        foreach (var kv in Revisions)
            if (string.Equals(BomCsvParser.NormalizeModelKey(kv.Key), norm, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public CheckingConfig Merge(CheckingConfig legacy)
    {
        var merged = JsonSerializer.Deserialize<CheckingConfig>(JsonSerializer.Serialize(legacy))!;
        foreach (var r in Revisions.Values.OrderBy(r => r.Serial).ThenBy(r => r.SpeakerModel, StringComparer.Ordinal))
        {
            // Revisions in a state can only be supplied by the verified synchronization service.
            var model = ModelConfigEditorWindow.ParseModel(r.SpeakerModel, r.ConfigJson);
            int index = FindModelIndex(merged.models, r.SpeakerModel);
            if (index < 0) merged.models.Add(model);
            else
            {
                merged.models[index] = model;
                string norm = BomCsvParser.NormalizeModelKey(r.SpeakerModel);
                for (int j = merged.models.Count - 1; j >= 0; j--)
                    if (j != index && (string.Equals(merged.models[j].model, r.SpeakerModel, StringComparison.OrdinalIgnoreCase)
                        || (norm.Length > 0 && string.Equals(BomCsvParser.NormalizeModelKey(merged.models[j].model), norm, StringComparison.OrdinalIgnoreCase))))
                        merged.models.RemoveAt(j);
            }
        }
        return merged;
    }
}

public static class ActiveAudioConfigSync
{
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    internal static string? PublicKeyOverrideForTest { get; set; }
    public static ActiveAudioConfigState Current { get; private set; } = new(new Dictionary<string, ActiveAudioConfig>(), "");
    public static string? RevisionFor(string model) => Current.FindRevision(model)?.RevisionId;
    public static Task<ActiveAudioConfigState> RefreshDefaultAsync() => RefreshAndSetAsync();
    private static async Task<ActiveAudioConfigState> RefreshAndSetAsync()
    {
        await RefreshGate.WaitAsync();
        try
        {
            string key = PublicKeyOverrideForTest ?? AudioConfigSignature.PinnedPublicKey;
            Current = await RefreshAsync(ServerEngine.GetActiveAudioConfigsAsync,
                new ActiveAudioConfigStore(Path.Combine(ServerConfigSync.UserDataDirectory, "active-audio-config"), key),
                key);
            return Current;
        }
        finally { RefreshGate.Release(); }
    }
    public static async Task<ActiveAudioConfigState> RefreshAsync(Func<Task<ActiveAudioConfig[]>> fetch, ActiveAudioConfigStore store, string key)
    {
        var warnings = new List<string>(); var revisions = store.Load(warnings);
        try
        {
            var fetched = await fetch().ConfigureAwait(false);
            var duplicates = fetched.GroupBy(r => r.SpeakerModel).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var fetchedModels = fetched.Select(r => r.SpeakerModel).ToHashSet(StringComparer.Ordinal);
            foreach (var staleKey in revisions.Keys.Where(k => !fetchedModels.Contains(k)).ToArray())
                revisions.Remove(staleKey);
            foreach (var revision in fetched)
                try
                {
                    if (duplicates.Contains(revision.SpeakerModel)) throw new InvalidDataException("Trùng ACTIVE cho model.");
                    revisions.TryGetValue(revision.SpeakerModel, out var previous);
                    AudioConfigSignature.Verify(revision, key, previous);
                    store.Save(revision, previous); revisions[revision.SpeakerModel] = revision;
                }
                catch (Exception ex) { warnings.Add($"{revision.SpeakerModel}: bỏ ACTIVE không hợp lệ ({ex.Message}); dùng cache đã xác minh hoặc feed v1.3."); }
            try { store.RetainOnly(revisions.Keys.ToArray()); }
            catch (Exception ex) { warnings.Add("Không dọn được cache ACTIVE cũ: " + ex.Message); }
        }
        catch (Exception ex) { warnings.Add("ACTIVE OFFLINE: " + ex.Message + "; dùng cache đã xác minh hoặc feed v1.3."); }
        return new(revisions, string.Join("\n", warnings));
    }
}
