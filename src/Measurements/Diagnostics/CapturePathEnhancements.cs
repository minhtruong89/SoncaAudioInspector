using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SoncaAudioInspector;

/// <summary>A read-only snapshot; an unavailable registry key is unknown, never evidence that effects are off.</summary>
public sealed record CapturePathEnhancementStatus(string EndpointId, string EndpointName, bool ReadSucceeded,
    int? DisableSysFx, string[] EffectNames, string? Error = null)
{
    public bool EnhancementsOff => ReadSucceeded && DisableSysFx == 1;
    public bool MayProcessShared => ReadSucceeded && !EnhancementsOff && EffectNames.Length > 0;
    public string Warning => MayProcessShared
        ? $"Mic đang bật Audio enhancements / Voice Clarity (APO: {string.Join(", ", EffectNames)}): các bài thu Shared (đáp tuyến, THD, headroom, Pink v1.1, oscilloscope) bị Windows xử lý — tắt: Settings → System → Sound → {EndpointName} → Audio enhancements: Off, rồi đo lại. Bài Rè thu Exclusive không bị ảnh hưởng."
        : "";
    public string Tooltip => (ReadSucceeded
        ? $"{EndpointName}: Disable_SysFx = {DisableSysFx?.ToString(CultureInfo.InvariantCulture) ?? "không có"}; APO: {(EffectNames.Length == 0 ? "chưa thấy" : string.Join(", ", EffectNames))}."
        : $"Chưa đọc được trạng thái Audio enhancements: {Error ?? "không có dữ liệu"}.")
        + " Windows có thể tạo endpoint mới khi đổi cổng/hub USB và bật lại enhancements. Rè thu Exclusive không bị ảnh hưởng bởi APO Shared; trạng thái registry không chứng minh toàn bộ đường thu sạch.";
}

public static class CapturePathEnhancements
{
    public const string DisableSysFxKey = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
    private static readonly string[] EffectPrefixes =
    ["{9e6136e0-57ab-4949-b57a-3627be142855},", "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},", "{d3993a3f-99c2-4402-b5ec-a92a0367664b},"];

    /// <summary>Pure parser, independent of Windows/registry/device access. The supplied dictionary is not mutated.</summary>
    public static CapturePathEnhancementStatus Parse(string? endpointId, string endpointName,
        IReadOnlyDictionary<string, object?> properties, bool readSucceeded = true, string? error = null)
    {
        int? disabled = null;
        var effects = new List<string>();
        foreach (var pair in properties)
        {
            if (pair.Key.Equals(DisableSysFxKey, StringComparison.OrdinalIgnoreCase)) disabled = Integer(pair.Value);
            if (!EffectPrefixes.Any(p => pair.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (string value in Strings(pair.Value).Where(s => !string.IsNullOrWhiteSpace(s) && s != "0"))
            {
                // Keep the useful driver/APO name rather than a device-instance path. GUID entries still count as effects.
                string name = value.Contains("VocaEffectPack", StringComparison.OrdinalIgnoreCase) ? "VocaEffectPack" : value.Trim();
                if (name.Length > 160) name = name[..160] + "…";
                effects.Add(name);
            }
        }
        return new(endpointId ?? "", endpointName, readSucceeded, disabled,
            effects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), error);
    }

    public static string? EndpointGuid(string? endpointId)
    {
        if (endpointId == null) return null;
        var match = Regex.Match(endpointId, @"\.\{(?<id>[0-9a-fA-F-]{36})\}$", RegexOptions.CultureInvariant);
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var guid) ? "{" + guid.ToString() + "}" : null;
    }

    /// <summary>Only opens the selected endpoint FxProperties with writable:false. Never enumerates devices or writes registry.</summary>
    public static CapturePathEnhancementStatus Read(string? endpointId, string endpointName, bool render = false)
    {
        string? guid = EndpointGuid(endpointId);
        if (guid == null) return Parse(endpointId, endpointName, new Dictionary<string, object?>(), false, "ID endpoint không hợp lệ");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\"
                + (render ? "Render" : "Capture") + @"\" + guid + @"\FxProperties", writable: false);
            if (key == null) return Parse(endpointId, endpointName, new Dictionary<string, object?>(), false, "không có khóa FxProperties");
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
                if (name.Equals(DisableSysFxKey, StringComparison.OrdinalIgnoreCase)
                    || EffectPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return Parse(endpointId, endpointName, values);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or PlatformNotSupportedException)
        { return Parse(endpointId, endpointName, new Dictionary<string, object?>(), false, ex.Message); }
    }

    private static int? Integer(object? value)
    {
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number) ? number
                : int.TryParse(element.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
        try { return value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }
    private static IEnumerable<string> Strings(object? value) => value switch
    {
        null => [], string text => [text], string[] list => list,
        JsonElement e when e.ValueKind == JsonValueKind.Array => e.EnumerateArray().Select(x => x.ToString()),
        byte[] bytes => bytes.Any(b => b != 0) ? ["APO (dữ liệu nhị phân)"] : [],
        _ => [Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""]
    };
}
