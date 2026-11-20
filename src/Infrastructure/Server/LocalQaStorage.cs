using System.IO;
using System.Text;
using System.Text.Json;

namespace SoncaAudioInspector;

public sealed record PendingAudioQaUpload(string Server, string StaffId, string ProductId, bool Passed,
    ServerEngine.AudioQaStepResult[] Steps, string[] GraphFiles, bool DeviceReady, string UploadSessionId,
    DateTimeOffset CreatedUtc, bool SafeToRetry = false, AudioQaConfiguration? Configuration = null, string? OperatorNote = null);

public static class LocalQaStorage
{
    public const int UploadedGraphRetentionDays = 30;
    private const string PendingName = ".pending-audio-qa.json";
    private const string CompleteName = ".uploaded-audio-qa.json";
    private static readonly object LogLock = new();
    public static string Root => RootFor(ResultPersistencePolicy.IsTrial);
    public static string RootFor(bool trial) => Path.Combine(AppContext.BaseDirectory, trial ? "test data" : "fail data");
    public static IEnumerable<string> Roots => new[] { RootFor(false), RootFor(true) };
    public static string RootOf(string path) => Roots.FirstOrDefault(root => IsSafeFolder(root, Path.GetDirectoryName(Path.GetFullPath(path))!)) ?? Root;
    private sealed record UploadedGraphs(DateTimeOffset UploadedUtc, string[] Files, string UploadSessionId);
    private const string RejectedName = ".rejected-audio-qa.json";
    private sealed record RejectedUpload(string UploadSessionId, DateTimeOffset RejectedUtc, string Reason);

    public static string? SavePending(PendingAudioQaUpload pending, IReadOnlyList<string> graphPaths, string? root = null)
    {
        return ResultPersistencePolicy.Execute<string?>(() =>
        {
        if (graphPaths.Count == 0
            || pending.Configuration?.Phase is { } phase && !string.Equals(phase, "AUTOTEST", StringComparison.OrdinalIgnoreCase)
            || pending.Configuration?.TrialStatus is { } trialStatus && trialStatus is not ("PASS" or "FAIL"))
            return null;
        string folder = Path.GetDirectoryName(Path.GetFullPath(graphPaths[0]))!;
        if (!IsSafeFolder(root ?? Root, folder) || pending.GraphFiles.Length != graphPaths.Count
            || !pending.GraphFiles.All(IsGraphName) || graphPaths.Any(path =>
            !File.Exists(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, StringComparison.OrdinalIgnoreCase))
            || !graphPaths.Select(Path.GetFileName).SequenceEqual(pending.GraphFiles, StringComparer.OrdinalIgnoreCase)) return null;
        string path = Path.Combine(folder, PendingName);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(pending), keepBackup: false);
        return path;

        }, null);
    }

    public static IEnumerable<string> FindPending(string root) => FindMarkers(root, PendingName);

    public static PendingAudioQaUpload? ReadPending(string root, string path)
    {
        if (!IsSafeFolder(root, Path.GetDirectoryName(Path.GetFullPath(path))!) || Path.GetFileName(path) != PendingName) return null;
        var pending = AtomicFile.ReadJson<PendingAudioQaUpload>(path);
        if (pending == null || pending.GraphFiles == null || pending.Steps == null
            || pending.GraphFiles.Length == 0 && pending.Configuration is not { IsTrial: true, TrialStatus: "INVALID" }
            || !pending.GraphFiles.All(IsGraphName)) return null;
        string rejectedPath = Path.Combine(Path.GetDirectoryName(path)!, RejectedName);
        if (File.Exists(rejectedPath))
        {
            var rejected = AtomicFile.ReadJson<RejectedUpload>(rejectedPath);
            if (rejected == null || rejected.UploadSessionId == pending.UploadSessionId) return null;
        }
        string completePath = Path.Combine(Path.GetDirectoryName(path)!, CompleteName);
        if (File.Exists(completePath))
        {
            var complete = AtomicFile.ReadJson<UploadedGraphs>(completePath);
            if (complete?.UploadSessionId == pending.UploadSessionId) return null;
        }
        return pending;
    }

    public static void RejectPending(string pendingPath, string reason)
    {
        if (!File.Exists(pendingPath) || Path.GetFileName(pendingPath) != PendingName
            || !Roots.Any(root => IsSafeFolder(root, Path.GetDirectoryName(Path.GetFullPath(pendingPath))!))) return;
        string folder = Path.GetDirectoryName(pendingPath)!;
        var pending = AtomicFile.ReadJson<PendingAudioQaUpload>(pendingPath);
        if (pending == null) return;
        AtomicFile.WriteAllText(Path.Combine(folder, RejectedName), JsonSerializer.Serialize(new RejectedUpload(pending.UploadSessionId, DateTimeOffset.UtcNow, reason)), keepBackup: false);
        try { File.Delete(pendingPath); }
        catch (IOException ex) { System.Diagnostics.Trace.WriteLine("QA đã đánh dấu từ chối, không retry: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { System.Diagnostics.Trace.WriteLine("QA đã đánh dấu từ chối, không retry: " + ex.Message); }
        System.Diagnostics.Trace.WriteLine(reason + " · removed pending QA: " + pendingPath);
    }
    public static void MarkRetrySafe(string pendingPath)
    {
        var pending = ReadPending(RootOf(pendingPath), pendingPath);
        if (pending != null) AtomicFile.WriteAllText(pendingPath,
            JsonSerializer.Serialize(pending with { SafeToRetry = true }), keepBackup: false);
    }

    public static void MarkUploaded(string root, string pendingPath, DateTimeOffset? now = null)
    {
        var pending = ReadPending(root, pendingPath);
        if (pending == null) return;
        AtomicFile.WriteAllText(Path.Combine(Path.GetDirectoryName(pendingPath)!, CompleteName),
            JsonSerializer.Serialize(new UploadedGraphs(now ?? DateTimeOffset.UtcNow, pending.GraphFiles, pending.UploadSessionId)), keepBackup: false);
        File.Delete(pendingPath);
    }

    // Only marked, confirmed uploads become eligible. Existing unmarked folders,
    // pending uploads, standards, calibrations and unrelated files are preserved.
    public static int PruneUploadedGraphs(string root, DateTimeOffset? now = null)
    {
        int removed = 0;
        DateTimeOffset cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-UploadedGraphRetentionDays);
        foreach (string marker in FindMarkers(root, CompleteName))
        {
            try
            {
                string folder = Path.GetDirectoryName(marker)!;
                if (File.Exists(Path.Combine(folder, PendingName)) || !IsSafeFolder(root, folder)) continue;
                var manifest = AtomicFile.ReadJson<UploadedGraphs>(marker);
                if (manifest == null || manifest.Files == null || manifest.UploadedUtc >= cutoff || !manifest.Files.All(IsGraphName)) continue;
                foreach (string name in manifest.Files)
                {
                    string image = Path.Combine(folder, name);
                    if (File.Exists(image) && (File.GetAttributes(image) & FileAttributes.ReparsePoint) == 0)
                    { File.Delete(image); removed++; }
                }
                File.Delete(marker);
                if (!Directory.EnumerateFileSystemEntries(folder).Any() && !string.Equals(Path.GetFullPath(root), folder, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(folder);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Lưu trữ ảnh QA: " + ex.Message); }
        }
        return removed;
    }

    public static void AppendRotatingLog(string path, string line)
    {
        ResultPersistencePolicy.Execute(() =>
        {
        lock (LogLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
            {
                for (int index = 3; index >= 1; index--)
                {
                    string destination = path + "." + index;
                    string source = index == 1 ? path : path + "." + (index - 1);
                    if (File.Exists(source)) File.Move(source, destination, overwrite: true);
                }
            }
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }

        });
    }

    private static bool IsGraphName(string name) => !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name) && (name.StartsWith("FRA-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-frequency-shift.png", StringComparison.OrdinalIgnoreCase))
        && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static IEnumerable<string> FindMarkers(string root, string name)
    {
        if (!Directory.Exists(root) || !IsSafeFolder(root, root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(root, name, new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true });
    }

    private static bool IsSafeFolder(string root, string folder)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(root, folder, StringComparison.OrdinalIgnoreCase)
            && !folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        for (string? current = folder; current != null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
