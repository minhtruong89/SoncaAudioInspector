namespace SoncaAudioInspector;

public static class SymphonyModelDefinition
{
    public const string Name = "SYMPHONY";
    public const ushort FirmwareModelId = 1062;

    public static ModelConfig Create(string playback, string microphone, double levelDbFs = -24)
    {
        if (!SoncaFirmwareModels.Current.Find(FirmwareModelId).Any(name => name.Equals(Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("File model firmware chưa xác nhận SYMPHONY / 1062.");
        var model = C360ModelDefinition.Create(Name);
        var io = model.testItems.InOut;
        io.AutomationProfile = Acoustic3ModelDefinition.Profile;
        io.AutomationProfileVersion = 1;
        io.FirmwareModelId = FirmwareModelId;
        io.BleSelectStrongestByModel = true;
        io.AcousticCapture = true;
        io.TestVolume = new(); // Preserve the operator's current speaker/Windows levels.
        io.BluetoothAutoRemove = false;
        io.Description = "Dò BLE theo file model, xác nhận INFO SYMPHONY 1062, chọn RSSI mạnh nhất; xác minh LINE IN và BT trước khi đo bằng mic ngoài.";
        io.Devices = new() { Input = new() { ["LINE IN"] = playback, ["BT"] = Name }, Output = new() { ["Measurement Mic"] = microphone } };
        io.Tests = io.Tests.Where(test => test.SpeakerStage is "LineIn" or "Bluetooth").ToList();
        foreach (var test in io.Tests)
        {
            test.name = test.SpeakerStage == "LineIn" ? "LINE IN · Đáp tuyến / THD" : "BT · Đáp tuyến / THD";
            test.RecordingIn = "Measurement Mic";
            test.PlaybackLevelDbfs = Math.Clamp(levelDbFs, -60, -12);
            test.MaximumRuns = 1; test.RequiredPasses = 1;
            test.ReferenceTestId = null;
        }
        for (int index = 0; index < io.Tests.Count; index++) io.Tests[index].id = $"IO{index + 1:000}";
        return model;
    }
}
