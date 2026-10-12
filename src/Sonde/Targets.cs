namespace Sonde;

/// <summary>An endpoint under the Sonde home directory and whether a probe answers there.</summary>
/// <param name="Name">The name the process was launched under.</param>
/// <param name="Endpoint">Its socket path or pipe name.</param>
/// <param name="Listening">True when a connect was accepted.</param>
public sealed record TargetInfo(string Name, string Endpoint, bool Listening);

/// <summary>Turns what a person or agent typed into an endpoint, and lists the endpoints that answer.</summary>
public static class Targets
{
    private const string PipePrefix = "sonde-";

    /// <summary>A socket path or pipe name as given; otherwise the endpoint for a process of that name (see <see cref="Launch.EndpointFor(string)"/>).</summary>
    public static string Resolve(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (OperatingSystem.IsWindows())
            return target.StartsWith(PipePrefix, StringComparison.Ordinal) || target.Contains('\\', StringComparison.Ordinal) ? target : Launch.EndpointFor(target);
        return target.Contains('/', StringComparison.Ordinal) || target.EndsWith(".sock", StringComparison.Ordinal) ? target : Launch.EndpointFor(target);
    }

    /// <summary>Every endpoint under <see cref="Launch.HomeDirectory"/>, with whether a probe answers at each.</summary>
    public static async Task<IReadOnlyList<TargetInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TargetInfo>();
        if (OperatingSystem.IsWindows())
        {
            string[] pipes;
            try
            {
                pipes = Directory.GetFiles(@"\\.\pipe\");
            }
            catch (IOException)
            {
                return results;
            }
            foreach (var pipe in pipes)
            {
                var name = Path.GetFileName(pipe);
                if (name.StartsWith(PipePrefix, StringComparison.Ordinal))
                    results.Add(new TargetInfo(name[PipePrefix.Length..], name, true));
            }
            return results;
        }

        if (!Directory.Exists(Launch.HomeDirectory))
            return results;
        foreach (var socket in Directory.GetFiles(Launch.HomeDirectory, "*.sock").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listening = await ProbeClient.IsListeningAsync(socket, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            results.Add(new TargetInfo(Path.GetFileNameWithoutExtension(socket), socket, listening));
        }
        return results;
    }
}
