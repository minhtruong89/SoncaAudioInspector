using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

/// <summary>Shared acceptance setup for every route in a model. App auto-sets speaker BLE levels before measurement and restores original levels afterwards.</summary>
public sealed class ModelTestVolume
{
    public static ModelTestVolume C360Defaults() => new() {BleMasterPercent=100,BleMusicPercent=50,BleMicPercent=50,WindowsPlaybackPercent=75,WindowsRecordingPercent=100};
    public double? BleMasterPercent { get; set; }
    public double? BleMusicPercent { get; set; }
    public double? BleMicPercent { get; set; }
    public double? WindowsPlaybackPercent { get; set; }
    public double? WindowsRecordingPercent { get; set; }
    [JsonIgnore] public bool RequiresBle => BleMasterPercent.HasValue || BleMusicPercent.HasValue || BleMicPercent.HasValue;
    [JsonIgnore] public bool IsEmpty => new[]{BleMasterPercent,BleMusicPercent,BleMicPercent,WindowsPlaybackPercent,WindowsRecordingPercent}.All(v=>!v.HasValue);
    /// <summary>v1.5.3: a test's own TestVolume overrides the model level field by field; empty fields follow the model.</summary>
    public static ModelTestVolume? Merge(ModelTestVolume? model, ModelTestVolume? test)
    {
        if (test == null || test.IsEmpty) return model;
        return new() { BleMasterPercent = test.BleMasterPercent ?? model?.BleMasterPercent, BleMusicPercent = test.BleMusicPercent ?? model?.BleMusicPercent,
            BleMicPercent = test.BleMicPercent ?? model?.BleMicPercent, WindowsPlaybackPercent = test.WindowsPlaybackPercent ?? model?.WindowsPlaybackPercent,
            WindowsRecordingPercent = test.WindowsRecordingPercent ?? model?.WindowsRecordingPercent };
    }
    /// <summary>Volume mapping shown to the operator for every test (BLE speaker levels and Windows endpoint levels).</summary>
    public static string Describe(ModelTestVolume? volume, double? legacyPlayback = null, double? legacyRecording = null)
    {
        static string P(double? v) => v is double value ? $"{value:0.##}%" : "giữ";
        return $"BLE MASTER {P(volume?.BleMasterPercent)} · MUSIC {P(volume?.BleMusicPercent)} · MIC {P(volume?.BleMicPercent)} · "
            + $"Windows phát {P(volume?.WindowsPlaybackPercent ?? legacyPlayback)} · thu {P(volume?.WindowsRecordingPercent ?? legacyRecording)}";
    }
    public IEnumerable<(string Name, ushort Register, double Required)> BleTargets(bool includeMic = true)
    {
        if (BleMasterPercent is double master) yield return ("MASTER DSP", SoncaBleMixerProfile.MasterVolume, master);
        if (BleMusicPercent is double music) yield return ("MUSIC", SoncaBleMixerProfile.MusicVolume, music);
        if (includeMic && BleMicPercent is double mic) yield return ("MIC", SoncaBleMixerProfile.MicVolume, mic);
    }
    public void Validate()
    {
        if (new[] { BleMasterPercent, BleMusicPercent, BleMicPercent, WindowsPlaybackPercent, WindowsRecordingPercent }
            .Any(v => v.HasValue && (!double.IsFinite(v.Value) || v < 1 || v > 100)))
            throw new ArgumentException("Âm lượng cố định của model phải từ 1 đến 100%; để trống để giữ cấu hình hiện tại.");
    }
    public string[] Mismatches(IReadOnlyDictionary<ushort, ushort> state, bool includeMic = true)
    {
        Validate(); var errors = new List<string>();
        foreach (var (name, register, required) in BleTargets(includeMic))
        {
            if (!state.TryGetValue(register, out ushort raw) || raw > 8192)
                errors.Add($"{name}: chưa đọc được mức thực tế; yêu cầu {required:0.##}%");
            // App auto-sets BLE levels before measurement and restores original levels when done;
            // C360 physical keys step by > 0.5% and do not land on 50.00%.
            else if (Math.Abs(SoncaBleMixerProfile.VolumeFromRaw(raw) - required) > .55)
                errors.Add($"{name}: đang {SoncaBleMixerProfile.VolumeFromRaw(raw):0.##}% → cần {required:0.##}%");
        }
        return errors.ToArray();
    }
    public void RequireMatch(IReadOnlyDictionary<ushort, ushort> state, bool includeMic = true)
    {
        var errors = Mismatches(state, includeMic);
        if (errors.Length > 0) throw new InvalidOperationException("Chỉnh âm lượng loa đúng cấu hình model trước khi đo:\n" + string.Join("\n", errors));
    }
}

public partial class TestConfig
{
    [JsonIgnore] public ModelTestVolume? ModelVolume { get; set; }
    /// <summary>Optional per-test levels (web /config "Âm lượng riêng bài này"); null or empty = the model TestVolume.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ModelTestVolume? TestVolume { get; set; }
    [JsonIgnore] public string VolumeMapping => ModelTestVolume.Describe(ModelVolume, WindowsPlaybackPercent, WindowsRecordingPercent);
}
