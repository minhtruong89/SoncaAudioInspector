using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Sonca.RewAudioInspector.Models;

namespace Sonca.RewAudioInspector.Services;

public sealed class RewApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;

    public string BaseUrl => _baseUrl;

    public RewApiClient(string baseUrl = "http://localhost:4735")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(8)
        };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }

    /// <summary>
    /// Checks whether REW REST API is alive.
    /// </summary>
    public async Task<bool> IsApiAliveAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync("/version", ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets REW version string (e.g. "5.40 Beta 135 API 0.9.8").
    /// </summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetFromJsonAsync<RewVersionResponse>("/version", _jsonOptions, ct);
            return res?.Message;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Tries to launch REW with -api argument if not currently running.
    /// </summary>
    public async Task<bool> EnsureRewRunningAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (await IsApiAliveAsync(ct))
        {
            return true;
        }

        string rewExePath = @"C:\Program Files\REW\roomeqwizard.exe";
        if (!File.Exists(rewExePath))
        {
            // Try standard x86 path or local AppData
            string rewX86 = @"C:\Program Files (x86)\REW\roomeqwizard.exe";
            if (File.Exists(rewX86)) rewExePath = rewX86;
        }

        if (File.Exists(rewExePath))
        {
            Console.WriteLine($"[REW API] Starting REW process: {rewExePath} -api ...");
            Process.Start(new ProcessStartInfo
            {
                FileName = rewExePath,
                Arguments = "-api",
                UseShellExecute = true
            });
        }
        else
        {
            Console.WriteLine($"[REW API] REW executable not found at '{rewExePath}'. Please launch REW manually with '-api'.");
        }

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            if (await IsApiAliveAsync(ct))
            {
                Console.WriteLine($"[REW API] Connected successfully to REW API at {_baseUrl}!");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sets Audio Sample Rate. REW accepts: 44100, 48000, 88200, 96000, 176400, 192000.
    /// Default standard is 44100.0 Hz (44.1 kHz).
    /// </summary>
    public async Task<(bool success, string message)> SetSampleRateAsync(double rateHz = 44100.0, CancellationToken ct = default)
    {
        try
        {
            var req = new RewValue<double> { Value = rateHz, Unit = "Hz" };
            var response = await _http.PostAsJsonAsync("/audio/samplerate", req, _jsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"Sample rate set to {rateHz} Hz: {content.Trim()}");
            }
            return (false, $"Failed to set sample rate {rateHz} Hz: {content.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Gets current Audio Sample Rate from REW.
    /// </summary>
    public async Task<RewValue<double>?> GetSampleRateAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<RewValue<double>>("/audio/samplerate", _jsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Configures REW RTA for standardized Spectrum analysis.
    /// Default: 64k FFT Length (65536 points), Hann window, Spectrum mode, calcDistortionEnabled: true.
    /// </summary>
    public async Task<(bool success, string message)> ConfigureRtaAsync(
        string fftLength = "64k",
        string window = "Hann",
        string mode = "Spectrum",
        string smoothing = "None",
        bool calcDistortion = true,
        CancellationToken ct = default)
    {
        try
        {
            var cfg = new RewRtaConfiguration
            {
                FftLength = fftLength,
                Window = window,
                Mode = mode,
                Smoothing = smoothing,
                CalcDistortionEnabled = calcDistortion,
                Use64BitFft = true,
                MaximumOverlap = "50%",
                Averaging = "None"
            };

            var response = await _http.PostAsJsonAsync("/rta/configuration", cfg, _jsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Configures RTA Distortion settings (unit: percent or dBc).
    /// </summary>
    public async Task<(bool success, string message)> ConfigureDistortionAsync(
        string unit = "percent",
        double lowPass = 20000,
        double highPass = 20,
        CancellationToken ct = default)
    {
        try
        {
            var cfg = new RewDistortionConfiguration
            {
                DistortionUnit = unit,
                LowPass = lowPass,
                HighPass = highPass,
                EnableLowPass = false,
                EnableHighPass = false
            };

            var response = await _http.PostAsJsonAsync("/rta/distortion-configuration", cfg, _jsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Sends command to RTA ("Start", "Stop", "Reset averaging").
    /// </summary>
    public async Task<(bool success, string message)> SendRtaCommandAsync(string command, CancellationToken ct = default)
    {
        try
        {
            var req = new RewCommand(command);
            var response = await _http.PostAsJsonAsync("/rta/command", req, _jsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<(bool success, string message)> StartRtaAsync(CancellationToken ct = default) => SendRtaCommandAsync("Start", ct);
    public Task<(bool success, string message)> StopRtaAsync(CancellationToken ct = default) => SendRtaCommandAsync("Stop", ct);
    public Task<(bool success, string message)> ResetAveragingAsync(CancellationToken ct = default) => SendRtaCommandAsync("Reset averaging", ct);

    /// <summary>
    /// Configures REW generator for Sine signal at specified frequency and level.
    /// </summary>
    public async Task<(bool success, string message)> ConfigureSineGeneratorAsync(
        double frequencyHz = 1000.0,
        double levelDbfs = -12.0,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Set signal to sine
            await _http.PostAsJsonAsync("/generator/signal", new { signal = "sine" }, _jsonOptions, ct);

            // 2. Set sine config
            var sineConfig = new RewSineConfig
            {
                Frequency = frequencyHz,
                LockFrequencyToRtaFft = true,
                AddDither = true,
                DitherBits = 16
            };
            await _http.PostAsJsonAsync("/generator/signals/sine/configuration", sineConfig, _jsonOptions, ct);

            // 3. Set generator level
            var levelReq = new RewValue<double> { Value = levelDbfs, Unit = "dBFS" };
            var levelRes = await _http.PostAsJsonAsync("/generator/level", levelReq, _jsonOptions, ct);
            var content = await levelRes.Content.ReadAsStringAsync(ct);

            return (levelRes.IsSuccessStatusCode, content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Sends command to Generator ("Play", "Stop").
    /// </summary>
    public async Task<(bool success, string message)> SendGeneratorCommandAsync(string command, CancellationToken ct = default)
    {
        try
        {
            var req = new RewCommand(command);
            var response = await _http.PostAsJsonAsync("/generator/command", req, _jsonOptions, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            return (response.IsSuccessStatusCode, content.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<(bool success, string message)> StartGeneratorAsync(CancellationToken ct = default) => SendGeneratorCommandAsync("Play", ct);
    public Task<(bool success, string message)> StopGeneratorAsync(CancellationToken ct = default) => SendGeneratorCommandAsync("Stop", ct);

    /// <summary>
    /// Retrieves current distortion calculation from REW (THD, THD+N, H2-H9, SNR).
    /// </summary>
    public async Task<RewDistortionResult?> GetDistortionAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<RewDistortionResult>>("/rta/distortion", _jsonOptions, ct);
            return list?.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Retrieves captured FFT spectrum data (magnitudes across frequency bins).
    /// </summary>
    public async Task<RewCapturedSpectrum?> GetCapturedSpectrumAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<RewCapturedSpectrum>("/rta/captured-data", _jsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Executes the standardized REW measurement cycle:
    /// 1. Verifies REW API connection
    /// 2. Sets Sample Rate to 44.1 kHz (44,100 Hz)
    /// 3. Sets FFT Length to 64k (65,536 points) in Spectrum mode with Hann window
    /// 4. Configures distortion calculation (percent)
    /// 5. (Optional) Plays Sine generator (e.g. 1000 Hz, -12 dBFS)
    /// 6. Starts RTA capture and settles
    /// 7. Retrieves REW distortion metrics and FFT spectrum
    /// 8. Stops RTA and generator
    /// </summary>
    public async Task<RewStandardMeasurementReport> RunStandardMeasurementAsync(
        double sineFreqHz = 1000.0,
        double sineLevelDbfs = -12.0,
        int settleTimeMs = 1500,
        bool useGenerator = true,
        CancellationToken ct = default)
    {
        var report = new RewStandardMeasurementReport
        {
            SampleRateHz = 44100.0,
            FftLength = "64k",
            Window = "Hann",
            Mode = "Spectrum",
            Timestamp = DateTime.Now
        };

        if (!await IsApiAliveAsync(ct))
        {
            report.StatusSummary = "REW API is not running on " + _baseUrl;
            return report;
        }

        report.RewEngineActive = true;

        // 1. Enforce 44.1 kHz sample rate
        await SetSampleRateAsync(44100.0, ct);

        // 2. Enforce 64k FFT Spectrum
        await ConfigureRtaAsync(fftLength: "64k", window: "Hann", mode: "Spectrum", calcDistortion: true, ct: ct);

        // 3. Enforce distortion configuration
        await ConfigureDistortionAsync("percent", ct: ct);

        // 4. Start generator if requested
        if (useGenerator)
        {
            await ConfigureSineGeneratorAsync(sineFreqHz, sineLevelDbfs, ct);
            await StartGeneratorAsync(ct);
        }

        try
        {
            // 5. Start RTA
            await StartRtaAsync(ct);

            // Wait for FFT window buffer to populate
            await Task.Delay(settleTimeMs, ct);

            // 6. Read Distortion metrics directly from REW
            var dist = await GetDistortionAsync(ct);
            if (dist != null)
            {
                report.FundamentalFreqHz = dist.FundamentalFrequency;
                report.FundamentaldBFS = dist.FundamentaldBFS;
                report.ThdPercent = dist.Thd;
                report.ThdPlusNPercent = dist.ThdPlusN;
                report.HarmonicsPercent = dist.ThdHarmonics;
                report.SnrdB = dist.SnrdB;
                report.Enob = dist.Enob;
            }

            // 7. Read FFT Spectrum data directly from REW
            var spectrum = await GetCapturedSpectrumAsync(ct);
            if (spectrum?.Magnitude != null && spectrum.Magnitude.Count > 0)
            {
                report.SpectrumPoints = spectrum.Magnitude.Count;
                report.FreqStepHz = spectrum.FreqStep;

                double maxMag = double.NegativeInfinity;
                int maxIdx = -1;
                for (int i = 0; i < spectrum.Magnitude.Count; i++)
                {
                    if (spectrum.Magnitude[i] > maxMag)
                    {
                        maxMag = spectrum.Magnitude[i];
                        maxIdx = i;
                    }
                }
                report.PeakMagnitudeDbfs = maxMag;
                report.PeakFrequencyHz = spectrum.StartFreq + (maxIdx * spectrum.FreqStep);
            }

            report.StatusSummary = dist?.Message ?? "Measurement completed successfully via REW REST API";
        }
        finally
        {
            // Always stop RTA and generator
            await StopRtaAsync(ct);
            if (useGenerator)
            {
                await StopGeneratorAsync(ct);
            }
        }

        return report;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
