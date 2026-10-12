namespace Sonde.Probe;

/// <summary>
/// Finds the Harmony build to load for this host. The probe ships Harmony's own per-runtime
/// builds under <c>harmony/net{8,9,10}.0/</c> beside itself; the one for the host's major wins,
/// else the newest below it, else a bare <c>0Harmony.dll</c> beside the probe.
/// </summary>
internal static class HarmonyLocator
{
    public const string FileName = "0Harmony.dll";
    public const string Folder = "harmony";

    /// <summary>The path to load for a host on <paramref name="runtimeMajor"/>, or null when nothing ships.</summary>
    public static string? Pick(string probeDirectory, int runtimeMajor, Func<string, bool> exists)
    {
        for (var major = runtimeMajor; major >= 8; major--)
        {
            var candidate = Path.Combine(probeDirectory, Folder, $"net{major}.0", FileName);
            if (exists(candidate))
                return candidate;
        }
        var beside = Path.Combine(probeDirectory, FileName);
        return exists(beside) ? beside : null;
    }

    public static string? PickForThisHost()
    {
        var directory = Path.GetDirectoryName(typeof(HarmonyLocator).Assembly.Location);
        return string.IsNullOrEmpty(directory) ? null : Pick(directory, Environment.Version.Major, File.Exists);
    }
}
