using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector.Update;

/// <summary>Signed pointer to the mandatory station configuration (checking_config.json) on the update server.
/// Unlike app updates (optional, user may defer), every app start must apply exactly this file.
/// Shared with tools/UpdateFeedTool, so it depends on nothing beyond System.Text.Json.</summary>
public sealed record ServerConfigManifest(int Schema, string App, string Kind, long Serial, DateTimeOffset Published,
    string Sha256, long Size, string? Notes = null)
{
    public const string ExpectedApp = "SoncaAudioInspector";
    public const string ExpectedKind = "checking_config";
    public const long MaxSize = 8 * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static ServerConfigManifest Read(byte[] bytes)
        => JsonSerializer.Deserialize<ServerConfigManifest>(bytes, JsonOptions) ?? throw new InvalidDataException("Manifest config server rỗng.");
    public ServerConfigManifest Validate()
    {
        if (Schema != 1 || App != ExpectedApp || Kind != ExpectedKind || Serial <= 0 || Size <= 0 || Size > MaxSize
            || Sha256 is not { Length: 64 } || !Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Manifest config server không hợp lệ (schema/app/kind/serial/hash).");
        return this;
    }
    public string Describe() => $"#{Serial} · {Published.ToLocalTime():dd/MM/yyyy HH:mm} · {Sha256[..8]}";
    /// <summary>Structural check usable without the WPF app types (publisher side).</summary>
    public static void ValidateContentShape(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array || models.GetArrayLength() == 0)
            throw new InvalidDataException("Config không có danh sách model.");
    }
}
