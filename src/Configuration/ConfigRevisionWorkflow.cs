using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoncaAudioInspector;

public sealed class ConfigTrialEditorSession(Action<ModelConfig> applyTemporary)
{
    public AudioConfigRevision? Selected { get; private set; }
    public async Task<AudioConfigRevision> SaveAsync(ModelConfig candidate, string? note)
    {
        var revision = await ServerEngine.CreateAudioConfigRevisionAsync(candidate, note);
        Apply(revision); return revision;
    }
    public void Select(AudioConfigRevision? revision) => Selected = revision;
    public void Apply(AudioConfigRevision revision)
    {
        if (revision.Status is not ("DRAFT" or "PENDING" or "REJECTED")) throw new InvalidOperationException("Chọn bản DRAFT/PENDING/REJECTED để thử.");
        var model = ModelConfigEditorWindow.ParseModel(revision.SpeakerModel, revision.ConfigJson);
        applyTemporary(model);
        SessionModelConfigurations.Current.SetServerTrial(model.model, revision.Id);
        Selected = revision;
    }
    public async Task<AudioConfigRevision> SubmitAsync()
    {
        var revision = Selected ?? throw new InvalidOperationException("Lưu thử lên server hoặc chọn một bản DRAFT trước.");
        if (revision.Status != "DRAFT") throw new InvalidOperationException("Chỉ gửi duyệt bản DRAFT.");
        Selected = await ServerEngine.SubmitAudioConfigRevisionAsync(revision.Id); return Selected;
    }
}

public static class ConfigRevisionDiff
{
    public static string Describe(string? baseline, string proposed)
    {
        var before = Flatten(baseline); var after = Flatten(proposed); var lines = new List<string>();
        foreach (string path in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(path, out var a); after.TryGetValue(path, out var b);
            if (a != b) lines.Add(path + ": " + (a ?? "(chưa có)") + " → " + (b ?? "(bỏ)"));
        }
        return lines.Count == 0 ? "Không có khác biệt nội dung." : string.Join("\n", lines);
    }
    private static Dictionary<string, string> Flatten(string? json)
    {
        var values = new Dictionary<string, string>();
        if (json != null) Walk(JsonNode.Parse(json), "", values); return values;
    }
    private static void Walk(JsonNode? node, string path, Dictionary<string, string> values)
    {
        if (node is JsonObject obj)
            foreach (var p in obj) Walk(p.Value, path.Length == 0 ? p.Key : path + " · " + p.Key, values);
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
            {
                var item = array[i];
                string label = item is JsonObject o && o["id"] != null ? o["id"]!.GetValue<string>() : $"[{i}]";
                Walk(item, label.StartsWith("IO", StringComparison.Ordinal) || item is JsonObject && item["id"] != null ? label : path + " " + label, values);
            }
        else values[path] = node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "null";
    }
}

/// <summary>One human click per attempt. A conflict only updates the diff; the next explicit click rebases and approves.</summary>
public sealed class ConfigApprovalReview(AudioConfigRevision revision)
{
    public AudioConfigRevision Revision { get; private set; } = revision;
    public AudioConfigRevision? Baseline { get; private set; } = revision.BaseRevision;
    public bool RequiresReconfirmation { get; private set; }
    public string Diff => ConfigRevisionDiff.Describe(Baseline?.ConfigJson, Revision.ConfigJson);
    public async Task<bool> ApproveAsync()
    {
        try
        {
            if (RequiresReconfirmation)
                Revision = await ServerEngine.RebaseAudioConfigRevisionAsync(Revision.Id, Baseline?.Id);
            Revision = await ServerEngine.ApproveAudioConfigRevisionAsync(Revision.Id);
            RequiresReconfirmation = false; return true;
        }
        catch (ServerEngine.ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict && ex.Code == "STALE_BASE")
        {
            if (ex.Details is not JsonElement details || !details.TryGetProperty("currentActiveRevision", out var current)) throw;
            Baseline = current.ValueKind == JsonValueKind.Null ? null : current.Deserialize<AudioConfigRevision>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            RequiresReconfirmation = true; return false;
        }
    }
    public async Task RejectAsync(string reason) => Revision = await ServerEngine.RejectAudioConfigRevisionAsync(Revision.Id, reason);
}
