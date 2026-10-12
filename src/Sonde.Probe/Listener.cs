using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Sonde.Probe.Protocol;

namespace Sonde.Probe;

/// <summary>
/// Accepts connections on the endpoint and answers one request per connection. POSIX listens on a
/// Unix domain socket at the endpoint path; Windows on a named pipe. The transport is the only
/// platform-specific part of the probe. There is no network listener and never will be.
/// </summary>
internal static class Listener
{
    /// <summary>Serves until cancelled. Never throws: a probe that cannot bind logs and goes quiet.</summary>
    public static async Task ServeAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                await ServeNamedPipeAsync(endpoint, cancellationToken).ConfigureAwait(false);
            else
                await ServeUnixSocketAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"listener stopped: {ex}");
        }
    }

    private static async Task ServeUnixSocketAsync(string path, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(path))
        {
            // A socket file that still answers belongs to a live probe (the host's own, if this is a
            // child that got the endpoint by mistake). Leave it alone and go quiet; only a stale
            // file, left by a process that is gone, is replaced.
            if (await IsListeningAsync(path).ConfigureAwait(false))
            {
                Bootstrap.Log($"another probe already listens at {path}; this process will not bind it.");
                return;
            }
            File.Delete(path);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(16);

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = HandleConnectionAsync(new NetworkStream(connection, ownsSocket: true), cancellationToken);
        }
    }

    /// <summary>True when a connect to the Unix socket at <paramref name="path"/> is accepted within half a second.</summary>
    internal static async Task<bool> IsListeningAsync(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ServeNamedPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        // Windows lets a second process add server instances to a pipe name that another process
        // already serves, so the socket rule needs stating here too: a pipe that already exists
        // belongs to a live probe (the host's own, if this is a child that got the endpoint by
        // mistake). Leave it alone and go quiet.
        if (File.Exists(@"\\.\pipe\" + pipeName))
        {
            Bootstrap.Log($"another probe already listens on pipe {pipeName}; this process will not serve it.");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            _ = HandleConnectionAsync(server, cancellationToken);
        }
    }

    /// <summary>Reads one request from <paramref name="stream"/>, answers it, closes the stream.</summary>
    internal static async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var request = await Framing.ReadMessageAsync<ProbeRequest>(stream, cancellationToken).ConfigureAwait(false);
            if (request is null)
                return;

            object response = request.Kind switch
            {
                ProbeRequestKind.Hello => BuildHello(),
                ProbeRequestKind.Status => BuildStatus(),
                ProbeRequestKind.Run => await EvalRunner.RunAsync(request).ConfigureAwait(false),
                _ => new RunResponse { ProtocolError = $"Unknown request kind {request.Kind}." },
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, response.GetType(), Framing.Json);
            await Framing.WriteFrameAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Bootstrap.Log($"connection error: {ex.Message}");
        }
        finally
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already closed by the peer.
            }
        }
    }

    internal static HelloResponse BuildHello()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray();
        return new HelloResponse
        {
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            ProcessId = Environment.ProcessId,
            EntryAssembly = Assembly.GetEntryAssembly()?.GetName().Name,
            LoadedAssemblyPaths = loaded
                .Where(a => !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            LoadedAssemblyNames = loaded
                .Where(a => string.IsNullOrEmpty(a.Location))
                .Select(a => a.GetName().Name ?? "")
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            DiRootCaptured = SondeHost.DiRootCaptured,
            LiveContexts = SondeHost.LiveContexts,
            SondeVersion = SondeVersion,
        };
    }

    internal static StatusResponse BuildStatus() => new()
    {
        ProcessId = Environment.ProcessId,
        DiRootCaptured = SondeHost.DiRootCaptured,
        LiveContexts = SondeHost.LiveContexts,
        RunningEval = EvalRunner.RunningSince is { } since ? new RunningEvalInfo { StartedAtUtc = since } : null,
        Hooks = Hooks.Describe(),
        Taps = Taps.Names(),
    };

    /// <summary>The probe's informational version (<c>0.1.0</c>, or <c>0.1.0-dev</c> from a local build).</summary>
    internal static string SondeVersion =>
        typeof(Listener).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Listener).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
