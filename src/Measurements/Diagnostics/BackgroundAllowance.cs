using System.IO;
using System.Text.Json;

namespace SoncaAudioInspector;

/// <summary>
/// Station allowance for the Rè (Pink/Chirp) pre-playback background. The default is the factory gate
/// (−55 dBFS RMS, peak 0,05 FS); a poorly isolated test box may raise it up to the caps. The Pink/Chirp analyses keep
/// their 5 dB headroom above the RMS allowance, and the bass SNR / sensitivity limits from the signed config stay as they are.
/// </summary>
public sealed record BackgroundAllowance(double RmsDbFs = BackgroundAllowance.DefaultRmsDbFs, double PeakLimit = BackgroundAllowance.DefaultPeakLimit)
{
    public const double DefaultRmsDbFs = -55, MaxRmsDbFs = -35;
    public const double DefaultPeakLimit = .05, MaxPeakLimit = .316; // −26 / −10 dBFS
    public const string FileName = "rubbuzz-background-allowance.json";
    public static BackgroundAllowance Default { get; } = new();

    public double PeakDbFs => 20 * Math.Log10(PeakLimit);
    /// <summary>Upper background RMS accepted by the Pink/Chirp analyses: −50 dBFS at the default allowance.</summary>
    public double AnalysisRmsLimitDbFs => RmsDbFs + 5;
    public bool IsDefault => RmsDbFs == DefaultRmsDbFs && PeakLimit == DefaultPeakLimit;
    public string Describe => $"nền cho phép RMS ≤ {RmsDbFs:0.#} dBFS, đỉnh < {PeakDbFs:0.#} dBFS";

    public static double PeakFromDbFs(double dbFs) => Math.Pow(10, dbFs / 20);

    public void Validate()
    {
        if (!double.IsFinite(RmsDbFs) || RmsDbFs < DefaultRmsDbFs || RmsDbFs > MaxRmsDbFs
            || !double.IsFinite(PeakLimit) || PeakLimit < DefaultPeakLimit - 1e-12 || PeakLimit > MaxPeakLimit + 1e-12)
            throw new ArgumentException($"Nền cho phép: RMS từ {DefaultRmsDbFs:0} đến {MaxRmsDbFs:0} dBFS, đỉnh từ {20 * Math.Log10(DefaultPeakLimit):0} đến {20 * Math.Log10(MaxPeakLimit):0} dBFS.");
    }

    /// <summary>Missing or unreadable file → the factory gate (the stricter choice), never a crash.</summary>
    public static BackgroundAllowance Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var value = JsonSerializer.Deserialize<BackgroundAllowance>(File.ReadAllText(path));
            if (value == null) return Default;
            value.Validate();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return Default; }
    }

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Allowance that would accept this measurement: measured + 3 dB margin, never below the current one, capped.</summary>
    public BackgroundAllowance SuggestFor(BackgroundCheck check)
    {
        double rms = double.IsFinite(check.RmsDbFs) ? Math.Ceiling(check.RmsDbFs + 3) : RmsDbFs;
        double peakDb = double.IsFinite(check.PeakDbFs) ? Math.Ceiling(check.PeakDbFs + 3) : PeakDbFs;
        return new(Math.Clamp(Math.Max(RmsDbFs, rms), DefaultRmsDbFs, MaxRmsDbFs),
            Math.Clamp(Math.Max(PeakLimit, PeakFromDbFs(peakDb)), DefaultPeakLimit, MaxPeakLimit));
    }
}

/// <summary>A Rè background the gate refused; the message carries the measured numbers and the next step.</summary>
public sealed class RubBuzzBackgroundException(BackgroundCheck check) : InvalidOperationException(check.IsDigitalSilence
    ? $"Mic trả về im lặng số; chưa phát âm thanh — {check.Reason}."
    : $"Nền cao; chưa phát âm thanh — {check.Reason}. Chờ yên, đóng kín thùng cách âm hoặc tăng nền cho phép rồi đo lại.")
{
    public BackgroundCheck Check { get; } = check;
}
