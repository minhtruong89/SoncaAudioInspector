using System.Text.Json.Serialization;

namespace Sonca.RewAudioInspector.Models;

/// <summary>
/// Generic REW value with unit (e.g. {"value": 44100.0, "unit": "Hz"}).
/// </summary>
public sealed class RewValue<T>
{
    [JsonPropertyName("value")]
    public T Value { get; set; } = default!;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;
}

public sealed class RewApiResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class RewVersionResponse
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public sealed class RewAudioStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; }
}

public sealed class RewAudioDriver
{
    [JsonPropertyName("driver")]
    public string Driver { get; set; } = string.Empty;
}

public sealed class RewCommand
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public List<string>? Parameters { get; set; }

    public RewCommand() { }

    public RewCommand(string command)
    {
        Command = command;
    }
}

/// <summary>
/// Mirrors REW RTA configuration endpoint: /rta/configuration
/// </summary>
public sealed class RewRtaConfiguration
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Spectrum"; // "Spectrum", "RTA 1/48", etc.

    [JsonPropertyName("smoothing")]
    public string Smoothing { get; set; } = "None";

    [JsonPropertyName("fftLength")]
    public string FftLength { get; set; } = "64k"; // "8k", "16k", "32k", "64k", "128k", etc.

    [JsonPropertyName("window")]
    public string Window { get; set; } = "Hann";

    [JsonPropertyName("averaging")]
    public string Averaging { get; set; } = "None"; // "None", "Linear", "Forever", etc.

    [JsonPropertyName("stopAt")]
    public bool StopAt { get; set; } = false;

    [JsonPropertyName("stopAtValue")]
    public int StopAtValue { get; set; } = 100;

    [JsonPropertyName("maximumOverlap")]
    public string MaximumOverlap { get; set; } = "50%";

    [JsonPropertyName("calcDistortionEnabled")]
    public bool CalcDistortionEnabled { get; set; } = true;

    [JsonPropertyName("restartCaptureOnGeneratorChange")]
    public bool RestartCaptureOnGeneratorChange { get; set; } = false;

    [JsonPropertyName("stopGeneratorWithRTA")]
    public bool StopGeneratorWithRta { get; set; } = false;

    [JsonPropertyName("use64BitFFT")]
    public bool Use64BitFft { get; set; } = true;

    [JsonPropertyName("adjustRTALevels")]
    public bool AdjustRtaLevels { get; set; } = false;

    [JsonPropertyName("fundamentalFromSineGen")]
    public bool FundamentalFromSineGen { get; set; } = true;
}

/// <summary>
/// Mirrors REW RTA Distortion configuration: /rta/distortion-configuration
/// </summary>
public sealed class RewDistortionConfiguration
{
    [JsonPropertyName("lowPass")]
    public double LowPass { get; set; } = 20000;

    [JsonPropertyName("highPass")]
    public double HighPass { get; set; } = 20;

    [JsonPropertyName("enableLowPass")]
    public bool EnableLowPass { get; set; } = false;

    [JsonPropertyName("enableHighPass")]
    public bool EnableHighPass { get; set; } = false;

    [JsonPropertyName("useManualFundamental")]
    public bool UseManualFundamental { get; set; } = false;

    [JsonPropertyName("manualFundamentalVrms")]
    public double ManualFundamentalVrms { get; set; } = 1.0;

    [JsonPropertyName("useAES17StandardNotch")]
    public bool UseAes17StandardNotch { get; set; } = false;

    [JsonPropertyName("showHarmonicPhase")]
    public bool ShowHarmonicPhase { get; set; } = false;

    [JsonPropertyName("highlightFundamental")]
    public bool HighlightFundamental { get; set; } = false;

    [JsonPropertyName("distortionUnit")]
    public string DistortionUnit { get; set; } = "percent"; // "percent" or "dBc"

    [JsonPropertyName("useCoherentAveraging")]
    public bool UseCoherentAveraging { get; set; } = false;

    [JsonPropertyName("useCrossCorrelationAveraging")]
    public bool UseCrossCorrelationAveraging { get; set; } = false;

    [JsonPropertyName("monitorClockRateMatch")]
    public bool MonitorClockRateMatch { get; set; } = true;

    [JsonPropertyName("showNoteName")]
    public bool ShowNoteName { get; set; } = false;
}

/// <summary>
/// Mirrors REW RTA Distortion reading: /rta/distortion
/// </summary>
public sealed class RewDistortionResult
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("nanotime")]
    public long? Nanotime { get; set; }

    [JsonPropertyName("totalSamplesProcessed")]
    public long? TotalSamplesProcessed { get; set; }

    [JsonPropertyName("fundamentalFrequency")]
    public double? FundamentalFrequency { get; set; }

    [JsonPropertyName("fundamentaldBFS")]
    public double? FundamentaldBFS { get; set; }

    [JsonPropertyName("fundamentalLevel")]
    public double? FundamentalLevel { get; set; }

    [JsonPropertyName("gaindB")]
    public double? GaindB { get; set; }

    [JsonPropertyName("thd")]
    public double? Thd { get; set; }

    [JsonPropertyName("thdPlusN")]
    public double? ThdPlusN { get; set; }

    [JsonPropertyName("nAndNHD")]
    public double? NAndNhd { get; set; }

    [JsonPropertyName("enob")]
    public double? Enob { get; set; }

    [JsonPropertyName("higherHarmonicDistortion")]
    public double? HigherHarmonicDistortion { get; set; }

    [JsonPropertyName("thdHarmonics")]
    public List<double>? ThdHarmonics { get; set; } // Harmonics d2, d3, d4... in % or dBc

    [JsonPropertyName("harmonics")]
    public List<double>? Harmonics { get; set; } // Harmonic peak levels in dBFS

    [JsonPropertyName("harmonicPhasesDegrees")]
    public List<double>? HarmonicPhasesDegrees { get; set; }

    [JsonPropertyName("snrdB")]
    public double? SnrdB { get; set; }

    [JsonPropertyName("d2Percent")]
    public double? D2Percent { get; set; }

    [JsonPropertyName("d3Percent")]
    public double? D3Percent { get; set; }
}

/// <summary>
/// Mirrors REW captured RTA FFT spectrum data: /rta/captured-data
/// </summary>
public sealed class RewCapturedSpectrum
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("startFreq")]
    public double StartFreq { get; set; }

    [JsonPropertyName("freqStep")]
    public double FreqStep { get; set; }

    [JsonPropertyName("ppo")]
    public int Ppo { get; set; }

    [JsonPropertyName("magnitude")]
    public List<double>? Magnitude { get; set; }

    [JsonPropertyName("phase")]
    public List<double>? Phase { get; set; }
}

/// <summary>
/// Generator sine configuration: /generator/signals/sine/configuration
/// </summary>
public sealed class RewSineConfig
{
    [JsonPropertyName("frequency")]
    public double Frequency { get; set; } = 1000.0;

    [JsonPropertyName("lockFrequencyToRTAFFT")]
    public bool LockFrequencyToRtaFft { get; set; } = true;

    [JsonPropertyName("addHarmonicDistortion")]
    public bool AddHarmonicDistortion { get; set; } = false;

    [JsonPropertyName("addDither")]
    public bool AddDither { get; set; } = true;

    [JsonPropertyName("ditherBits")]
    public int DitherBits { get; set; } = 16;
}

/// <summary>
/// Generator status: /generator/status
/// </summary>
public sealed class RewGeneratorStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("playing")]
    public bool Playing { get; set; }

    [JsonPropertyName("signal")]
    public string Signal { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public double Level { get; set; }

    [JsonPropertyName("levelUnit")]
    public string LevelUnit { get; set; } = string.Empty;
}

/// <summary>
/// Standardized Final Measurement Report
/// </summary>
public sealed class RewStandardMeasurementReport
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public double SampleRateHz { get; set; } = 44100.0;
    public string FftLength { get; set; } = "64k";
    public string Window { get; set; } = "Hann";
    public string Mode { get; set; } = "Spectrum";
    public bool RewEngineActive { get; set; }

    // Distortion
    public double? FundamentalFreqHz { get; set; }
    public double? FundamentaldBFS { get; set; }
    public double? ThdPercent { get; set; }
    public double? ThdPlusNPercent { get; set; }
    public List<double>? HarmonicsPercent { get; set; }
    public double? SnrdB { get; set; }
    public double? Enob { get; set; }

    // FFT Spectrum
    public int SpectrumPoints { get; set; }
    public double FreqStepHz { get; set; }
    public double PeakMagnitudeDbfs { get; set; }
    public double PeakFrequencyHz { get; set; }

    public string StatusSummary { get; set; } = string.Empty;
}
