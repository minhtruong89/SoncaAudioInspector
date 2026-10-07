using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace SoncaAudioInspector;

internal static class BluetoothDeviceRemoval
{
    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static bool MatchesModel(string deviceName, string modelName)
    {
        static string ModelKey(string name)
        {
            string endpointName = Regex.Match(name, @"\(([^)]+)\)\s*$").Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(endpointName)) name = endpointName;
            name = Regex.Replace(name.Trim(), @"^\d+\s*-\s*", "");
            string key = Normalize(name);
            return key is "MISAM" or "MI30SAM" ? "MI30SAM" : key;
        }

        string modelKey = ModelKey(modelName);
        return modelKey.Length >= 4 && ModelKey(deviceName) == modelKey;
    }

    internal static async Task<(bool Success, int Removed, int Matched, string Message)> RemovePairedDevicesForModelAsync(string modelName)
    {
        // Query pairing records so disconnected devices are included. Removal is
        // called only by the manual button, never by measurement completion.
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            Array.Empty<string>(),
            DeviceInformationKind.AssociationEndpoint);
        var byId = paired.ToDictionary(device => device.Id, StringComparer.Ordinal);
        return await RemoveMatchingDevicesAsync(modelName,
            paired.Select(device => new KeyValuePair<string, string>(device.Id, device.Name)),
            async id =>
            {
                var result = await byId[id].Pairing.UnpairAsync();
                if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                    throw new InvalidOperationException(result.Status.ToString());
            });
    }

    internal static async Task<(bool Success, int Removed, int Matched, string Message)> RemoveMatchingDevicesAsync(
        string modelName, IEnumerable<KeyValuePair<string, string>> pairedDevices, Func<string, Task> unpair)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, 0, "Chọn model trước khi xóa thiết bị Bluetooth đã ghép.");

        var targets = pairedDevices.Where(device => MatchesModel(device.Value, modelName))
            .DistinctBy(device => device.Key).ToArray();
        if (targets.Length == 0)
            return (true, 0, 0, $"Không còn thiết bị Bluetooth đã ghép thuộc model '{modelName}'.");

        int removed = 0;
        var failures = new List<string>();
        foreach (var device in targets)
        {
            try
            {
                await unpair(device.Key);
                removed++;
            }
            catch (Exception ex) { failures.Add($"{device.Value}: {ex.Message}"); }
        }

        return (failures.Count == 0, removed, targets.Length,
            $"Đã xóa {removed}/{targets.Length} thiết bị Bluetooth đã ghép thuộc model '{modelName}'."
            + (failures.Count == 0 ? "" : " Chưa xóa được: " + string.Join("; ", failures)));
    }
}
