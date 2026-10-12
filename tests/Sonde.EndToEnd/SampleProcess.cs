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

    /// <summary>The runtime major CI asked the samples to run on (SONDE_E2E_RUNTIME, e.g. "10.0.x"), or null.</summary>
    public static int? RequestedRuntimeMajor
    {
        get
        {
            var requested = Environment.GetEnvironmentVariable("SONDE_E2E_RUNTIME");
            return !string.IsNullOrEmpty(requested) && int.TryParse(requested.Split('.')[0], out var major) ? major : null;
        }
    }

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
        start.ArgumentList.Add(Repo.SampleDll(sample));
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
        ApplyRuntimeSelection(start);
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
        ApplyRuntimeSelection(start);
        var launched = Start(start, endpoint, baseUrl);
        await launched.WaitReadyAsync(TimeSpan.FromMinutes(3));
        return launched;
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

    private static void ApplyRuntimeSelection(ProcessStartInfo start)
    {
        // The samples target net8.0 and roll forward by major. On a CI cell for .NET 9 or 10 the
        // 9 SDK's runtime is present too, so ask the host for the latest major to land on the
        // cell's runtime; the hello's runtime version is asserted against the request.
        if (RequestedRuntimeMajor is > 8)
            start.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
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
