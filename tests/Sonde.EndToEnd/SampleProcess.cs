using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sonde.EndToEnd;

/// <summary>
/// A sample launched as a real child process, with or without the probe, through the environment
/// route or through <c>dotnet watch</c>. Disposing kills the whole process tree.
/// </summary>
internal sealed class SampleProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private SampleProcess(Process process, string? endpoint, string? baseUrl)
    {
        _process = process;
        Endpoint = endpoint;
        BaseUrl = baseUrl;
    }

    public string? Endpoint { get; }
    public string? BaseUrl { get; }
    public int Pid => _process.Id;
    public Process Process => _process;
    public string StandardError
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>The runtime the samples are launched on: the one this test host runs on.</summary>
    public static int RuntimeMajor => Environment.Version.Major;

    /// <summary>Launches <c>dotnet &lt;sample&gt;.dll</c>. With <paramref name="probe"/> true the probe variables are set.</summary>
    public static async Task<SampleProcess> LaunchAsync(string sample, bool probe = true, string? endpoint = null, bool web = false, TimeSpan? readyTimeout = null)
    {
        // The working directory is the app's content root (where appsettings.json is), as a real launch would have it.
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Repo.SampleDll(sample)),
        };
        // `dotnet exec --runtimeconfig` pins the sample to this test host's runtime major, whatever
        // else the machine has installed; the hello's runtime version is asserted against it.
        foreach (var arg in new[] { "exec", "--runtimeconfig", PinnedRuntimeConfig(sample), Repo.SampleDll(sample) })
            start.ArgumentList.Add(arg);
        string? baseUrl = null;
        if (web)
        {
            baseUrl = $"http://127.0.0.1:{FreePort()}/";
            start.Environment["ASPNETCORE_URLS"] = baseUrl;
        }
        if (probe)
        {
            endpoint ??= NewEndpoint(sample);
            Launch.ApplyEnvironment(start.Environment!, endpoint);
        }
        var launched = Start(start, endpoint, baseUrl);
        await launched.WaitReadyAsync(readyTimeout ?? TimeSpan.FromSeconds(30));
        return launched;
    }

    /// <summary>Launches the sample under <c>dotnet watch run</c>, delivering the probe through the runtimeconfig template.</summary>
    public static async Task<SampleProcess> LaunchUnderWatchAsync(string sample, bool web)
    {
        var endpoint = NewEndpoint(sample + "-watch");
        var templateDir = Path.Combine(Path.GetTempPath(), "sonde-e2e-templates-" + Guid.NewGuid().ToString("N")[..8]);
        var template = Launch.WriteRuntimeConfigTemplate(templateDir, sample, null, Repo.SampleProject(sample))
            ?? throw new InvalidOperationException("could not write the runtimeconfig template");

        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Repo.SampleProject(sample) };
        foreach (var arg in new[] { "watch", "--non-interactive", "--no-hot-reload", "run", "-c", "E2EWatch" })
            start.ArgumentList.Add(arg);
        string? baseUrl = null;
        if (web)
        {
            baseUrl = $"http://127.0.0.1:{FreePort()}/";
            start.Environment["ASPNETCORE_URLS"] = baseUrl;
        }
        start.Environment["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "1";
        start.Environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        Launch.ApplyEnvironment(start.Environment!, endpoint, null, template);
        var launched = Start(start, endpoint, baseUrl);
        await launched.WaitReadyAsync(TimeSpan.FromMinutes(3));
        return launched;
    }

    /// <summary>Waits until <paramref name="text"/> appears in the process's output, or the timeout passes.</summary>
    public async Task<bool> WaitForOutputAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!StandardError.Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline || _process.HasExited)
                return StandardError.Contains(text, StringComparison.Ordinal);
            await Task.Delay(100);
        }
        return true;
    }

    public SondeClient Client => new(Endpoint ?? throw new InvalidOperationException("launched without the probe"));

    public async Task<string> GetAsync(string path)
    {
        using var http = new HttpClient();
        return await http.GetStringAsync(new Uri(BaseUrl + path.TrimStart('/')));
    }

    private static SampleProcess Start(ProcessStartInfo start, string? endpoint, string? baseUrl)
    {
        var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
        var launched = new SampleProcess(process, endpoint, baseUrl);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (launched._stderr)
            {
                launched._stderr.AppendLine(e.Data);
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (launched._stderr)
            {
                launched._stderr.AppendLine(e.Data);
            }
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return launched;
    }

    private async Task WaitReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        if (Endpoint is not null)
        {
            while (!await ProbeClient.IsListeningAsync(Endpoint, TimeSpan.FromMilliseconds(300)))
            {
                if (_process.HasExited)
                    throw new InvalidOperationException($"the sample exited with {_process.ExitCode} before its probe answered:\n{StandardError}");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"no probe at {Endpoint} after {timeout}:\n{StandardError}");
                await Task.Delay(100);
            }
        }
        if (BaseUrl is not null)
        {
            using var http = new HttpClient();
            while (true)
            {
                try
                {
                    await http.GetStringAsync(new Uri(BaseUrl));
                    break;
                }
                catch (HttpRequestException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
            }
        }
        else if (Endpoint is null)
        {
            // No probe and no web: give the host a moment to settle.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>The sample's own runtimeconfig with every framework pinned to this host's major and LatestPatch roll-forward.</summary>
    private static string PinnedRuntimeConfig(string sample)
    {
        var original = Path.ChangeExtension(Repo.SampleDll(sample), ".runtimeconfig.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(original))!.AsObject();
        var options = root["runtimeOptions"]!.AsObject();
        options["rollForward"] = "LatestPatch";
        var frameworks = options["frameworks"]?.AsArray();
        if (frameworks is null && options["framework"] is System.Text.Json.Nodes.JsonObject single)
        {
            options.Remove("framework");
            frameworks = [single];
            options["frameworks"] = frameworks;
        }
        foreach (var framework in frameworks!)
            framework!["version"] = $"{RuntimeMajor}.0.0";
        var pinned = Path.Combine(Path.GetTempPath(), $"sonde-e2e-{sample}-net{RuntimeMajor}.runtimeconfig.json");
        File.WriteAllText(pinned, root.ToJsonString());
        return pinned;
    }

    public static string NewEndpoint(string sample)
    {
        var name = sample + "-" + Guid.NewGuid().ToString("N")[..8];
        return OperatingSystem.IsWindows() ? "sonde-e2e-" + name : Path.Combine(Path.GetTempPath(), "sonde-e2e-" + name + ".sock");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            _process.Dispose();
            if (Endpoint is not null && !OperatingSystem.IsWindows())
                File.Delete(Endpoint);
        }
    }
}
