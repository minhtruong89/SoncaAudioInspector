using System.Reflection;

namespace SoncaAudioInspector;

public static class AppVersion
{
    public const string BetaChannel = "beta";
    public static Version Current { get; } = Normalize(typeof(AppVersion).Assembly.GetName().Version!);
    public static string Display => Format(Current);
    /// <summary>Numeric version for servers, logs and backup names: "1.5.5", or "1.5.5.1" for a quick patch (sonca-quick-patch).</summary>
    public static string Numeric => Numbers(Current);
    public static string Numbers(Version value) { var v = Normalize(value); return v.Revision > 0 ? v.ToString(4) : v.ToString(3); }
    /// <summary>Release channel compiled into this build (csproj SoncaReleaseChannel → AssemblyMetadata); null = stable.</summary>
    public static string? Channel { get; } = ReadChannel(typeof(AppVersion).Assembly);
    public static bool IsBeta => Channel == BetaChannel;
    /// <summary>Version for people: "v1.4.4 beta" on a beta build. Version strings in feeds and assemblies stay numeric.</summary>
    public static string DisplayWithChannel => Display + (IsBeta ? " beta" : "");
    public static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    public static string Format(Version value)
    {
        var v = Normalize(value);
        return "v" + (v.Revision > 0 ? v.ToString(4) : v.Build > 0 ? v.ToString(3) : v.ToString(2));
    }
    public static string? ReadChannel(Assembly assembly)
    {
        string? value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SoncaReleaseChannel")?.Value?.Trim().ToLowerInvariant();
        return value == BetaChannel ? BetaChannel : null;
    }
}
