using SoncaAudioInspector.Update;
using System.Net.Http;
using System.Text.Json;

namespace SoncaAudioInspector;

public sealed record AudioConfigRevision(string Id, string SpeakerModel, string Status, long? Serial,
    string ConfigJson, string ConfigSha256, string? Note = null, string? CreatedById = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? SubmittedAt = null, string? DecidedById = null,
    DateTimeOffset? DecidedAt = null, string? RejectReason = null, string? Signature = null,
    string? BaseRevisionId = null, string? AppVersion = null, AudioConfigRevision? BaseRevision = null)
{
    public override string ToString() => $"{SpeakerModel} · {Status} · {Id} · {CreatedById} · {CreatedAt:g}"
        + (string.IsNullOrWhiteSpace(RejectReason) ? "" : " · " + RejectReason);
}
public sealed record ActiveAudioConfig(string SpeakerModel, string RevisionId, long Serial,
    string ConfigJson, string ConfigSha256, string Signature, ConfigApprovedBy? ApprovedBy = null, DateTimeOffset? ApprovedAt = null);
public sealed record ConfigApprovedBy(string StaffId, string? Name);
public sealed record AppMe(string StaffId, string Name, string Role);
public sealed record ConfigPublicKey(string Algorithm, string SignatureFormat, string PublicKeyPem);
public sealed record AudioQaConfiguration(string? ConfigRevisionId, string? SerialNumber, string QaVerdict = "NONE",
    bool IsTrial = false, string? SpeakerModel = null, string? ConfigSnapshot = null, string Phase = "AUTOTEST", string? TrialStatus = null,
    string? OperatorNote = null);

public static partial class ServerEngine
{
    // Test transport still goes through the normal bearer/app-key and retry path. Never configured by the UI.
    internal static HttpClient? AuthorizedClientOverride { get; set; }
    public static bool IsConfigAdmin => string.Equals(UserRole, "ADMIN", StringComparison.Ordinal);
    private static async Task<T> ConfigRequest<T>(HttpMethod method, string path, object? body = null)
    {
        var data = await SendAuthorizedForDataAsync(method, "api/app/" + path, body);
        return data.Deserialize<T>(JsonOptions) ?? throw new InvalidOperationException("Server trả dữ liệu cấu hình rỗng.");
    }
    public static async Task<AppMe> GetMeAsync()
    {
        var me = await ConfigRequest<AppMe>(HttpMethod.Get, "me");
        StaffID = me.StaffId; UserName = me.Name; UserRole = me.Role;
        return me;
    }
    public static Task<AudioConfigRevision> CreateAudioConfigRevisionAsync(ModelConfig model, string? note = null)
    {
        var body = new Dictionary<string, object> { ["speakerModel"] = model.model,
            ["config"] = JsonSerializer.SerializeToElement(model, ModelConfigEditorWindow.JsonOptions), ["appVersion"] = AppVersion.Numeric };
        if (!string.IsNullOrWhiteSpace(note)) body["note"] = note.Trim();
        return ConfigRequest<AudioConfigRevision>(HttpMethod.Post, "audio-config/revisions", body);
    }
    public static Task<AudioConfigRevision[]> ListAudioConfigRevisionsAsync(string? model = null, string? status = null)
    {
        var query = new List<string>();
        if (model != null) query.Add("speakerModel=" + Uri.EscapeDataString(model));
        if (status != null) query.Add("status=" + Uri.EscapeDataString(status));
        return ConfigRequest<AudioConfigRevision[]>(HttpMethod.Get, "audio-config/revisions" + (query.Count == 0 ? "" : "?" + string.Join("&", query)));
    }
    private static string RevisionRoute(string id, string action) => "audio-config/revisions/" + Uri.EscapeDataString(id) + "/" + action;
    public static Task<AudioConfigRevision> SubmitAudioConfigRevisionAsync(string id)
        => ConfigRequest<AudioConfigRevision>(HttpMethod.Post, RevisionRoute(id, "submit"));
    public static Task<AudioConfigRevision[]> GetPendingAudioConfigRevisionsAsync()
        => ConfigRequest<AudioConfigRevision[]>(HttpMethod.Get, "audio-config/pending");
    public static Task<AudioConfigRevision> ApproveAudioConfigRevisionAsync(string id)
        => ConfigRequest<AudioConfigRevision>(HttpMethod.Post, RevisionRoute(id, "approve"));
    public static Task<AudioConfigRevision> RebaseAudioConfigRevisionAsync(string id, string? expectedActiveRevisionId)
        => ConfigRequest<AudioConfigRevision>(HttpMethod.Post, RevisionRoute(id, "rebase"), new { expectedActiveRevisionId });
    public static Task<AudioConfigRevision> RejectAudioConfigRevisionAsync(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 4000)
            throw new ArgumentException("Nhập lý do từ chối (1–4000 ký tự).");
        return ConfigRequest<AudioConfigRevision>(HttpMethod.Post, RevisionRoute(id, "reject"), new { reason = reason.Trim() });
    }
    public static Task<ActiveAudioConfig[]> GetActiveAudioConfigsAsync()
        => ConfigRequest<ActiveAudioConfig[]>(HttpMethod.Get, "audio-config/active");
    public static Task<ConfigPublicKey> GetAudioConfigPublicKeyAsync()
        => ConfigRequest<ConfigPublicKey>(HttpMethod.Get, "audio-config/public-key");
}
