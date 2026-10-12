using System.Net.Sockets;
using System.Text.Json.Nodes;
using Sonde.Probe.Protocol;

namespace Sonde;

/// <summary>Knobs for one eval.</summary>
public sealed record EvalOptions
{
    /// <summary>The default and clamp range for <see cref="TimeoutMs"/>.</summary>
    public const int DefaultTimeoutMs = 10_000;

    /// <summary>The shortest timeout accepted.</summary>
    public const int MinTimeoutMs = 100;

    /// <summary>The longest timeout accepted.</summary>
    public const int MaxTimeoutMs = 300_000;

    /// <summary>The deepest result walk accepted.</summary>
    public const int MaxMaxDepth = 32;

    /// <summary>How long to wait for the script before the probe answers <see cref="EvalOutcome.TimedOut"/>.</summary>
    public int TimeoutMs { get; init; } = DefaultTimeoutMs;

    /// <summary>How deep the result serialiser walks an object graph.</summary>
    public int MaxDepth { get; init; } = Probe.ResultJson.DefaultMaxDepth;

    internal int ClampedTimeoutMs => Math.Clamp(TimeoutMs, MinTimeoutMs, MaxTimeoutMs);
    internal int ClampedMaxDepth => Math.Clamp(MaxDepth, 1, MaxMaxDepth);
}

/// <summary>How an eval ended.</summary>
public enum EvalOutcome
{
    /// <summary>The script returned; <see cref="EvalResult.Result"/> holds its value.</summary>
    Completed,

    /// <summary>The script threw; <see cref="EvalResult.Exception"/> holds the rendered exception.</summary>
    ScriptThrew,

    /// <summary>The timeout tripped first. The script may still be running inside the target.</summary>
    TimedOut,

    /// <summary>The script did not compile; <see cref="EvalResult.Diagnostics"/> says why.</summary>
    CompileFailed,

    /// <summary>The probe is already running an eval; <see cref="EvalResult.BusySinceUtc"/> says since when.</summary>
    Busy,

    /// <summary>No probe answered at the endpoint; <see cref="EvalResult.Error"/> says what happened.</summary>
    Unreachable,

    /// <summary>The probe refused the request as malformed; <see cref="EvalResult.Error"/> says why.</summary>
    ProtocolError,
}

/// <summary>What came back from an eval, whichever way it ended.</summary>
public sealed record EvalResult
{
    /// <summary>How the eval ended.</summary>
    public EvalOutcome Outcome { get; init; }

    /// <summary>The script's return value, when <see cref="Outcome"/> is <see cref="EvalOutcome.Completed"/>.</summary>
    public JsonNode? Result { get; init; }

    /// <summary>Lines the script wrote with <c>g.Log</c>.</summary>
    public IReadOnlyList<string> Logs { get; init; } = [];

    /// <summary>The script's exception as text, when it threw.</summary>
    public string? Exception { get; init; }

    /// <summary>Wall time inside the target, when the script ran.</summary>
    public long ElapsedMs { get; init; }

    /// <summary>Collectible load contexts the target still holds.</summary>
    public int LiveContexts { get; init; }

    /// <summary>Compiler errors, when the script did not compile.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>When the running eval started, when the probe was busy.</summary>
    public DateTimeOffset? BusySinceUtc { get; init; }

    /// <summary>What went wrong, for <see cref="EvalOutcome.Unreachable"/> and <see cref="EvalOutcome.ProtocolError"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A client for one probe: hello, status, and the whole eval flow (hello, compile against what the
/// target has loaded, run, shape the answer). Stateless beyond the endpoint; create one per target.
/// </summary>
public sealed class SondeClient
{
    private readonly string _probeAssemblyPath;

    /// <summary>A client for the probe at <paramref name="endpoint"/> (a socket path or a pipe name).</summary>
    public SondeClient(string endpoint) : this(endpoint, null)
    {
    }

    /// <summary>A client that compiles against a specific probe assembly rather than the one beside this library.</summary>
    public SondeClient(string endpoint, string? probeAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        Endpoint = endpoint;
        _probeAssemblyPath = probeAssemblyPath ?? ProbeAssembly.Path;
    }

    /// <summary>The endpoint this client talks to.</summary>
    public string Endpoint { get; }

    /// <summary>Describes the target: runtime, process id, loaded assemblies, probe version.</summary>
    /// <exception cref="SondeUnreachableException">No probe answered.</exception>
    public async Task<HelloResponse> HelloAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ProbeClient.HelloAsync(Endpoint, cancellationToken).ConfigureAwait(false)
                ?? throw new SondeUnreachableException(Endpoint, "the probe closed without a hello reply", null);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw new SondeUnreachableException(Endpoint, ex.Message, ex);
        }
    }

    /// <summary>Reports the probe's state: running eval, live contexts, hooks and taps.</summary>
    /// <exception cref="SondeUnreachableException">No probe answered.</exception>
    public async Task<StatusResponse> StatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ProbeClient.StatusAsync(Endpoint, cancellationToken).ConfigureAwait(false)
                ?? throw new SondeUnreachableException(Endpoint, "the probe closed without a status reply", null);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            throw new SondeUnreachableException(Endpoint, ex.Message, ex);
        }
    }

    /// <summary>Compiles <paramref name="script"/> against the target and runs it. Never throws for
    /// an outcome the script or the target produced; those are in the result.</summary>
    public async Task<EvalResult> EvalAsync(string script, EvalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        options ??= new EvalOptions();

        HelloResponse hello;
        try
        {
            hello = await HelloAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SondeUnreachableException ex)
        {
            return new EvalResult { Outcome = EvalOutcome.Unreachable, Error = ex.Message };
        }

        var compiled = ScriptCompiler.Compile(script, hello.LoadedAssemblyPaths, hello.LoadedAssemblyNames, _probeAssemblyPath);
        if (!compiled.Success)
            return new EvalResult { Outcome = EvalOutcome.CompileFailed, Diagnostics = compiled.Diagnostics };

        var request = new ProbeRequest
        {
            Kind = ProbeRequestKind.Run,
            AssemblyBase64 = Convert.ToBase64String(compiled.Assembly.Span),
            EntryTypeName = ScriptCompiler.EntryTypeName,
            EntryMethodName = ScriptCompiler.EntryMethodName,
            TimeoutMs = options.ClampedTimeoutMs,
            MaxDepth = options.ClampedMaxDepth,
        };

        RunResponse? run;
        try
        {
            // Give the probe its timeout plus room to answer before giving up on the connection.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(options.ClampedTimeoutMs + 30_000);
            run = await ProbeClient.RunAsync(Endpoint, request, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransport(ex) && !cancellationToken.IsCancellationRequested)
        {
            return new EvalResult { Outcome = EvalOutcome.Unreachable, Error = $"lost the probe during the eval: {ex.Message}" };
        }
        if (run is null)
            return new EvalResult { Outcome = EvalOutcome.Unreachable, Error = "the probe closed without a result" };

        if (run.BusySinceUtc is { } since)
            return new EvalResult { Outcome = EvalOutcome.Busy, BusySinceUtc = since, LiveContexts = run.LiveContexts };
        if (run.ProtocolError is { } protocolError)
            return new EvalResult { Outcome = EvalOutcome.ProtocolError, Error = protocolError, LiveContexts = run.LiveContexts };

        return new EvalResult
        {
            Outcome = run.TimedOut ? EvalOutcome.TimedOut : run.Exception is not null ? EvalOutcome.ScriptThrew : EvalOutcome.Completed,
            Result = run.Result,
            Logs = run.Logs,
            Exception = run.Exception,
            ElapsedMs = run.ElapsedMs,
            LiveContexts = run.LiveContexts,
        };
    }

    private static bool IsTransport(Exception ex) =>
        ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException;
}

/// <summary>No probe answered at an endpoint.</summary>
public sealed class SondeUnreachableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SondeUnreachableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public SondeUnreachableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    public SondeUnreachableException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for <paramref name="endpoint"/>.</summary>
    public SondeUnreachableException(string endpoint, string reason, Exception? innerException)
        : base($"No probe answered at {endpoint}: {reason}. The process must be started with the probe (see `sonde env`).", innerException)
    {
        Endpoint = endpoint;
    }

    /// <summary>The endpoint that did not answer.</summary>
    public string? Endpoint { get; }
}
