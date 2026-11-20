using System.Text.Json;
using System.Collections.Concurrent;

namespace SoncaAudioInspector;

/// <summary>Process-only overrides. Official models are never mutated or serialized with these entries.</summary>
public sealed class SessionModelConfigurations
{
    public static SessionModelConfigurations Current { get; } = new();
    private readonly ConcurrentDictionary<string, (ModelConfig Model, DateTimeOffset Saved)> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _serverTrials = new(StringComparer.OrdinalIgnoreCase);
    public string SelectedModel { get; set; } = "";
    public bool IsTemporary(string model) => _models.ContainsKey(model);
    public DateTimeOffset? SavedAt(string model) => _models.TryGetValue(model, out var entry) ? entry.Saved : null;
    public void Apply(ModelConfig model) { _serverTrials.TryRemove(model.model, out _); _models[model.model] = (Clone(model), DateTimeOffset.Now); }
    public void SetServerTrial(string model, string revision) { if (IsTemporary(model)) _serverTrials[model] = revision; }
    public string? ServerTrialRevision(string model) => _serverTrials.GetValueOrDefault(model);
    public string TrialBanner(string model) => _serverTrials.TryGetValue(model, out var revision)
        ? $"CẤU HÌNH THỬ · {model} · bản {revision} · kết quả lưu vào DỮ LIỆU THỬ"
        : $"CẤU HÌNH THỬ · {model} · dùng lúc {SavedAt(model):HH:mm} · kết quả lưu vào DỮ LIỆU THỬ";
    public bool Discard(string model) { _serverTrials.TryRemove(model, out _); return _models.TryRemove(model, out _); }
    public ModelConfig? Find(string model) => _models.TryGetValue(model, out var entry) ? Clone(entry.Model) : null;
    public ModelConfig Resolve(ModelConfig official) => Clone(_models.TryGetValue(official.model, out var entry) ? entry.Model : official);
    private static ModelConfig Clone(ModelConfig model)
    {
        var copy = JsonSerializer.Deserialize<ModelConfig>(JsonSerializer.Serialize(model))!;
        if (copy.testItems?.InOut is { } config) C360ModelDefinition.NormalizeCaptureAlias(config);
        return copy;
    }
}
