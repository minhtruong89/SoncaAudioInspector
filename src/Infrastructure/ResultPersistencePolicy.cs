using System.IO;
using System.Text;
using NAudio.Wave;

namespace SoncaAudioInspector;

/// <summary>Single decision point for all measurement persistence. A scope pins a running acquisition's policy across awaits.</summary>
public static class ResultPersistencePolicy
{
    public const string TrialLabel = "CẤU HÌNH THỬ · KẾT QUẢ LƯU VÀO DỮ LIỆU THỬ";
    private static readonly AsyncLocal<bool?> MeasurementAllowed = new();
    private static readonly AsyncLocal<bool?> MeasurementTrial = new();
    public static bool CanPersist => MeasurementAllowed.Value ?? true;
    public static bool IsTrial => MeasurementTrial.Value ?? SessionModelConfigurations.Current.IsTemporary(SessionModelConfigurations.Current.SelectedModel);
    public static IDisposable BeginTrial(bool trial)
    {
        var previous = MeasurementTrial.Value;
        MeasurementTrial.Value = trial;
        return new Scope(() => MeasurementTrial.Value = previous);
    }
    public static IDisposable BeginMeasurement()
        => BeginResult(CanPersist);
    public static IDisposable BeginResult(bool resultAllowed)
    {
        var previous = MeasurementAllowed.Value;
        var trial = BeginTrial(IsTrial);
        MeasurementAllowed.Value = CanPersist && resultAllowed;
        return new Scope(() => { MeasurementAllowed.Value = previous; trial.Dispose(); });
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    public static void Execute(Action write) { if (CanPersist) write(); }
    public static T Execute<T>(Func<T> write, T blocked) => CanPersist ? write() : blocked;
    public static Task<bool> SubmitAsync(Func<Task<bool>> submit) => CanPersist ? submit() : Task.FromResult(false);
    public static void CreateDirectory(string path) => Execute(() => { Directory.CreateDirectory(path); });
    public static void WriteAllText(string path, string text) => Execute(() => File.WriteAllText(path, text));
    public static void WriteAllText(string path, string text, Encoding encoding) => Execute(() => File.WriteAllText(path, text, encoding));
    public static void WriteAllLines(string path, IEnumerable<string> lines) => Execute(() => File.WriteAllLines(path, lines));
    public static void AppendAllText(string path, string text) => Execute(() => File.AppendAllText(path, text));
    public static void AppendAllText(string path, string text, Encoding encoding) => Execute(() => File.AppendAllText(path, text, encoding));
    public static void Move(string source, string destination, bool overwrite = false) => Execute(() => File.Move(source, destination, overwrite));
    public static Stream Create(string path) => CanPersist ? File.Create(path) : new MemoryStream();
    public static StreamWriter CreateText(string path) => CanPersist ? new StreamWriter(path) : new StreamWriter(new MemoryStream());
    public static WaveFileWriter CreateWave(string path, WaveFormat format) => CanPersist ? new WaveFileWriter(path, format) : new WaveFileWriter(new MemoryStream(), format);
}
