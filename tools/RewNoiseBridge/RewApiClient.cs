using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RewNoiseBridge;

internal sealed class RewApiClient : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("http://127.0.0.1:4735/"),
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<RewConnectionInfo> GetConnectionInfoAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage docResponse = await _http.GetAsync("doc.json", cancellationToken);
        docResponse.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await docResponse.Content.ReadAsStringAsync(cancellationToken));
        string apiVersion = doc.RootElement.GetProperty("info").GetProperty("version").GetString() ?? "?";

        AudioStatus status = await GetAsync<AudioStatus>("audio/status", cancellationToken);
        ValueText driver = await GetAsync<ValueText>("audio/driver", cancellationToken, "driver");
        ValueNumber sampleRate = await GetAsync<ValueNumber>("audio/samplerate", cancellationToken);
        ValueText inputDevice = await GetAsync<ValueText>("audio/java/input-device", cancellationToken, "device");
        ValueText input = await GetAsync<ValueText>("audio/java/input", cancellationToken, "input");

        using JsonDocument calibration = await GetDocumentAsync("audio/input-cal", cancellationToken);
        JsonElement root = calibration.RootElement;
        string selection = root.TryGetProperty("currentInputSelection", out JsonElement selected)
            ? selected.GetString() ?? "?"
            : "?";
        double? dBfsAt94 = null;
        if (root.TryGetProperty("calDataAllInputs", out JsonElement calData)
            && calData.TryGetProperty("dBFSAt94dBSPL", out JsonElement sensitivity)
            && sensitivity.TryGetDouble(out double parsed))
        {
            dBfsAt94 = parsed;
        }

        return new RewConnectionInfo(
            apiVersion,
            status.Enabled,
            status.Ready,
            driver.Value,
            sampleRate.Value,
            inputDevice.Value,
            input.Value,
            selection,
            dBfsAt94);
    }

    public Task<SplLevels> GetLevelsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SplLevels>("spl-meter/1/levels", cancellationToken);

    public async Task ConfigureMeterAsync(string weighting, string filter, CancellationToken cancellationToken = default)
    {
        var configuration = new
        {
            showSPL = true,
            showLeq = true,
            showSEL = false,
            splWeighting = weighting,
            leqWeighting = weighting,
            selWeighting = weighting,
            filter,
            highPassActive = false,
            rollingLeqActive = true,
            rollingLeqMinutes = 1
        };

        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "spl-meter/1/configuration", configuration, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendCommandAsync(string command, bool tolerateAlreadyOpen = false, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "spl-meter/1/command", new { command, parameters = Array.Empty<string>() }, JsonOptions, cancellationToken);
        if (tolerateAlreadyOpen && (int)response.StatusCode == 400) return;
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, string? alternateValueName = null)
    {
        using HttpResponseMessage response = await _http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (alternateValueName is not null)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            string value = document.RootElement.TryGetProperty(alternateValueName, out JsonElement element)
                ? element.GetString() ?? "?"
                : "?";
            return (T)(object)new ValueText(value);
        }
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"REW trả dữ liệu rỗng cho {path}.");
    }

    private async Task<JsonDocument> GetDocumentAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public void Dispose() => _http.Dispose();
}

internal sealed record RewConnectionInfo(
    string ApiVersion,
    bool AudioEnabled,
    bool AudioReady,
    string Driver,
    double SampleRate,
    string InputDevice,
    string Input,
    string CalibrationSelection,
    double? DbFsAt94DbSpl);

internal sealed record AudioStatus(bool Enabled, bool Ready);
internal sealed record ValueText(string Value);
internal sealed record ValueNumber(double Value);

internal sealed record SplLevels(
    int MeterNumber,
    string SplWeighting,
    string LeqWeighting,
    string SelWeighting,
    string Filter,
    double Spl,
    double Leq,
    bool IsRollingLeq,
    double RollingLeqMinutes,
    double Leq1m,
    double Leq10m,
    double Sel,
    double LcPeak,
    double LzPeak,
    double ElapsedTime);
