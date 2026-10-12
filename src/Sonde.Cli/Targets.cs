namespace Sonde.Cli;

/// <summary>Turns what a person typed into an endpoint, and lists the endpoints that answer.</summary>
internal static class Targets
{
    /// <summary>A socket path or pipe name as given; otherwise the endpoint for a process of that name.</summary>
    public static string Resolve(string target)
    {
        if (OperatingSystem.IsWindows())
            return target.StartsWith("sonde-", StringComparison.Ordinal) || target.Contains('\\', StringComparison.Ordinal) ? target : Launch.EndpointFor(target);
        return target.Contains('/', StringComparison.Ordinal) || target.EndsWith(".sock", StringComparison.Ordinal) ? target : Launch.EndpointFor(target);
    }

    /// <summary>Every endpoint under the Sonde home directory, with whether a probe answers there.</summary>
    public static async Task<IReadOnlyList<(string Name, string Endpoint, bool Listening)>> ListAsync()
    {
        var results = new List<(string, string, bool)>();
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
                if (!name.StartsWith("sonde-", StringComparison.Ordinal))
                    continue;
                results.Add((name["sonde-".Length..], name, true));
            }
            return results;
        }

        if (!Directory.Exists(Launch.HomeDirectory))
            return results;
        foreach (var socket in Directory.GetFiles(Launch.HomeDirectory, "*.sock").Order(StringComparer.Ordinal))
        {
            var listening = await ProbeClient.IsListeningAsync(socket, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            results.Add((Path.GetFileNameWithoutExtension(socket), socket, listening));
        }
        return results;
    }
}
