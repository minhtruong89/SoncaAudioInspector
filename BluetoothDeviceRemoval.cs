using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

internal static class BluetoothDeviceRemoval
{
    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static bool IsMi30SamName(string endpointName)
    {
        // Windows may prefix an endpoint with its instance number. Do not use
        // substring matching: MI30 SAMPLE and other speaker models are unrelated.
        string name = Regex.Match(endpointName, @"\(([^)]+)\)\s*$").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(name)) name = endpointName;
        name = Regex.Replace(name.Trim(), @"^\d+\s*-\s*", "");
        return Normalize(name) == "MI30SAM";
    }

    internal static async Task<(bool Success, string Message)> RemovePassedMi30SamDevicesAsync()
    {
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint);
        var targets = paired.Where(device => IsMi30SamName(device.Name)).ToArray();
        if (targets.Length == 0)
            return (true, "Không còn ghép đôi MI30 SAM trong Windows.");
        int removed = 0;
        var failures = new List<string>();
        foreach (var device in targets)
        {
            try
            {
                var result = await device.Pairing.UnpairAsync();
                if (result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired)
                    removed++;
                else failures.Add($"{device.Name}: {result.Status}");
            }
            catch (Exception ex) { failures.Add($"{device.Name}: {ex.Message}"); }
        }
        return (failures.Count == 0, $"Đã gỡ {removed}/{targets.Length} ghép đôi MI30 SAM."
            + (failures.Count == 0 ? "" : " Chưa gỡ được: " + string.Join("; ", failures)));
    }

    internal static async Task<(bool Success, string Message)> RemovePairedAudioDeviceAsync(string endpointName)
    {
        // Audio endpoint IDs are not Bluetooth pairing IDs. Resolve the paired
        // physical device by its distinctive name and refuse ambiguous matches.
        string deviceName = Regex.Match(endpointName, @"\((?:\d+-)?([^)]{4,})\)").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = endpointName;
        string target = Normalize(deviceName);
        if (target.Length < 4)
            return (false, "Không xác định được tên thiết bị Bluetooth từ ngõ phát đã chọn.");

        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Array.Empty<string>(),
            DeviceInformationKind.AssociationEndpoint);
        var matches = paired.Where(device =>
        {
            string name = Normalize(device.Name);
            return name.Length >= 4 && (target == name || target.Contains(name, StringComparison.Ordinal));
        }).ToList();
        if (matches.Count != 1)
            return (false, matches.Count == 0
                ? $"Không tìm thấy thiết bị Bluetooth đã ghép tương ứng với '{endpointName}'."
                : "Có nhiều thiết bị Bluetooth trùng tên. Hãy xóa thiết bị trong Windows Bluetooth & devices.");

        var result = await matches[0].Pairing.UnpairAsync();
        return result.Status == DeviceUnpairingResultStatus.Unpaired
            ? (true, $"Đã xóa ghép đôi {matches[0].Name} khỏi Windows.")
            : (false, $"Windows không xóa được {matches[0].Name}: {result.Status}.");
    }
}
