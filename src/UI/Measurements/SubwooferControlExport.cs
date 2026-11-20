using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

/// <summary>Saves a Ctrl+Shift+T session: every lượt as float WAV plus result.json. tools/StandardMeasurementCheck
/// --subwoofer-replay re-runs the analysis on such a folder.</summary>
public static class SubwooferControlExport
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string Save(SubwooferControlSession session, string dataDirectory)
    {
        if (!session.Measured.Any()) throw new InvalidOperationException("Chưa có bước nào đo hợp lệ để lưu.");
        string folder = Path.Combine(dataDirectory, "captures",
            "subwoofer-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        ResultPersistencePolicy.CreateDirectory(folder);
        var steps = new List<object>();
        foreach (var (step, response) in session.Measured)
        {
            var wavs = new List<string>();
            for (int i = 0; i < response.Captures.Length; i++)
            {
                string name = $"{step.Id}-{i + 1}.wav";
                PinkRubBuzzStimulus.WriteWave(Path.Combine(folder, name), response.Captures[i], response.SampleRate);
                wavs.Add(name);
            }
            steps.Add(new { step.Id, step.Title, step.Panel, LedLit = session.Led(step.Id), Response = response, Wavs = wavs });
        }
        ResultPersistencePolicy.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
        {
            SubwooferControlAnalysis.Version, CreatedUtc = DateTimeOffset.UtcNow, session.Settings, session.Limits,
            session.NoLedAnswered, session.Verdict, Steps = steps, session.Checks
        }, Json));
        return folder;
    }
}
