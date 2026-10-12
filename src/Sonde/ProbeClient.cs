using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Sonde.Probe.Protocol;

namespace Sonde;

/// <summary>
/// The client side of the wire protocol: one connection per request, a Unix domain socket on
/// POSIX, a named pipe on Windows. Compilation and result shaping live above this in
/// <see cref="SondeClient"/>; this only moves frames.
/// </summary>
public static class ProbeClient
{
    /// <summary>Asks the probe at <paramref name="endpoint"/> to describe its process.</summary>
    public static Task<HelloResponse?> HelloAsync(string endpoint, CancellationToken cancellationToken = default) =>
        ExchangeAsync<HelloResponse>(endpoint, new ProbeRequest { Kind = ProbeRequestKind.Hello }, cancellationToken);

    /// <summary>Asks the probe at <paramref name="endpoint"/> for its state.</summary>
    public static Task<StatusResponse?> StatusAsync(string endpoint, CancellationToken cancellationToken = default) =>
        ExchangeAsync<StatusResponse>(endpoint, new ProbeRequest { Kind = ProbeRequestKind.Status }, cancellationToken);

    /// <summary>Sends a compiled script to the probe at <paramref name="endpoint"/> and waits for its answer.</summary>
    public static Task<RunResponse?> RunAsync(string endpoint, ProbeRequest request, CancellationToken cancellationToken = default) =>
        ExchangeAsync<RunResponse>(endpoint, request, cancellationToken);

    /// <summary>True when something accepts a connection at <paramref name="endpoint"/> within <paramref name="timeout"/>.</summary>
    public static async Task<bool> IsListeningAsync(string endpoint, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var stream = await ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<TResponse?> ExchangeAsync<TResponse>(string endpoint, ProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        using var stream = await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await Framing.WriteMessageAsync(stream, request, cancellationToken).ConfigureAwait(false);
        return await Framing.ReadMessageAsync<TResponse>(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return await ConnectPipeAsync(endpoint, cancellationToken).ConfigureAwait(false);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<Stream> ConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return pipe;
    }
}
