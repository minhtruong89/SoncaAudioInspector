using System.IO;

namespace SoncaAudioInspector;

internal sealed class AudioEndpointInUseException(string endpoint, Exception inner)
    : IOException($"0x8889000A · {endpoint} đang bị giữ.", inner)
{
    public string Endpoint { get; } = endpoint;
}

public static class RubBuzzCaptureFallback
{
    public static async Task<T> RunAsync<T>(Func<bool, Task<T>> capture, Action<Exception> retrying, CancellationToken token)
    {
        try { return await capture(true); }
        catch (Exception ex) when (AudioSessionDiagnostics.IsDeviceInUse(ex))
        {
            token.ThrowIfCancellationRequested();
            retrying(ex);
            return await capture(false);
        }
    }

    public static string? BusyEndpoint(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is AudioEndpointInUseException endpoint) return endpoint.Endpoint;
        return null;
    }
}
