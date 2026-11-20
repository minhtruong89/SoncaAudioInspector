using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class SymphonyConfigSyncCheck
{
    public static async Task Run(string output, string expectedModelFile)
    {
        Directory.CreateDirectory(output);
        var state = await ServerConfigSync.RunDefaultAsync();
        if (state.Mode != ServerConfigMode.Online)
            throw new InvalidOperationException("Chưa đồng bộ online cấu hình đã ký: " + state.Message);
        var expected = ModelConfigEditorWindow.ParseModel("SYMPHONY", File.ReadAllText(expectedModelFile));
        var config = AtomicFile.ReadJson<CheckingConfig>(ServerConfigSync.UserCheckingConfigPath)!;
        var model = config.models.Single(m => m.model == "SYMPHONY");
        if (!JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(model, ModelConfigEditorWindow.JsonOptions)),
            JsonNode.Parse(JsonSerializer.Serialize(expected, ModelConfigEditorWindow.JsonOptions))))
            throw new InvalidOperationException("SYMPHONY tại trạm không khớp file đã yêu cầu.");
        Exception? failure = null;
        string[]? names = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                var app = new App(); app.InitializeComponent();
                // WPF test host: no startup devices, network, BLE or timers.
                window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new object[] { false }, null)!;
                typeof(MainWindow).GetMethod("LoadCheckingConfig", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                names = ((ComboBox)window.FindName("ComboModels")).Items.Cast<object>().Select(item => item.ToString()!).ToArray();
                if (names.Count(name => name == "SYMPHONY") != 1)
                    throw new InvalidOperationException("Danh sách model WPF chưa có đúng một SYMPHONY.");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new InvalidOperationException("Không nạp được SYMPHONY trong WPF test host.", failure);
        File.WriteAllText(Path.Combine(output, "config-sync.json"), JsonSerializer.Serialize(new {
            status = state.Mode.ToString(), state.Manifest, state.Message, signedStationConfigMatches = true,
            userConfig = ServerConfigSync.UserCheckingConfigPath, offscreenModelList = names,
            capture = model.testItems.InOut.Devices.Output.Values.Single(),
            bleCheck = model.testItems.InOut.IsBleCheckAtStartEnabled,
            tests = model.testItems.InOut.Tests.Select(test => new { test.id, test.name, test.SpeakerStage, test.IsEnabled }),
            existingVisibleWindowVerified = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: signed config #" + state.Manifest!.Serial + "; SYMPHONY có trong danh sách model WPF; mic USB và hai bài LINE IN/BT khớp file.");
    }
}
