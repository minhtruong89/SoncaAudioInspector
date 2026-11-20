using System.Text;

namespace SoncaAudioInspector;

// Offline stand-in for src/Infrastructure/ResultPersistencePolicy.cs.
// This harness compiles the measurement sources directly (no WPF, no app session), so the trial-config session that
// blocks persistence in the app does not exist here: a result may be written unless its own scope forbids it.
// Only the members the linked sources call are provided; a new call in those sources fails this build on purpose.
public static class ResultPersistencePolicy
{
    private static readonly AsyncLocal<bool?> Allowed = new();
    public static bool CanPersist => Allowed.Value ?? true;
    public static IDisposable BeginMeasurement() => BeginResult(CanPersist);
    public static IDisposable BeginResult(bool resultAllowed)
    {
        var previous = Allowed.Value;
        Allowed.Value = CanPersist && resultAllowed;
        return new Scope(() => Allowed.Value = previous);
    }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    public static void CreateDirectory(string path) { if (CanPersist) Directory.CreateDirectory(path); }
    public static void WriteAllText(string path, string text) { if (CanPersist) File.WriteAllText(path, text); }
    public static void WriteAllText(string path, string text, Encoding encoding) { if (CanPersist) File.WriteAllText(path, text, encoding); }
    public static void WriteAllLines(string path, IEnumerable<string> lines) { if (CanPersist) File.WriteAllLines(path, lines); }
}
